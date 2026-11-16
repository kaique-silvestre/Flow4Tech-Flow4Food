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
    IResumosDasComandas resumos,
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

                // Rounded to the 2 decimals the column stores, so the response always equals what was frozen.
                var taxa = Math.Round((await settings.ResolveAsync(VendaSettings.TaxaServicoPercentual, org.OrganizationId, org.UnitId, agora, ct)).AsDecimal(), 2, MidpointRounding.AwayFromZero);
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

    /// <summary>Open comandas of the active unit, each with its waiter's name, consumption and last launch. Without <c>comanda:ver-comandas-de-outros</c> only the caller's own.</summary>
    public Task<IReadOnlyList<ComandaDaLista>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var todas = await permissoes.PodeAsync(request, ComandaPermissions.VerComandasDeOutros, ct);
                var abertas = await store.ListarAbertasAsync(request.Organization.OrganizationId, request.Organization.UnitId, new ConsultaDaCentral(GarcomId: todas ? null : request.UserId), LimiteDaLista, ct);
                var resumo = await resumos.ResumirAsync(request.Organization.OrganizationId, [.. abertas.Select(c => new ComandaParaResumir(c.Id, c.GarcomId, c.AbertaEm))], ct);
                return (IReadOnlyList<ComandaDaLista>)[.. abertas.Select(c => new ComandaDaLista(c, resumo[c.Id]))];
            },
            cancellationToken);
    }

    /// <summary>
    /// The central de comandas: the list above narrowed by waiter, table, minimum open time and number, plus the total that matches. Read-only (no audit). The unit is the session's; without
    /// <c>comanda:ver-comandas-de-outros</c> the server limits it to the caller's own comandas whatever the filter says.
    /// </summary>
    public Task<ResultadoDaCentral> ConsultarCentralAsync(IRequestContext request, FiltroDaCentral filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(filtro);
        if (!RegrasDaCentral.Valido(filtro))
        {
            return Task.FromResult(new ResultadoDaCentral(false, [], 0));
        }

        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var todas = await permissoes.PodeAsync(request, ComandaPermissions.VerComandasDeOutros, ct);
                var consulta = RegrasDaCentral.Mapear(filtro, todas ? null : request.UserId, clock.UtcNow);
                if (consulta is null)
                {
                    return new ResultadoDaCentral(true, [], 0);
                }

                var org = request.Organization.OrganizationId;
                var unidade = request.Organization.UnitId;
                var abertas = await store.ListarAbertasAsync(org, unidade, consulta, LimiteDaLista, ct);
                var total = await store.ContarAbertasAsync(org, unidade, consulta, ct);
                var resumo = await resumos.ResumirAsync(org, [.. abertas.Select(c => new ComandaParaResumir(c.Id, c.GarcomId, c.AbertaEm))], ct);
                return new ResultadoDaCentral(true, [.. abertas.Select(c => new ComandaDaLista(c, resumo[c.Id]))], total);
            },
            cancellationToken);
    }
}
