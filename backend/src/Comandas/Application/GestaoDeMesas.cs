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

    /// <summary>The area named does not exist in the unit (or is retired).</summary>
    AreaNaoEncontrada,

    /// <summary>The table named does not exist in the unit.</summary>
    MesaNaoEncontrada,

    /// <summary>The unit already has a table with this identification.</summary>
    JaExiste,

    /// <summary>The unit turned the use of tables off.</summary>
    MesasDesligadas,
}

public sealed record ResultadoDeCriarMesa(ResultadoDeMesa Resultado, Mesa? Mesa = null);

public sealed record ResultadoDeArea(ResultadoDeMesa Resultado, Area? Area = null);

/// <summary>
/// Registering and listing the tables of the active unit. Who may call is the route's job (<c>mesa:ver</c>, <c>mesa:gerenciar</c>, decided by the one engine). Creating is ONE unit of work:
/// the row and its audit record commit or roll back together. The unit comes from the session, never from the client.
/// </summary>
public sealed class GestaoDeMesas(IUnitOfWork unitOfWork, IMesasStore store, IAuditLog audit, ISettings settings, IClock clock)
{
    public Task<ResultadoDeCriarMesa> CriarAsync(IRequestContext request, string? identificacao, int? capacidade, Guid? areaId = null, CancellationToken cancellationToken = default)
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

                if (areaId is { } area && await store.AcharAreaAtivaAsync(org.OrganizationId, org.UnitId, area, ct) is null)
                {
                    return new ResultadoDeCriarMesa(ResultadoDeMesa.AreaNaoEncontrada);
                }

                var mesa = new Mesa(EntityId.New().Value, org.UnitId, nome, capacidade, true, areaId);
                if (!await store.TentarInserirAsync(org.OrganizationId, mesa, clock.UtcNow, ct))
                {
                    return new ResultadoDeCriarMesa(ResultadoDeMesa.JaExiste);
                }

                var depois = new JsonObject { ["identificacao"] = nome };
                if (capacidade is { } lugares)
                {
                    depois["capacidade"] = lugares;
                }

                if (areaId is { } areaUsada)
                {
                    depois["areaId"] = areaUsada.ToString("D");
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.CriarMesa, ObjectType = "mesa", ObjectId = mesa.Id.ToString("D"), After = depois }, ct);
                return new ResultadoDeCriarMesa(ResultadoDeMesa.Ok, mesa);
            },
            cancellationToken);
    }

    /// <summary>Active tables of the active unit with their derived status; empty when the unit does not use tables.</summary>
    public Task<IReadOnlyList<MesaComStatus>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                if (!await UsaMesasAsync(org, ct))
                {
                    return [];
                }

                var mesas = await store.ListarAtivasAsync(org.OrganizationId, org.UnitId, ct);
                var ocupacao = await store.OcupacaoAsync(org.OrganizationId, org.UnitId, ct);
                return (IReadOnlyList<MesaComStatus>)mesas.Select(m => new MesaComStatus(m, ocupacao.TryGetValue(m.Id, out var o) ? RegrasDeMesa.Status(o.ComandasAbertas, o.AguardandoConta) : RegrasDeMesa.Status(0))).ToList();
            },
            cancellationToken);
    }

    private async Task<bool> UsaMesasAsync(OrganizationContext org, CancellationToken ct) =>
        (await settings.ResolveAsync(OperacaoSettings.UsarMesas, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).Value == "true";
}
