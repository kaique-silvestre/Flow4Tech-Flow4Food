using System.Text.Json.Nodes;
using Flow4Tech.Caixa.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Pagamentos.Domain;

namespace Flow4Tech.Pagamentos.Application;

/// <summary>Raised inside the unit of work when the shift closed between the check and the movement: everything of the payment rolls back and the caller answers that the register is closed.</summary>
public sealed class TurnoEncerradoException : InvalidOperationException
{
    public TurnoEncerradoException()
        : base("O turno de caixa foi encerrado durante o recebimento.")
    {
    }

    public TurnoEncerradoException(string message)
        : base(message)
    {
    }

    public TurnoEncerradoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Whether the unit requires an open shift for cash and which shift the operator has open (null when none, or when the unit does not require one).</summary>
public sealed record ContextoDeRecebimento(bool CaixaExigido, Guid? TurnoId);

/// <summary>
/// Public contract of Pagamentos for the comanda use case. Every method runs inside the CALLER's unit of work (the comanda use case owns it), so the payment row, the movement in the shift, the
/// audit record and the event commit or roll back together with the comanda change. The cash enters the shift through the public contract of Caixa.
/// </summary>
public sealed class GestaoDePagamentos(
    IUnitOfWork unitOfWork,
    IPagamentosStore store,
    IEstornosStore estornos,
    IDecisaoDeEstorno decisaoDeEstorno,
    IMetodosDePagamentoStore metodos,
    IMovimentosDoCaixa caixa,
    IAuditLog audit,
    IEventosDePagamentos eventos,
    ISettings settings,
    IClock clock,
    IEnumerable<IProvedorDePagamento> provedores)
{
    /// <summary>Reads the unit's cash rule and the operator's open shift. With the rule off the payment has no shift and counts in none.</summary>
    public Task<ContextoDeRecebimento> PrepararAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                var exigido = (await settings.ResolveAsync(CaixaSettings.ExigirCaixaAberto, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).AsBoolean();
                var turno = exigido ? await caixa.TurnoAbertoAsync(org.OrganizationId, org.UnitId, request.UserId, ct) : null;
                return new ContextoDeRecebimento(exigido, turno);
            },
            cancellationToken);
    }

    public Task<PagamentoRecebido?> AcharAsync(IRequestContext request, Guid pagamentoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct => await store.AcharAsync(request.Organization.OrganizationId, pagamentoId, ct) is { } p ? PagamentoRecebido.De(p) : null,
            cancellationToken);
    }

    public Task<long> SomaAplicadaAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.SomaAplicadaAsync(request.Organization.OrganizationId, comandaId, ct), cancellationToken);
    }

    public Task<IReadOnlyList<PagamentoRecebido>> ListarDaComandaAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<PagamentoRecebido>>(
            request,
            async ct => (await store.ListarDaComandaAsync(request.Organization.OrganizationId, comandaId, ct)).Select(PagamentoRecebido.De).ToList(),
            cancellationToken);
    }

    /// <summary>
    /// Resolves the method of every line (an unknown one or another organization's is invalid, an inactive one is refused) and shares the balance among them with the pure rule. Reads only; the caller
    /// then writes with <see cref="RegistrarAsync"/> once it holds the comanda version.
    /// </summary>
    public Task<PlanoDeRecebimento> PlanejarAsync(IRequestContext request, long saldoCentavos, IReadOnlyList<LinhaDoRecebimento> linhas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(linhas);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                await metodos.GarantirDeFabricaAsync(org.OrganizationId, clock.UtcNow, ct);
                var resolvidos = new List<MetodoDePagamento>(linhas.Count);
                foreach (var linha in linhas)
                {
                    var metodo = linha.MetodoId is { } id ? await metodos.AcharAsync(org.OrganizationId, id, ct) : await metodos.AcharDinheiroAsync(org.OrganizationId, ct);
                    if (metodo is null)
                    {
                        return new PlanoDeRecebimento(RecusaDoPlano.Invalido, []);
                    }

                    resolvidos.Add(metodo);
                }

                if (resolvidos.Any(m => !m.Ativo))
                {
                    return new PlanoDeRecebimento(RecusaDoPlano.MetodoInativo, []);
                }

                var rateio = RegrasDeRecebimentoMisto.Ratear(saldoCentavos, linhas.Select((l, i) => new LinhaDeRecebimento(resolvidos[i].Tipo == TipoDeMetodo.Dinheiro, l.RecebidoCentavos, l.GorjetaCentavos)).ToList());
                if (rateio.Recusa != RecusaDeRecebimento.Nenhuma)
                {
                    return new PlanoDeRecebimento(rateio.Recusa == RecusaDeRecebimento.ExcedeSaldo ? RecusaDoPlano.ExcedeSaldo : RecusaDoPlano.Invalido, []);
                }

                return new PlanoDeRecebimento(RecusaDoPlano.Nenhuma, linhas.Select((l, i) => new LinhaPlanejada(l, MetodoDoSalao.De(resolvidos[i]), rateio.Linhas[i].AplicadoCentavos, rateio.Linhas[i].TrocoCentavos)).ToList());
            },
            cancellationToken);
    }

    /// <summary>
    /// Records the planned lines of one receipt: for each line the provider confirms it, the row is inserted, the audit record and the event are written; the cash lines together are added to the shift
    /// (when there is one), the other methods move no cash. The caller already planned the lines from the balance it saw under the comanda version it then locked; <paramref name="saldoCentavos"/> is that
    /// balance (each line stores what is left after it). Throws <see cref="TurnoEncerradoException"/> when the shift closed meanwhile.
    /// </summary>
    public Task<IReadOnlyList<PagamentoRecebido>> RegistrarAsync(IRequestContext request, Guid comandaId, Guid? turnoId, long saldoCentavos, int comandaVersao, IReadOnlyList<LinhaPlanejada> linhas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(linhas);
        return unitOfWork.ExecuteAsync<IReadOnlyList<PagamentoRecebido>>(
            request,
            async ct =>
            {
                var org = request.Organization;
                var provedor = provedores.Single(p => p.Nome == ProvedorManual.NomeDoProvedor);
                var gravados = new List<PagamentoRecebido>(linhas.Count);
                var saldo = saldoCentavos;
                var dinheiro = 0L;
                foreach (var linha in linhas)
                {
                    var pedido = linha.Pedido;
                    var recebido = pedido.RecebidoCentavos;
                    if (!(await provedor.IniciarAsync(new SolicitacaoDePagamento(pedido.PagamentoId, comandaId, recebido, linha.AplicadoCentavos), ct)).Confirmado
                        && !(await provedor.ConfirmarAsync(pedido.PagamentoId, ct)).Confirmado)
                    {
                        throw new InvalidOperationException("O provedor manual sempre confirma.");
                    }

                    saldo -= linha.AplicadoCentavos;
                    var pagamento = new Pagamento(
                        pedido.PagamentoId,
                        org.UnitId,
                        comandaId,
                        turnoId,
                        RegrasDeMetodo.ChaveDoPagamento(linha.Metodo.Chave),
                        recebido,
                        linha.AplicadoCentavos,
                        linha.TrocoCentavos,
                        request.UserId,
                        RegrasDoInstante.Microssegundos(clock.UtcNow),
                        saldo,
                        comandaVersao,
                        linha.Metodo.Id,
                        linha.Metodo.Nome,
                        pedido.GorjetaCentavos,
                        EstadoDoPagamento.Confirmado,
                        pedido.DivisaoId,
                        pedido.DivisaoParte);
                    if (!await store.TentarInserirAsync(org.OrganizationId, pagamento, ct))
                    {
                        throw new InvalidOperationException("Ja existe um pagamento com este id.");
                    }

                    dinheiro += linha.Metodo.Dinheiro ? linha.AplicadoCentavos : 0;
                    var depois = new JsonObject
                    {
                        ["comandaId"] = comandaId.ToString("D"),
                        ["metodo"] = pagamento.Metodo,
                        ["metodoNome"] = pagamento.MetodoNome,
                        ["recebidoCentavos"] = recebido,
                        ["aplicadoCentavos"] = linha.AplicadoCentavos,
                        ["trocoCentavos"] = pagamento.TrocoCentavos,
                        ["gorjetaCentavos"] = pagamento.GorjetaCentavos,
                        ["saldoAposCentavos"] = saldo,
                    };
                    if (pedido.DivisaoId is { } divisao)
                    {
                        depois["divisaoId"] = divisao.ToString("D");
                        depois["divisaoParte"] = pedido.DivisaoParte;
                    }

                    await audit.RecordAsync(request, new AuditEntry { Action = PagamentosAuditActions.Registrar, ObjectType = "pagamento", ObjectId = pedido.PagamentoId.ToString("D"), After = depois }, ct);
                    await eventos.PublicarAsync(org, new PagamentoRegistrado(EntityId.New().Value, pedido.PagamentoId, comandaId, org.UnitId, turnoId, recebido, linha.AplicadoCentavos, pagamento.TrocoCentavos, request.UserId, pagamento.Metodo, pagamento.GorjetaCentavos, pedido.DivisaoId, pedido.DivisaoParte), ct);
                    gravados.Add(PagamentoRecebido.De(pagamento));
                }

                // Every payment of a shift takes its row lock, cash or not (zero cents for a card or Pix): the closing waits for it and then counts it, and a closed shift refuses it.
                if (turnoId is { } turno && !await caixa.RegistrarRecebimentoEmDinheiroAsync(org.OrganizationId, turno, dinheiro, ct))
                {
                    throw new TurnoEncerradoException();
                }

                return gravados;
            },
            cancellationToken);
    }

    /// <summary>
    /// Refunds <paramref name="valorCentavos"/> of a payment (all that is left when null) with a reason. Writes a NEGATIVE entry; the payment is never edited. The limit (<c>pagamento:estornar</c>, by value and
    /// daily cap) is asked of the engine here, with the cents the server measured, after the shift movement and before the write; a refusal rolls everything back, is audited on its own transaction
    /// (<c>acesso:negar</c>) and answered by the caller as 403.
    /// </summary>
    public async Task<ResultadoDeEstorno> EstornarAsync(IRequestContext request, Guid pagamentoId, long? valorCentavos, string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The permission comes BEFORE every validation (reason, existence, balance, shift): whoever holds no refund rule learns nothing about the payment.
        if (!await TemRegraDeEstornoAsync(request, cancellationToken))
        {
            await RegistrarNegacaoAsync(request, "pagamento", pagamentoId, cancellationToken);
            return new ResultadoDeEstorno(ResultadoDoEstorno.Negado, []);
        }

        try
        {
            return await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    if (!RegrasDeEstorno.TentarMotivo(motivo, out var texto) || valorCentavos is < 1)
                    {
                        return new ResultadoDeEstorno(ResultadoDoEstorno.Invalido, []);
                    }

                    var pagamento = await store.AcharAsync(request.Organization.OrganizationId, pagamentoId, ct);
                    if (pagamento is null || pagamento.UnidadeId != request.Organization.UnitId)
                    {
                        return new ResultadoDeEstorno(ResultadoDoEstorno.NaoEncontrado, []);
                    }

                    return await ExecutarEstornoAsync(request, [(pagamento, valorCentavos)], texto, false, ct);
                },
                cancellationToken);
        }
        catch (DecisaoDeEstornoNegadaException)
        {
            await RegistrarNegacaoAsync(request, "pagamento", pagamentoId, cancellationToken);
            return new ResultadoDeEstorno(ResultadoDoEstorno.Negado, []);
        }
        catch (EstornoRecusadoException recusa)
        {
            return new ResultadoDeEstorno(recusa.Resultado, []);
        }
    }

    /// <summary>
    /// Refunds, in the CALLER's unit of work, everything still refundable of every payment of the comanda (cancelling a comanda that was paid). The engine is asked ONCE, with the total, so the limit is
    /// the limit of one operation. A refusal throws <see cref="DecisaoDeEstornoNegadaException"/> (the caller rolls back and audits); any other failure comes back as a value and wrote nothing.
    /// </summary>
    public async Task<ResultadoDeEstorno> EstornarComandaAsync(IRequestContext request, Guid comandaId, string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RegrasDeEstorno.TentarMotivo(motivo, out var texto))
        {
            return new ResultadoDeEstorno(ResultadoDoEstorno.Invalido, []);
        }

        var org = request.Organization;
        var pagamentos = await store.ListarDaComandaAsync(org.OrganizationId, comandaId, cancellationToken);
        var feitos = await estornos.SomaEstornadaPorPagamentoAsync(org.OrganizationId, comandaId, cancellationToken);
        var pedidos = pagamentos
            .Where(p => p.Estado == EstadoDoPagamento.Confirmado && RegrasDeEstorno.SaldoEstornavel(p.AplicadoCentavos, feitos.GetValueOrDefault(p.Id)) > 0)
            .Select(p => (p, (long?)null))
            .ToList();
        return pedidos.Count == 0 ? new ResultadoDeEstorno(ResultadoDoEstorno.Ok, []) : await ExecutarEstornoAsync(request, pedidos, texto, true, cancellationToken);
    }

    /// <summary>A payment with every refund it has, for the caller who may see it; null when it is not of the session's unit.</summary>
    public Task<EstornosDoPagamento?> ListarEstornosAsync(IRequestContext request, Guid pagamentoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<EstornosDoPagamento?>(
            request,
            async ct =>
            {
                var org = request.Organization;
                var pagamento = await store.AcharAsync(org.OrganizationId, pagamentoId, ct);
                if (pagamento is null || pagamento.UnidadeId != org.UnitId)
                {
                    return null;
                }

                var feitos = await estornos.ListarDoPagamentoAsync(org.OrganizationId, pagamentoId, ct);
                var estornado = -feitos.Sum(e => e.ValorCentavos);
                return new EstornosDoPagamento(PagamentoRecebido.De(pagamento), estornado, RegrasDeEstorno.SaldoEstornavel(pagamento.AplicadoCentavos, estornado), [.. feitos.Select(EstornoFeito.De)]);
            },
            cancellationToken);
    }

    /// <summary>
    /// Does the caller hold ANY rule of <c>pagamento:estornar</c>? Asks the engine for the smallest possible operation (zero cents): allowed exactly when some rule applies, whatever its limit. It runs in its own
    /// unit of work, rolled back at the end, so a daily cap is never reserved. Each real refund is still decided with the real cents.
    /// </summary>
    private async Task<bool> TemRegraDeEstornoAsync(IRequestContext request, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.ExecuteAsync<object?>(
                request,
                async ct => throw new ConsultaDeRegraDeEstornoConcluidaException(await decisaoDeEstorno.PodeAsync(request, 0, ct)),
                cancellationToken);
        }
        catch (ConsultaDeRegraDeEstornoConcluidaException feito)
        {
            return feito.Resultado;
        }

        throw new InvalidOperationException("The question must end by rolling back.");
    }

    /// <summary>
    /// Plans every refund first (lock of the payment, refundable balance, shift), moves the shift for each, asks the engine once with the total and only then writes. Nothing is written before the engine
    /// answers, and a failure that comes back as a value happens before any write that would survive (the shift movement is the only earlier write and is rolled back by the caller's failure path).
    /// </summary>
    private async Task<ResultadoDeEstorno> ExecutarEstornoAsync(IRequestContext request, List<(Pagamento Pagamento, long? Pedido)> pedidos, string motivo, bool ignorarQuitados, CancellationToken ct)
    {
        var org = request.Organization;
        var exigido = (await settings.ResolveAsync(CaixaSettings.ExigirCaixaAberto, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).AsBoolean();
        var plano = new List<(Pagamento Pagamento, long Valor, Guid? Turno, long SaldoApos)>(pedidos.Count);
        foreach (var (pagamento, pedido) in pedidos)
        {
            if (pagamento.Estado != EstadoDoPagamento.Confirmado)
            {
                return new ResultadoDeEstorno(ResultadoDoEstorno.NaoConfirmado, []);
            }

            await estornos.TravarPagamentoAsync(org.OrganizationId, pagamento.Id, ct);
            var saldo = RegrasDeEstorno.SaldoEstornavel(pagamento.AplicadoCentavos, await estornos.SomaEstornadaAsync(org.OrganizationId, pagamento.Id, ct));
            if (ignorarQuitados && pedido is null && saldo == 0)
            {
                // A refund that committed between the listing of the comanda and the lock of this payment already took everything: nothing is left to hand back here.
                continue;
            }

            if (RegrasDeEstorno.Valor(pedido, saldo) is not { } valor)
            {
                return new ResultadoDeEstorno(ResultadoDoEstorno.ExcedeSaldoEstornavel, [], saldo);
            }

            var turno = pagamento.TurnoId is { } original && await caixa.TurnoEstaAbertoAsync(org.OrganizationId, original, ct)
                ? original
                : exigido ? await caixa.TurnoAbertoAsync(org.OrganizationId, org.UnitId, request.UserId, ct) : null;
            if (exigido && turno is null)
            {
                return new ResultadoDeEstorno(ResultadoDoEstorno.SemTurnoAberto, []);
            }

            plano.Add((pagamento, valor, turno, saldo - valor));
        }

        if (plano.Count == 0)
        {
            return new ResultadoDeEstorno(ResultadoDoEstorno.Ok, []);
        }

        foreach (var (pagamento, valor, turno, _) in plano)
        {
            if (turno is { } t)
            {
                var dinheiro = pagamento.Metodo == RegrasDePagamento.Dinheiro ? valor : 0;
                var movimento = await caixa.RegistrarEstornoEmDinheiroAsync(org.OrganizationId, t, dinheiro, ct);
                if (movimento != ResultadoDoEstornoNoCaixa.Ok)
                {
                    // Earlier movements of this loop were written: the caller's unit of work must not commit them, so the failure rolls everything back.
                    throw new EstornoRecusadoException(movimento == ResultadoDoEstornoNoCaixa.SemDinheiro ? ResultadoDoEstorno.CaixaSemDinheiro : ResultadoDoEstorno.TurnoEncerrado);
                }
            }
        }

        // The question comes after the shift lock and measures the SERVER's number: the sum of what is about to be handed back.
        if (!await decisaoDeEstorno.PodeAsync(request, plano.Sum(p => p.Valor), ct))
        {
            throw new DecisaoDeEstornoNegadaException();
        }

        var feitos = new List<EstornoFeito>(plano.Count);
        foreach (var (pagamento, valor, turno, saldoApos) in plano)
        {
            var estorno = new Estorno(EntityId.New().Value, pagamento.UnidadeId, pagamento.Id, pagamento.ComandaId, turno, pagamento.MetodoId, pagamento.Metodo, -valor, motivo, request.UserId, RegrasDoInstante.Microssegundos(clock.UtcNow));
            await estornos.InserirAsync(org.OrganizationId, estorno, ct);
            var depois = new JsonObject
            {
                ["comandaId"] = pagamento.ComandaId.ToString("D"),
                ["estornoId"] = estorno.Id.ToString("D"),
                ["valorCentavos"] = estorno.ValorCentavos,
                ["motivo"] = motivo,
                ["turnoId"] = turno?.ToString("D"),
                ["saldoEstornavelAposCentavos"] = saldoApos,
            };
            await audit.RecordAsync(request, new AuditEntry { Action = PagamentosAuditActions.Estornar, ObjectType = "pagamento", ObjectId = pagamento.Id.ToString("D"), After = depois }, ct);
            await eventos.PublicarAsync(org, new PagamentoEstornado(EntityId.New().Value, estorno.Id, pagamento.Id, pagamento.ComandaId, pagamento.UnidadeId, turno, estorno.ValorCentavos, motivo, request.UserId), ct);
            feitos.Add(EstornoFeito.De(estorno));
        }

        return new ResultadoDeEstorno(ResultadoDoEstorno.Ok, feitos, plano.Count == 1 ? plano[0].SaldoApos : 0);
    }

    /// <summary>A refusal by the engine is a security fact: written on its own transaction after the unit of work rolled back (<c>acesso:negar</c>, result <c>negado</c>, the permission only, never the reason).</summary>
    public Task RegistrarNegacaoAsync(IRequestContext request, string tipoDoObjeto, Guid objetoId, CancellationToken cancellationToken = default) =>
        audit.RecordDetachedAsync(
            request,
            new AuditEntry
            {
                Action = PlatformAuditActions.AcessoNegar,
                Result = AuditResult.Negado,
                ObjectType = tipoDoObjeto,
                ObjectId = objetoId.ToString("D"),
                After = new JsonObject { ["permissao"] = PagamentosPermissions.Estornar },
            },
            cancellationToken);

    /// <summary>Cents applied so far to each part of a recorded split of the comanda.</summary>
    public Task<IReadOnlyDictionary<(Guid DivisaoId, int Parte), long>> SomaAplicadaPorParteAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.SomaAplicadaPorParteAsync(request.Organization.OrganizationId, comandaId, ct), cancellationToken);
    }
}

/// <summary>PostgreSQL keeps microseconds: an instant is cut to them BEFORE it is answered, so the first response and every repeat of it carry the very same value.</summary>
internal static class RegrasDoInstante
{
    public static DateTimeOffset Microssegundos(DateTimeOffset instante) => new(instante.Ticks - (instante.Ticks % 10), instante.Offset);
}
