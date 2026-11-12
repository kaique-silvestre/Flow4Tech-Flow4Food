using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Comandas.Application;

/// <summary>
/// Registering areas and placing tables in them (active unit). Who may call is the route's job (<c>mesa:gerenciar</c>, decided by the one engine). Each write is ONE unit of work: the row and its
/// audit record commit or roll back together. The unit comes from the session, never from the client. A unit that turned tables off cannot manage areas.
/// </summary>
public sealed class GestaoDeAreas(IUnitOfWork unitOfWork, IMesasStore store, IAuditLog audit, ISettings settings, IClock clock)
{
    public Task<ResultadoDeArea> CriarAsync(IRequestContext request, string? nome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var limpo = RegrasDeArea.Nome(nome);
                if (limpo is null)
                {
                    return new ResultadoDeArea(ResultadoDeMesa.Invalido);
                }

                var org = request.Organization;
                if (!await UsaMesasAsync(org, ct))
                {
                    return new ResultadoDeArea(ResultadoDeMesa.MesasDesligadas);
                }

                var area = new Area(EntityId.New().Value, org.UnitId, limpo, true);
                if (!await store.TentarInserirAreaAsync(org.OrganizationId, area, clock.UtcNow, ct))
                {
                    return new ResultadoDeArea(ResultadoDeMesa.JaExiste);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.CriarArea, ObjectType = "area", ObjectId = area.Id.ToString("D"), After = new JsonObject { ["nome"] = limpo } }, ct);
                return new ResultadoDeArea(ResultadoDeMesa.Ok, area);
            },
            cancellationToken);
    }

    /// <summary>Renames and/or retires the area. Retiring keeps the area and its tables; the tables then show without an area.</summary>
    public Task<ResultadoDeArea> AtualizarAsync(IRequestContext request, Guid areaId, string? nome, bool ativa, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var limpo = RegrasDeArea.Nome(nome);
                if (limpo is null)
                {
                    return new ResultadoDeArea(ResultadoDeMesa.Invalido);
                }

                var org = request.Organization;
                if (!await UsaMesasAsync(org, ct))
                {
                    return new ResultadoDeArea(ResultadoDeMesa.MesasDesligadas);
                }

                var antes = await store.AcharAreaAsync(org.OrganizationId, org.UnitId, areaId, ct);
                if (antes is null)
                {
                    return new ResultadoDeArea(ResultadoDeMesa.AreaNaoEncontrada);
                }

                if (!await store.TentarAtualizarAreaAsync(org.OrganizationId, org.UnitId, areaId, limpo, ativa, ct))
                {
                    return new ResultadoDeArea(ResultadoDeMesa.JaExiste);
                }

                var area = antes with { Nome = limpo, Ativa = ativa };
                var entrada = new AuditEntry
                {
                    Action = ComandasAuditActions.AlterarArea,
                    ObjectType = "area",
                    ObjectId = areaId.ToString("D"),
                    Before = new JsonObject { ["nome"] = antes.Nome, ["ativa"] = antes.Ativa },
                    After = new JsonObject { ["nome"] = limpo, ["ativa"] = ativa },
                };
                await audit.RecordAsync(request, entrada, ct);
                return new ResultadoDeArea(ResultadoDeMesa.Ok, area);
            },
            cancellationToken);
    }

    /// <summary>Puts the table in an active area of the unit, or takes it out of any when <paramref name="areaId"/> is null.</summary>
    public Task<ResultadoDeMesa> DefinirAreaDaMesaAsync(IRequestContext request, Guid mesaId, Guid? areaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                if (!await UsaMesasAsync(org, ct))
                {
                    return ResultadoDeMesa.MesasDesligadas;
                }

                var mesa = await store.AcharAtivaAsync(org.OrganizationId, org.UnitId, mesaId, ct);
                if (mesa is null)
                {
                    return ResultadoDeMesa.MesaNaoEncontrada;
                }

                if (areaId is { } id && await store.AcharAreaAtivaAsync(org.OrganizationId, org.UnitId, id, ct) is null)
                {
                    return ResultadoDeMesa.AreaNaoEncontrada;
                }

                if (!await store.DefinirAreaDaMesaAsync(org.OrganizationId, org.UnitId, mesaId, areaId, ct))
                {
                    return ResultadoDeMesa.MesaNaoEncontrada;
                }

                var entrada = new AuditEntry
                {
                    Action = ComandasAuditActions.DefinirAreaDaMesa,
                    ObjectType = "mesa",
                    ObjectId = mesaId.ToString("D"),
                    Before = new JsonObject { ["areaId"] = mesa.AreaId?.ToString("D") },
                    After = new JsonObject { ["areaId"] = areaId?.ToString("D") },
                };
                await audit.RecordAsync(request, entrada, ct);
                return ResultadoDeMesa.Ok;
            },
            cancellationToken);
    }

    private async Task<bool> UsaMesasAsync(OrganizationContext org, CancellationToken ct) =>
        (await settings.ResolveAsync(OperacaoSettings.UsarMesas, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).Value == "true";
}
