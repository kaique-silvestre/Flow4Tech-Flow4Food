using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDeMesa
{
    Ok,

    Invalido,

    /// <summary>The unit already has a table with this identification.</summary>
    JaExiste,

    /// <summary>The unit turned the use of tables off.</summary>
    MesasDesligadas,
}

public sealed record ResultadoDeCriarMesa(ResultadoDeMesa Resultado, Mesa? Mesa = null);

/// <summary>
/// Registering and listing the tables of the active unit. Who may call is the route's job (<c>mesa:ver</c>, <c>mesa:gerenciar</c>, decided by the one engine). Creating is ONE unit of work:
/// the row and its audit record commit or roll back together. The unit comes from the session, never from the client.
/// </summary>
public sealed class GestaoDeMesas(IUnitOfWork unitOfWork, IMesasStore store, IAuditLog audit, ISettings settings, IClock clock)
{
    public Task<ResultadoDeCriarMesa> CriarAsync(IRequestContext request, string? identificacao, int? capacidade, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var nome = RegrasDeMesa.Identificacao(identificacao);
                if (nome is null || !RegrasDeMesa.CapacidadeValida(capacidade))
                {
                    return new ResultadoDeCriarMesa(ResultadoDeMesa.Invalido);
                }

                var org = request.Organization;
                if (!await UsaMesasAsync(org, ct))
                {
                    return new ResultadoDeCriarMesa(ResultadoDeMesa.MesasDesligadas);
                }

                var mesa = new Mesa(EntityId.New().Value, org.UnitId, nome, capacidade, true);
                if (!await store.TentarInserirAsync(org.OrganizationId, mesa, clock.UtcNow, ct))
                {
                    return new ResultadoDeCriarMesa(ResultadoDeMesa.JaExiste);
                }

                var depois = new JsonObject { ["identificacao"] = nome };
                if (capacidade is { } lugares)
                {
                    depois["capacidade"] = lugares;
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.CriarMesa, ObjectType = "mesa", ObjectId = mesa.Id.ToString("D"), After = depois }, ct);
                return new ResultadoDeCriarMesa(ResultadoDeMesa.Ok, mesa);
            },
            cancellationToken);
    }

    /// <summary>Active tables of the active unit; empty when the unit does not use tables.</summary>
    public Task<IReadOnlyList<Mesa>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct => await UsaMesasAsync(request.Organization, ct)
                ? await store.ListarAtivasAsync(request.Organization.OrganizationId, request.Organization.UnitId, ct)
                : [],
            cancellationToken);
    }

    private async Task<bool> UsaMesasAsync(OrganizationContext org, CancellationToken ct) =>
        (await settings.ResolveAsync(OperacaoSettings.UsarMesas, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).Value == "true";
}
