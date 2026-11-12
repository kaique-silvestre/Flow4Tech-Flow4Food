using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Api;

/// <summary><c>Status</c> is derived from the open comandas: <c>livre</c> or <c>ocupada</c>. A newly created table is always <c>livre</c>.</summary>
internal sealed record MesaDto(Guid Id, Guid UnidadeId, string Identificacao, int? Capacidade, bool Ativa, string Status, Guid? AreaId = null);

/// <summary>Identification nullable on purpose: an absent identification is a 400, never a default. <c>AreaId</c> is optional; when given it must be an active area of the unit.</summary>
internal sealed record CriarMesaRequest(string? Identificacao, int? Capacidade, Guid? AreaId = null);

internal sealed record AreaDto(Guid Id, Guid UnidadeId, string Nome, bool Ativa);

/// <summary>Nullable on purpose: an absent name is a 400.</summary>
internal sealed record CriarAreaRequest(string? Nome);

/// <summary>Renames and/or retires the area (<c>Ativa</c> false keeps it for history; its tables then show without an area).</summary>
internal sealed record AtualizarAreaRequest(string? Nome, bool Ativa);

/// <summary><c>AreaId</c> null takes the table out of any area.</summary>
internal sealed record DefinirAreaDaMesaRequest(Guid? AreaId);

/// <summary>
/// A table on the map. <c>Status</c> is derived from the open comandas: <c>livre</c>, <c>ocupada</c> or <c>aguardando-conta</c>. <c>AbertaDesde</c> is the opening time of the first open comanda (null when free):
/// the client computes the time since opening from it.
/// </summary>
internal sealed record MesaNoMapaDto(Guid Id, string Identificacao, int? Capacidade, Guid? AreaId, string Status, int ComandasAbertas, DateTimeOffset? AbertaDesde);

internal sealed record AreaNoMapaDto(Guid Id, string Nome, List<MesaNoMapaDto> Mesas);

/// <summary>Active areas with their tables, then the tables with no active area. Both lists are empty when the unit does not use tables.</summary>
internal sealed record MapaDeMesasDto(List<AreaNoMapaDto> Areas, List<MesaNoMapaDto> SemArea);

internal sealed record MesaErro(string Codigo);

/// <summary>
/// What the table ordered the last time it closed a comanda: <c>ComandaId</c>, <c>Numero</c>, <c>NumeroTexto</c> and <c>FechadaEm</c> are null and <c>Itens</c> is empty when there is none.
/// <c>Itens</c> holds the items that stood (cancelled ones left out), prices as they were frozen then.
/// </summary>
internal sealed record UltimaVezDaMesaDto(Guid? ComandaId, int? Numero, string? NumeroTexto, DateTimeOffset? FechadaEm, List<ItemDto> Itens);

internal static class MesasEndpoints
{
    private const long Body = 1024;

