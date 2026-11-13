using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDoRecebimento
{
    Ok,

    /// <summary>The payment id was already stored: the stored result is answered and nothing is written.</summary>
    Repetida,

    Invalido,

    /// <summary>The caller may not do this (the route answers 403 with an empty body).</summary>
    Negado,

    ComandaNaoEncontrada,

    ComandaNaoAberta,

    /// <summary>The unit requires an open shift and the operator has none.</summary>
    CaixaFechado,

    /// <summary>Nothing left to pay.</summary>
    SemSaldo,

    VersaoDesatualizada,

    /// <summary>Closing needs the balance to be zero.</summary>
    SaldoPendente,

    TaxaJaRemovida,

    /// <summary>Removing the fee would put the bill below what was already received.</summary>
    ValorJaRecebido,

    /// <summary>A method of the receipt is deactivated by the organization.</summary>
    MetodoInativo,

    /// <summary>The non-cash lines pass the balance (only cash gives change), or a line would apply nothing.</summary>
    ExcedeSaldo,

    /// <summary>The lines paying a part of a split would pass what that part owes.</summary>
    ExcedeParte,

    /// <summary>The comanda changed after the split a line points to was recorded.</summary>
    DivisaoInvalida,
}

/// <summary><c>Versao</c> is the comanda version after the write (the one the round produced, on a repeat); <c>VersaoAtual</c> is set on a stale version; <c>Conta</c> is the bill after the write.</summary>
public sealed record ResultadoDoRecebimentoComConta(
    ResultadoDoRecebimento Resultado,
    PagamentoRecebido? Pagamento = null,
    ContaDaComanda? Conta = null,
    int? Versao = null,
    int? VersaoAtual = null,
    EstadoDaComanda? Estado = null,
    IReadOnlyList<PagamentoRecebido>? Pagamentos = null);

