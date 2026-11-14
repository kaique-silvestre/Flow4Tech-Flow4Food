using System.Text.Json.Nodes;
using Flow4Tech.Caixa.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Caixa.Application;

/// <summary>
/// Supply (money in) and withdrawal (money out) of the caller's OWN open shift. The route has asked for <c>caixa:ver</c>; the LIMITED permission (<c>caixa:suprimento</c>, <c>caixa:sangria</c>) is asked of the
/// engine here, with the amount, after the shift row is locked, so a refusal rolls back everything, a daily cap included (403, no reason; a refused withdrawal is also audited on its own transaction, a refused supply is not).
/// A withdrawal never takes more than the drawer holds. Each movement is one append-only row, an audit record and an outbox event in the same unit of work.
/// </summary>
public sealed class GestaoDeMovimentosDoCaixa(IUnitOfWork unitOfWork, ICaixaStore store, IDecisaoDeCaixa decisao, IAuditLog audit, IEventosDoCaixa eventos, IClock clock)
{
    public Task<ResultadoDoMovimento> SuprirAsync(IRequestContext request, long valorCentavos, string? motivo, string? destino, CancellationToken cancellationToken = default) =>
        RegistrarAsync(request, TipoDeMovimento.Suprimento, valorCentavos, motivo, destino, cancellationToken);

    public Task<ResultadoDoMovimento> SangrarAsync(IRequestContext request, long valorCentavos, string? motivo, string? destino, CancellationToken cancellationToken = default) =>
        RegistrarAsync(request, TipoDeMovimento.Sangria, valorCentavos, motivo, destino, cancellationToken);

    /// <summary>The movements of the caller's open shift, oldest first; empty without one.</summary>
    public Task<IReadOnlyList<MovimentoDoCaixa>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                return await store.AcharAbertoAsync(org.OrganizationId, org.UnitId, request.UserId, ct) is { } turno
                    ? await store.ListarMovimentosAsync(org.OrganizationId, turno.Id, ct)
                    : [];
            },
            cancellationToken);
    }

    private async Task<ResultadoDoMovimento> RegistrarAsync(IRequestContext request, TipoDeMovimento tipo, long valorCentavos, string? motivo, string? destino, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sangria = tipo == TipoDeMovimento.Sangria;
        var permissao = sangria ? CaixaPermissions.Sangria : CaixaPermissions.Suprimento;
        try
        {
            return await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    var destinoLimpo = (string?)null;
                    var destinoPedido = !string.IsNullOrWhiteSpace(destino);
                    if (!RegrasDoCaixa.MovimentoValido(valorCentavos)
                        || !RegrasDoCaixa.TextoValido(motivo, RegrasDoCaixa.MotivoMaximo, out var motivoLimpo)
                        || ((sangria || destinoPedido) && !RegrasDoCaixa.TextoValido(destino, RegrasDoCaixa.DestinoMaximo, out destinoLimpo)))
                    {
                        return new ResultadoDoMovimento(ResultadoDoCaixa.Invalido);
                    }

                    var org = request.Organization;
                    if (await store.AcharAbertoAsync(org.OrganizationId, org.UnitId, request.UserId, ct) is not { } turno)
                    {
                        return new ResultadoDoMovimento(ResultadoDoCaixa.SemTurnoAberto);
                    }

                    // The row lock serializes this against closing and against other movements of the shift.
                    if (await store.SaldoDaGavetaTravandoAsync(org.OrganizationId, turno.Id, ct) is not { } saldo)
                    {
                        return new ResultadoDoMovimento(ResultadoDoCaixa.SemTurnoAberto);
                    }

                    if (sangria && !RegrasDoCaixa.PodeSangrar(saldo, valorCentavos))
                    {
                        return new ResultadoDoMovimento(ResultadoDoCaixa.SaldoInsuficiente);
                    }

                    if (!await decisao.PodeAsync(request, permissao, valorCentavos, ct))
                    {
                        throw new DecisaoDeCaixaNegadaException();
                    }

                    var movimento = new MovimentoDoCaixa(EntityId.New().Value, turno.Id, turno.UnidadeId, tipo, valorCentavos, motivoLimpo!, destinoLimpo, request.UserId, clock.UtcNow);
                    await store.InserirMovimentoAsync(org.OrganizationId, movimento, ct);
                    var depois = new JsonObject
                    {
                        ["turnoId"] = turno.Id.ToString("D"),
                        ["valorCentavos"] = valorCentavos,
                        ["motivo"] = movimento.Motivo,
                        ["destino"] = movimento.Destino,
                        ["saldoApos"] = sangria ? saldo - valorCentavos : saldo + valorCentavos,
                    };
                    await audit.RecordAsync(request, new AuditEntry { Action = sangria ? CaixaAuditActions.Sangria : CaixaAuditActions.Suprimento, ObjectType = "turno-caixa", ObjectId = turno.Id.ToString("D"), After = depois }, ct);
                    IDomainEvent evento = sangria
                        ? new CaixaSangria(EntityId.New().Value, movimento.Id, turno.Id, turno.UnidadeId, request.UserId, valorCentavos, movimento.Motivo, movimento.Destino!)
                        : new CaixaSuprimento(EntityId.New().Value, movimento.Id, turno.Id, turno.UnidadeId, request.UserId, valorCentavos, movimento.Motivo);
                    await eventos.PublicarAsync(org, evento, ct);
                    return new ResultadoDoMovimento(ResultadoDoCaixa.Ok, movimento);
                },
                cancellationToken);
        }
        catch (DecisaoDeCaixaNegadaException)
        {
            if (sangria)
            {
                await RegistrarNegacaoAsync(request, permissao, cancellationToken);
            }

            return new ResultadoDoMovimento(ResultadoDoCaixa.Negado);
        }
    }

    private Task RegistrarNegacaoAsync(IRequestContext request, string permissao, CancellationToken ct) =>
        audit.RecordDetachedAsync(
            request,
            new AuditEntry { Action = PlatformAuditActions.AcessoNegar, Result = AuditResult.Negado, ObjectType = "turno-caixa", After = new JsonObject { ["permissao"] = permissao } },
            ct);
}
