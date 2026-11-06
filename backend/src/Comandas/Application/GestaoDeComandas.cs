using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDeComanda
{
    Ok,

    Invalido,

    /// <summary>A table was named but the unit does not use tables.</summary>
    MesasDesligadas,

    /// <summary>A write carried a version other than the current one (the caller learns the current one).</summary>
    VersaoDesatualizada,
}

public sealed record ResultadoDeAbrir(ResultadoDeComanda Resultado, Comanda? Comanda = null, int? VersaoAtual = null);

/// <summary>
/// Opening and listing the comandas of the active unit. Who may call is the route's job (<c>comanda:abrir</c>, <c>comanda:ver</c>, decided by the one engine). Opening is ONE unit of work:
/// the display number, the row, the audit record and the outbox event commit or roll back together. Unit and waiter come from the session, never from the client.
/// </summary>
public sealed class GestaoDeComandas(
    IUnitOfWork unitOfWork,
    IComandasStore store,
    IMesasStore mesas,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    IDiaOperacional dias,
    ISettings settings,
    IClock clock)
{
    public const int LimiteDaLista = 200;

    public Task<ResultadoDeAbrir> AbrirAsync(IRequestContext request, string? identificacao, Guid? mesaId, int? pessoas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var nome = identificacao is null ? null : RegrasDeComanda.Identificacao(identificacao);
                if ((identificacao is not null && (nome is null || mesaId is not null)) || !RegrasDeComanda.PessoasValidas(pessoas))
                {
                    return new ResultadoDeAbrir(ResultadoDeComanda.Invalido);
                }

                var org = request.Organization;
                var agora = clock.UtcNow;
                Mesa? mesa = null;
                if (mesaId is { } id)
                {
                    if ((await settings.ResolveAsync(OperacaoSettings.UsarMesas, org.OrganizationId, org.UnitId, agora, ct)).Value != "true")
                    {
                        return new ResultadoDeAbrir(ResultadoDeComanda.MesasDesligadas);
                    }

                    mesa = await mesas.AcharAtivaAsync(org.OrganizationId, org.UnitId, id, ct);
                    if (mesa is null)
                    {
                        return new ResultadoDeAbrir(ResultadoDeComanda.Invalido);
                    }
                }

                var taxa = (await settings.ResolveAsync(VendaSettings.TaxaServicoPercentual, org.OrganizationId, org.UnitId, agora, ct)).AsDecimal();
                var dia = await dias.AtualAsync(org, ct);
                var numero = await store.ProximoNumeroAsync(org.OrganizationId, org.UnitId, dia, ct);
                var comanda = new Comanda(
                    EntityId.New().Value,
                    org.UnitId,
                    dia,
                    numero,
                    mesa?.Identificacao ?? nome ?? RegrasDeComanda.IdentificacaoPadrao(numero),
                    mesa?.Id,
                    RegrasDeComanda.CanalPadrao,
                    request.UserId,
                    pessoas,
                    EstadoDaComanda.Aberta,
                    agora,
                    null,
                    taxa,
                    false,
                    1);
                await store.InserirAsync(org.OrganizationId, comanda, ct);

                var depois = new JsonObject { ["numero"] = numero, ["identificacao"] = comanda.Identificacao, ["taxaServicoPercentual"] = taxa };
                if (comanda.MesaId is { } mesaUsada)
                {
                    depois["mesaId"] = mesaUsada.ToString("D");
                }

                if (pessoas is { } quantas)
                {
                    depois["pessoas"] = quantas;
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.AbrirComanda, ObjectType = "comanda", ObjectId = comanda.Id.ToString("D"), After = depois }, ct);
                await eventos.PublicarAsync(org, new ComandaAberta(EntityId.New().Value, comanda.Id, comanda.UnidadeId, numero, comanda.Identificacao, comanda.MesaId, comanda.GarcomId), ct);
                return new ResultadoDeAbrir(ResultadoDeComanda.Ok, comanda);
            },
            cancellationToken);
    }

    /// <summary>Open comandas of the active unit. Without <c>comanda:ver-comandas-de-outros</c> only the caller's own.</summary>
    public Task<IReadOnlyList<Comanda>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var todas = await permissoes.PodeAsync(request, ComandaPermissions.VerComandasDeOutros, ct);
                return await store.ListarAbertasAsync(request.Organization.OrganizationId, request.Organization.UnitId, todas ? null : request.UserId, LimiteDaLista, ct);
            },
            cancellationToken);
    }
}
