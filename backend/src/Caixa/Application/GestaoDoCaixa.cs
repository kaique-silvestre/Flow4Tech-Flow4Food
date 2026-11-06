using System.Text.Json.Nodes;
using Flow4Tech.Caixa.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Caixa.Application;

public enum ResultadoDoCaixa
{
    Ok,

    Invalido,

    /// <summary>The operator already has an open shift in this unit.</summary>
    JaAberto,

    /// <summary>The operator has no open shift in this unit.</summary>
    SemTurnoAberto,
}

public sealed record ResultadoDoTurno(ResultadoDoCaixa Resultado, Turno? Turno = null);

/// <summary>
/// Opening and closing the operator's shift. Who may call is the route's job (<c>caixa:abrir</c>, <c>caixa:fechar</c>, decided by the one engine). Each change is ONE unit of work: state
/// change, audit record and outbox event commit or roll back together. The shift belongs to the session's user and active unit, never to anything the client names.
/// </summary>
public sealed class GestaoDoCaixa(IUnitOfWork unitOfWork, ICaixaStore store, IAuditLog audit, IEventosDoCaixa eventos, IPermissaoDoOperador permissoes, IClock clock)
{
    public const int LimiteDaLista = 100;

    public Task<ResultadoDoTurno> AbrirAsync(IRequestContext request, long fundoCentavos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!RegrasDoCaixa.ValorValido(fundoCentavos))
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
                }

                var org = request.Organization;
                var turno = new Turno(EntityId.New().Value, org.UnitId, request.UserId, clock.UtcNow, fundoCentavos, null, null, null, null);
                if (!await store.TentarAbrirAsync(org.OrganizationId, turno, ct))
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.JaAberto);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = CaixaAuditActions.Abrir, ObjectType = "turno-caixa", ObjectId = turno.Id.ToString("D"), After = new JsonObject { ["fundoCentavos"] = fundoCentavos } }, ct);
                await eventos.PublicarAsync(org, new CaixaAberto(EntityId.New().Value, turno.Id, turno.UnidadeId, turno.OperadorId, fundoCentavos), ct);
                return new ResultadoDoTurno(ResultadoDoCaixa.Ok, turno);
            },
            cancellationToken);
    }

    /// <summary>Closes the operator's open shift. Always concludes: a difference is recorded and told, never blocks (no approval flow in this slice).</summary>
    public Task<ResultadoDoTurno> FecharAsync(IRequestContext request, long contadoCentavos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!RegrasDoCaixa.ValorValido(contadoCentavos))
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
                }

                var org = request.Organization;
                if (await store.AcharAbertoAsync(org.OrganizationId, org.UnitId, request.UserId, ct) is not { } aberto)
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.SemTurnoAberto);
                }

                // The database computes the expected cash (float + cash received in the shift) in the closing statement itself.
                if (!await store.FecharAsync(org.OrganizationId, aberto.Id, clock.UtcNow, contadoCentavos, ct) || await store.AcharAsync(org.OrganizationId, aberto.Id, ct) is not { } fechado)
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.SemTurnoAberto);
                }

                var esperado = fechado.EsperadoCentavos!.Value;
                var diferenca = fechado.DiferencaCentavos!.Value;
                var depois = new JsonObject
                {
                    ["fundoCentavos"] = aberto.FundoCentavos,
                    ["contadoCentavos"] = contadoCentavos,
                    ["recebidoDinheiroCentavos"] = fechado.RecebidoDinheiroCentavos,
                    ["esperadoCentavos"] = esperado,
                    ["diferencaCentavos"] = diferenca,
                };
                await audit.RecordAsync(request, new AuditEntry { Action = CaixaAuditActions.Fechar, ObjectType = "turno-caixa", ObjectId = aberto.Id.ToString("D"), After = depois }, ct);
                await eventos.PublicarAsync(org, new CaixaFechado(EntityId.New().Value, aberto.Id, aberto.UnidadeId, aberto.OperadorId, contadoCentavos, esperado, diferenca), ct);
                return new ResultadoDoTurno(ResultadoDoCaixa.Ok, fechado);
            },
            cancellationToken);
    }

    /// <summary>The caller's open shift in the active unit, or null.</summary>
    public Task<Turno?> AtualAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.AcharAbertoAsync(request.Organization.OrganizationId, request.Organization.UnitId, request.UserId, ct), cancellationToken);
    }

    /// <summary>Shifts of the active unit. Without <c>caixa:ver-todos</c> only the caller's own.</summary>
    public Task<IReadOnlyList<Turno>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var todos = await permissoes.PodeAsync(request, CaixaPermissions.VerTodos, ct);
                return await store.ListarAsync(request.Organization.OrganizationId, request.Organization.UnitId, todos ? null : request.UserId, LimiteDaLista, ct);
            },
            cancellationToken);
    }
}
