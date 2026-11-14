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