/// <summary>
/// The cashier's side of a comanda: seeing the bill, receiving cash, removing the service fee and closing. Each write is ONE unit of work: the version bump, the rows, the shift movement (through the
/// public contract of Pagamentos, which uses the one of Caixa), the audit record and the outbox event commit or roll back together, and a stale version changes nothing. The route has already asked the
/// engine for the route permission; payments are additionally gated by <c>pagamento:registrar</c> here.
/// </summary>
public sealed class GestaoDoRecebimento(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IRodadasStore rodadas,
    IDescontosStore descontos,
    IDivisoesStore divisoes,
    GestaoDePagamentos pagamentos,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    IClock clock)
{
    /// <summary>The bill of a comanda the caller may see (their own, or any with <c>comanda:ver-comandas-de-outros</c>); null otherwise.</summary>
    public Task<ResultadoDoRecebimentoComConta> ContaAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoEncontrada);
                }

                return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Ok, Conta: await CarregarContaAsync(request, comanda, ct), Versao: comanda.Versao, Estado: comanda.Estado);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<PagamentoRecebido>?> PagamentosAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<PagamentoRecebido>?>(
            request,
            async ct =>
            {
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return null;
                }

                return await pagamentos.ListarDaComandaAsync(request, comandaId, ct);
            },
            cancellationToken);
    }

    /// <summary>The single cash payment (the original route): one cash line, no tip, no split.</summary>
    public async Task<ResultadoDoRecebimentoComConta> ReceberAsync(IRequestContext request, Guid comandaId, Guid pagamentoId, long recebidoCentavos, int versao, CancellationToken cancellationToken = default)
    {
        var r = await ReceberLinhasAsync(request, comandaId, versao, [new LinhaDoRecebimento(pagamentoId, null, recebidoCentavos, 0, null, null)], cancellationToken);
        return r.Pagamentos is { Count: 1 } ? r with { Pagamento = r.Pagamentos[0], Pagamentos = null } : r;
    }

    /// <summary>
    /// A partial, split or mixed payment: one or more method lines of one payer, in ONE unit of work (the permission decisions, the shift, the version bump, the rows, the audit and the events). Each line
    /// carries its own client id; repeating them answers what was stored and writes nothing. More than one line, or a line paying a part of a split, also needs <c>pagamento:dividir</c>; a tip needs
    /// <c>pagamento:registrar-gorjeta</c>. The applied total never passes the balance, and the version bump is the lock that keeps two simultaneous receipts from passing it together.
    /// </summary>
    public async Task<ResultadoDoRecebimentoComConta> ReceberLinhasAsync(IRequestContext request, Guid comandaId, int versao, IReadOnlyList<LinhaDoRecebimento> linhas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(linhas);
        try
        {
            return await unitOfWork.ExecuteAsync(request, ct => ReceberDentroAsync(request, comandaId, versao, linhas, ct), cancellationToken);
        }
        catch (TurnoEncerradoException)
        {
            // The shift closed between the check and the movement: everything of this payment rolled back.
            return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.CaixaFechado);
        }
    }

    private async Task<ResultadoDoRecebimentoComConta> ReceberDentroAsync(IRequestContext request, Guid comandaId, int versao, IReadOnlyList<LinhaDoRecebimento> linhas, CancellationToken ct)
    {
        if (versao < 1 || linhas.Count is < 1 or > 10 || linhas.Any(l => l.PagamentoId == Guid.Empty || (l.DivisaoId is null) != (l.DivisaoParte is null) || l.DivisaoParte < 1) || linhas.Select(l => l.PagamentoId).Distinct().Count() != linhas.Count
            || linhas.Select(l => (l.DivisaoId, l.DivisaoParte)).Distinct().Count() != 1)
        {
            return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Invalido);
        }

        if (!await permissoes.PodeAsync(request, PagamentosPermissions.Registrar, ct)
            || ((linhas.Count > 1 || linhas.Any(l => l.DivisaoId is not null)) && !await permissoes.PodeAsync(request, PagamentosPermissions.Dividir, ct))
            || (linhas.Any(l => l.GorjetaCentavos != 0) && !await permissoes.PodeAsync(request, PagamentosPermissions.RegistrarGorjeta, ct)))
        {
            return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Negado);
        }

        var org = request.Organization;
        var comanda = await AcharVisivelAsync(request, comandaId, ct);
        if (comanda is null)
        {
            return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoEncontrada);
        }

        if (await GravadosAsync(request, comandaId, linhas, ct) is { } gravados)
        {
            return gravados;
        }

        if (comanda.Estado != EstadoDaComanda.Aberta)
        {
            return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoAberta);
        }

        var contexto = await pagamentos.PrepararAsync(request, ct);
        if (contexto.CaixaExigido && contexto.TurnoId is null)
        {
            return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.CaixaFechado);
        }

        var conta = await CarregarContaAsync(request, comanda, ct);
        if (conta.SaldoCentavos <= 0)
        {
            return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.SemSaldo);
        }

        // A receipt that pays a part of a split pays only that part: its balance is what the part still owes (when that is less than the bill's).
        var (recusaDaParte, restanteDaParte) = await ParteAsync(request, comanda, linhas[0], ct);
        if (recusaDaParte is { } recusada)
        {
            return new ResultadoDoRecebimentoComConta(recusada);
        }

        var saldoEfetivo = Math.Min(conta.SaldoCentavos, restanteDaParte ?? long.MaxValue);
        var plano = await pagamentos.PlanejarAsync(request, saldoEfetivo, linhas, ct);
        if (plano.Recusa != RecusaDoPlano.Nenhuma)
        {
            return new ResultadoDoRecebimentoComConta(plano.Recusa switch
            {
                RecusaDoPlano.MetodoInativo => ResultadoDoRecebimento.MetodoInativo,
                RecusaDoPlano.ExcedeSaldo => restanteDaParte < conta.SaldoCentavos ? ResultadoDoRecebimento.ExcedeParte : ResultadoDoRecebimento.ExcedeSaldo,
                _ => ResultadoDoRecebimento.Invalido,
            });
        }

        if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
        {
            // Lost the race on the row lock (or the caller is stale). A concurrent repeat of THESE payment ids is not a conflict: it gets the stored result.
            return await GravadosAsync(request, comandaId, linhas, ct) ?? await FalhaDeVersaoAsync(org, comandaId, ct);
        }

        var novaVersao = versao + 1;
        var registrados = await pagamentos.RegistrarAsync(request, comandaId, contexto.TurnoId, conta.SaldoCentavos, novaVersao, plano.Linhas, ct);
        var depois = RegrasDeComanda.Conta(conta.ConsumoCentavos, conta.TaxaServicoPercentual, conta.TaxaServicoRemovida, conta.PagoCentavos + plano.Linhas.Sum(l => l.AplicadoCentavos), conta.DescontoCentavos);
        return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Ok, Conta: depois, Versao: novaVersao, Estado: comanda.Estado, Pagamentos: registrados);
    }

    /// <summary>The payments already stored under these client ids: all of them (answered as a repeat), none (null, go on) or only some or another comanda's (invalid).</summary>
    private async Task<ResultadoDoRecebimentoComConta?> GravadosAsync(IRequestContext request, Guid comandaId, IReadOnlyList<LinhaDoRecebimento> linhas, CancellationToken ct)
    {
        var achados = new List<PagamentoRecebido>(linhas.Count);
        foreach (var linha in linhas)
        {
            if (await pagamentos.AcharAsync(request, linha.PagamentoId, ct) is { } existente)
            {
                achados.Add(existente);
            }
        }

        if (achados.Count == 0)
        {
            return null;
        }

        return achados.Count == linhas.Count && achados.All(a => a.ComandaId == comandaId)
            ? new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Repetida, Versao: achados.Max(a => a.ComandaVersao), Pagamentos: achados)
            : new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Invalido);
    }

    /// <summary>
    /// The part of a split the receipt pays (all its lines point to the same one): the split must be of this comanda and still match it, the part must exist, and it must still owe something. Returns what the
    /// part still owes (its total minus what was applied to it), or null when the receipt pays no part.
    /// </summary>
    private async Task<(ResultadoDoRecebimento? Recusa, long? Restante)> ParteAsync(IRequestContext request, Comanda comanda, LinhaDoRecebimento linha, CancellationToken ct)
    {
        if (linha.DivisaoId is not { } divisaoId || linha.DivisaoParte is not { } numero)
        {
            return (null, null);
        }

        var org = request.Organization;
        var divisao = await divisoes.AcharAsync(org.OrganizationId, divisaoId, ct);
        var parte = divisao?.Partes.FirstOrDefault(p => p.Numero == numero);
        if (divisao is null || divisao.ComandaId != comanda.Id || parte is null)
        {
            return (ResultadoDoRecebimento.Invalido, null);
        }

        var itens = await rodadas.ListarItensAsync(org.OrganizationId, comanda.Id, ct);
        var dados = await descontos.ListarAsync(org.OrganizationId, comanda.Id, ct);
        if (divisao.Assinatura != RegrasDeDivisao.Assinatura(itens, dados, comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida))
        {
            return (ResultadoDoRecebimento.DivisaoInvalida, null);
        }

        var jaPago = (await pagamentos.SomaAplicadaPorParteAsync(request, comanda.Id, ct)).GetValueOrDefault((divisaoId, numero));
        var restante = parte.TotalCentavos - jaPago;
        return restante <= 0 ? (ResultadoDoRecebimento.ExcedeParte, null) : (null, restante);
    }

    public Task<ResultadoDoRecebimentoComConta> RemoverTaxaServicoAsync(IRequestContext request, Guid comandaId, int versao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (versao < 1)
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Invalido);
                }

                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoEncontrada);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoAberta);
                }

                if (comanda.TaxaServicoRemovida)
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.TaxaJaRemovida);
                }

                var antes = await CarregarContaAsync(request, comanda, ct);
                var depois = RegrasDeComanda.Conta(antes.ConsumoCentavos, antes.TaxaServicoPercentual, true, antes.PagoCentavos, antes.DescontoCentavos);
                if (depois.SaldoCentavos < 0)
                {
                    // Removing the fee can only lower the bill, never below what was already paid (that would be a refund, outside this slice).
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ValorJaRecebido);
                }

                if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                {
                    return await FalhaDeVersaoAsync(org, comandaId, ct);
                }

                if (!await comandas.RemoverTaxaServicoAsync(org.OrganizationId, comandaId, ct))
                {
                    throw new InvalidOperationException("A taxa mudou durante a remocao.");
                }

                var json = new JsonObject
                {
                    ["comandaNumero"] = comanda.Numero,
                    ["taxaServicoPercentual"] = antes.TaxaServicoPercentual,
                    ["taxaServicoCentavos"] = antes.TaxaServicoCentavos,
                    ["totalAntesCentavos"] = antes.TotalCentavos,
                    ["totalDepoisCentavos"] = depois.TotalCentavos,
                    ["versao"] = versao + 1,
                };
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.RemoverTaxaServico, ObjectType = "comanda", ObjectId = comandaId.ToString("D"), After = json }, ct);
                await eventos.PublicarAsync(org, new TaxaServicoRemovida(EntityId.New().Value, comandaId, comanda.UnidadeId, comanda.Numero, antes.TaxaServicoPercentual, antes.TaxaServicoCentavos, request.UserId), ct);
                return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Ok, Conta: depois, Versao: versao + 1, Estado: comanda.Estado);
            },
            cancellationToken);
    }

    public Task<ResultadoDoRecebimentoComConta> FecharAsync(IRequestContext request, Guid comandaId, int versao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (versao < 1)
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Invalido);
                }

                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoEncontrada);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoAberta);
                }

                var conta = await CarregarContaAsync(request, comanda, ct);
                if (!RegrasDeComanda.PodeFechar(conta))
                {
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.SaldoPendente, Conta: conta, Versao: comanda.Versao);
                }

                if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                {
                    return await FalhaDeVersaoAsync(org, comandaId, ct);
                }

                var quando = RegrasDeRodada.Microssegundos(clock.UtcNow);
                if (!await comandas.FecharAsync(org.OrganizationId, comandaId, quando, ct))
                {
                    throw new InvalidOperationException("A comanda mudou durante o fechamento.");
                }

                var json = new JsonObject { ["comandaNumero"] = comanda.Numero, ["totalCentavos"] = conta.TotalCentavos, ["pagoCentavos"] = conta.PagoCentavos, ["versao"] = versao + 1 };
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.FecharComanda, ObjectType = "comanda", ObjectId = comandaId.ToString("D"), After = json }, ct);
                await eventos.PublicarAsync(org, new ComandaFechada(EntityId.New().Value, comandaId, comanda.UnidadeId, comanda.Numero, conta.TotalCentavos, request.UserId), ct);
                return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Ok, Conta: conta, Versao: versao + 1, Estado: EstadoDaComanda.Fechada);
            },
            cancellationToken);
    }

    /// <summary>The comanda when the caller may see it (their own, or any with <c>comanda:ver-comandas-de-outros</c>); null otherwise, so paying, closing and removing the fee answer as for a comanda that does not exist.</summary>
    private async Task<Comanda?> AcharVisivelAsync(IRequestContext request, Guid comandaId, CancellationToken ct)
    {
        var org = request.Organization;
        var comanda = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
        if (comanda is null || comanda.GarcomId == request.UserId)
        {
            return comanda;
        }

        return await permissoes.PodeAsync(request, ComandaPermissions.VerComandasDeOutros, ct) ? comanda : null;
    }

    private async Task<ContaDaComanda> CarregarContaAsync(IRequestContext request, Comanda comanda, CancellationToken ct)
    {
        var itens = await rodadas.ListarItensAsync(request.Organization.OrganizationId, comanda.Id, ct);
        var pago = await pagamentos.SomaAplicadaAsync(request, comanda.Id, ct);
        var dados = await descontos.ListarAsync(request.Organization.OrganizationId, comanda.Id, ct);
        return RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens), comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida, pago, RegrasDeDesconto.Calcular(itens, dados).TotalCentavos);
    }

    private async Task<ResultadoDoRecebimentoComConta> FalhaDeVersaoAsync(OrganizationContext org, Guid comandaId, CancellationToken ct)
    {
        var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
        return atual is null || atual.Estado != EstadoDaComanda.Aberta
            ? new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoAberta)
            : new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.VersaoDesatualizada, VersaoAtual: atual.Versao);
    }
}
