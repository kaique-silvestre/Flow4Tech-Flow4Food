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
}

/// <summary><c>Versao</c> is the comanda version after the write (the one the round produced, on a repeat); <c>VersaoAtual</c> is set on a stale version; <c>Conta</c> is the bill after the write.</summary>
public sealed record ResultadoDoRecebimentoComConta(ResultadoDoRecebimento Resultado, PagamentoRecebido? Pagamento = null, ContaDaComanda? Conta = null, int? Versao = null, int? VersaoAtual = null, EstadoDaComanda? Estado = null);

/// <summary>
/// The cashier's side of a comanda: seeing the bill, receiving cash, removing the service fee and closing. Each write is ONE unit of work: the version bump, the rows, the shift movement (through the
/// public contract of Pagamentos, which uses the one of Caixa), the audit record and the outbox event commit or roll back together, and a stale version changes nothing. The route has already asked the
/// engine for the route permission; payments are additionally gated by <c>pagamento:registrar</c> here.
/// </summary>
public sealed class GestaoDoRecebimento(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IRodadasStore rodadas,
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

    public async Task<ResultadoDoRecebimentoComConta> ReceberAsync(IRequestContext request, Guid comandaId, Guid pagamentoId, long recebidoCentavos, int versao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    if (pagamentoId == Guid.Empty || versao < 1 || !RegrasDeRecebimento.RecebidoValido(recebidoCentavos))
                    {
                        return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Invalido);
                    }

                    if (!await permissoes.PodeAsync(request, PagamentosPermissions.Registrar, ct))
                    {
                        return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Negado);
                    }

                    var org = request.Organization;
                    var comanda = await AcharVisivelAsync(request, comandaId, ct);
                    if (comanda is null)
                    {
                        return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoEncontrada);
                    }

                    if (await pagamentos.AcharAsync(request, pagamentoId, ct) is { } existente)
                    {
                        return Repetido(existente, comandaId);
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

                    if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                    {
                        // Lost the race on the row lock (or the caller is stale). A concurrent repeat of THIS payment id is not a conflict: it gets the stored result.
                        return await pagamentos.AcharAsync(request, pagamentoId, ct) is { } agora ? Repetido(agora, comandaId) : await FalhaDeVersaoAsync(org, comandaId, ct);
                    }

                    var aplicado = RegrasDeRecebimento.Aplicado(recebidoCentavos, conta.SaldoCentavos);
                    var novaVersao = versao + 1;
                    var pagamento = await pagamentos.RegistrarAsync(request, pagamentoId, comandaId, contexto.TurnoId, recebidoCentavos, aplicado, conta.SaldoCentavos - aplicado, novaVersao, ct);
                    var depois = RegrasDeComanda.Conta(conta.ConsumoCentavos, conta.TaxaServicoPercentual, conta.TaxaServicoRemovida, conta.PagoCentavos + aplicado);
                    return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Ok, pagamento, depois, novaVersao, Estado: comanda.Estado);
                },
                cancellationToken);
        }
        catch (TurnoEncerradoException)
        {
            // The shift closed between the check and the movement: everything of this payment rolled back.
            return new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.CaixaFechado);
        }
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
                var depois = RegrasDeComanda.Conta(antes.ConsumoCentavos, antes.TaxaServicoPercentual, true, antes.PagoCentavos);
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

    private static ResultadoDoRecebimentoComConta Repetido(PagamentoRecebido existente, Guid comandaId) =>
        existente.ComandaId == comandaId
            ? new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Repetida, existente, Versao: existente.ComandaVersao)
            : new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.Invalido);

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
        return RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens), comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida, pago);
    }

    private async Task<ResultadoDoRecebimentoComConta> FalhaDeVersaoAsync(OrganizationContext org, Guid comandaId, CancellationToken ct)
    {
        var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
        return atual is null || atual.Estado != EstadoDaComanda.Aberta
            ? new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.ComandaNaoAberta)
            : new ResultadoDoRecebimentoComConta(ResultadoDoRecebimento.VersaoDesatualizada, VersaoAtual: atual.Versao);
    }
}