    public static void MapMesas(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/mesas", async (RequestContextAccessor request, GestaoDeMesas mesas, CancellationToken cancellationToken) =>
                Results.Ok((await mesas.ListarAsync(request, cancellationToken)).Select(m => Dto(m.Mesa, m.Status)).ToList()))
            .WithName("listMesas")
            .RequireSession()
            .RequirePermission(MesaPermissions.Ver)
            .Produces<List<MesaDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/mesas/mapa", async (RequestContextAccessor request, ConsultaDoMapaDeMesas consulta, CancellationToken cancellationToken) =>
            {
                var mapa = await consulta.ConsultarAsync(request, cancellationToken);
                return Results.Ok(new MapaDeMesasDto(
                    [.. mapa.Areas.Select(a => new AreaNoMapaDto(a.Area.Id, a.Area.Nome, [.. a.Mesas.Select(NoMapa)]))],
                    [.. mapa.SemArea.Select(NoMapa)]));
            })
            .WithName("getMapaDeMesas")
            .RequireSession()
            .RequirePermission(MesaPermissions.Ver)
            .Produces<MapaDeMesasDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/mesas/areas", async (CriarAreaRequest body, RequestContextAccessor request, GestaoDeAreas areas, CancellationToken cancellationToken) =>
            {
                var r = await areas.CriarAsync(request, body.Nome, cancellationToken);
                return r.Resultado == ResultadoDeMesa.Ok ? Results.Json(AreaParaDto(r.Area!), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
            })
            .WithName("criarArea")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(MesaPermissions.Gerenciar)
            .Produces<AreaDto>(StatusCodes.Status201Created)
            .Produces<MesaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MesaErro>(StatusCodes.Status409Conflict);

        app.MapPut("/mesas/areas/{areaId:guid}", async (Guid areaId, AtualizarAreaRequest body, RequestContextAccessor request, GestaoDeAreas areas, CancellationToken cancellationToken) =>
            {
                var r = await areas.AtualizarAsync(request, areaId, body.Nome, body.Ativa, cancellationToken);
                return r.Resultado == ResultadoDeMesa.Ok ? Results.Ok(AreaParaDto(r.Area!)) : Erro(r.Resultado);
            })
            .WithName("atualizarArea")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(MesaPermissions.Gerenciar)
            .Produces<AreaDto>()
            .Produces<MesaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MesaErro>(StatusCodes.Status404NotFound)
            .Produces<MesaErro>(StatusCodes.Status409Conflict);

        app.MapPut("/mesas/{mesaId:guid}/area", async (Guid mesaId, DefinirAreaDaMesaRequest body, RequestContextAccessor request, GestaoDeAreas areas, CancellationToken cancellationToken) =>
            {
                var r = await areas.DefinirAreaDaMesaAsync(request, mesaId, body.AreaId, cancellationToken);
                return r == ResultadoDeMesa.Ok ? Results.NoContent() : Erro(r);
            })
            .WithName("definirAreaDaMesa")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(MesaPermissions.Gerenciar)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MesaErro>(StatusCodes.Status404NotFound)
            .Produces<MesaErro>(StatusCodes.Status409Conflict);

        app.MapGet("/mesas/{mesaId:guid}/ultima-vez", async (Guid mesaId, RequestContextAccessor request, ConsultaDaUltimaVez consulta, CancellationToken cancellationToken) =>
            {
                var r = await consulta.ConsultarAsync(request, mesaId, cancellationToken);
                if (r is null)
                {
                    return Results.Json(new MesaErro("nao-encontrada"), statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(r.Comanda is null
                    ? new UltimaVezDaMesaDto(null, null, null, null, [])
                    : new UltimaVezDaMesaDto(r.Comanda.Id, r.Comanda.Numero, RegrasDeComanda.NumeroDeExibicao(r.Comanda.Numero), r.Comanda.FechadaEm, [.. r.Itens.Select(ComandasEndpoints.ItemParaDto)]));
            })
            .WithName("getUltimaVezDaMesa")
            .RequireSession()
            .RequirePermission(MesaPermissions.Ver)
            .Produces<UltimaVezDaMesaDto>()
            .Produces<MesaErro>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/mesas", async (CriarMesaRequest body, RequestContextAccessor request, GestaoDeMesas mesas, CancellationToken cancellationToken) =>
            {
                var r = await mesas.CriarAsync(request, body.Identificacao, body.Capacidade, body.AreaId, cancellationToken);
                return r.Resultado == ResultadoDeMesa.Ok ? Results.Json(Dto(r.Mesa!), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
            })
            .WithName("criarMesa")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(MesaPermissions.Gerenciar)
            .Produces<MesaDto>(StatusCodes.Status201Created)
            .Produces<MesaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MesaErro>(StatusCodes.Status404NotFound)
            .Produces<MesaErro>(StatusCodes.Status409Conflict);
    }

    private static string Palavra(StatusDaMesa status) => status switch
    {
        StatusDaMesa.Ocupada => "ocupada",
        StatusDaMesa.AguardandoConta => "aguardando-conta",
        _ => "livre",
    };

    private static MesaDto Dto(Mesa m, StatusDaMesa status = StatusDaMesa.Livre) =>
        new(m.Id, m.UnidadeId, m.Identificacao, m.Capacidade, m.Ativa, Palavra(status), m.AreaId);

    private static MesaNoMapaDto NoMapa(MesaNoMapa m) =>
        new(m.Mesa.Id, m.Mesa.Identificacao, m.Mesa.Capacidade, m.Mesa.AreaId, Palavra(m.Status), m.ComandasAbertas, m.AbertaDesde);

    private static AreaDto AreaParaDto(Area a) => new(a.Id, a.UnidadeId, a.Nome, a.Ativa);

    private static IResult Erro(ResultadoDeMesa resultado) => resultado switch
    {
        ResultadoDeMesa.AreaNaoEncontrada or ResultadoDeMesa.MesaNaoEncontrada => Results.Json(new MesaErro("nao-encontrada"), statusCode: StatusCodes.Status404NotFound),
        ResultadoDeMesa.JaExiste => Results.Json(new MesaErro("ja-existe"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDeMesa.MesasDesligadas => Results.Json(new MesaErro("mesas-desligadas"), statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new MesaErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
