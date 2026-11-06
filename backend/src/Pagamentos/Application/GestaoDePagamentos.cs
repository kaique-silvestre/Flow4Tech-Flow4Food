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
    /// Records a cash payment: the provider confirms it, the row is inserted, the cash is added to the shift (when there is one), the audit record and the event are written. The caller already
    /// computed <paramref name="aplicadoCentavos"/> from the balance it saw under the comanda version it then locked. Throws <see cref="TurnoEncerradoException"/> when the shift closed meanwhile.
    /// </summary>
    public Task<PagamentoRecebido> RegistrarAsync(IRequestContext request, Guid pagamentoId, Guid comandaId, Guid? turnoId, long recebidoCentavos, long aplicadoCentavos, long saldoAposCentavos, int comandaVersao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                var provedor = provedores.Single(p => p.Nome == ProvedorManual.NomeDoProvedor);
                if (!(await provedor.IniciarAsync(new SolicitacaoDePagamento(pagamentoId, comandaId, recebidoCentavos, aplicadoCentavos), ct)).Confirmado
                    && !(await provedor.ConfirmarAsync(pagamentoId, ct)).Confirmado)
                {
                    throw new InvalidOperationException("O provedor manual sempre confirma.");
                }

                var pagamento = new Pagamento(
                    pagamentoId,
                    org.UnitId,
                    comandaId,
                    turnoId,
                    RegrasDePagamento.Dinheiro,
                    recebidoCentavos,
                    aplicadoCentavos,
                    RegrasDePagamento.Troco(recebidoCentavos, aplicadoCentavos),
                    request.UserId,
                    RegrasDoInstante.Microssegundos(clock.UtcNow),
                    saldoAposCentavos,
                    comandaVersao);
                if (!await store.TentarInserirAsync(org.OrganizationId, pagamento, ct))
                {
                    throw new InvalidOperationException("Ja existe um pagamento com este id.");
                }

                if (turnoId is { } turno && !await caixa.RegistrarRecebimentoEmDinheiroAsync(org.OrganizationId, turno, aplicadoCentavos, ct))
                {
                    throw new TurnoEncerradoException();
                }

                var depois = new JsonObject
                {
                    ["comandaId"] = comandaId.ToString("D"),
                    ["metodo"] = pagamento.Metodo,
                    ["recebidoCentavos"] = recebidoCentavos,
                    ["aplicadoCentavos"] = aplicadoCentavos,
                    ["trocoCentavos"] = pagamento.TrocoCentavos,
                    ["saldoAposCentavos"] = saldoAposCentavos,
                };
                await audit.RecordAsync(request, new AuditEntry { Action = PagamentosAuditActions.Registrar, ObjectType = "pagamento", ObjectId = pagamentoId.ToString("D"), After = depois }, ct);
                await eventos.PublicarAsync(org, new PagamentoRegistrado(EntityId.New().Value, pagamentoId, comandaId, org.UnitId, turnoId, recebidoCentavos, aplicadoCentavos, pagamento.TrocoCentavos, request.UserId), ct);
                return PagamentoRecebido.De(pagamento);
            },
            cancellationToken);
    }
}

/// <summary>PostgreSQL keeps microseconds: an instant is cut to them BEFORE it is answered, so the first response and every repeat of it carry the very same value.</summary>
internal static class RegrasDoInstante
{
    public static DateTimeOffset Microssegundos(DateTimeOffset instante) => new(instante.Ticks - (instante.Ticks % 10), instante.Offset);
}
