using Flow4Tech.Caixa.Application;
using Flow4Tech.Caixa.Domain;

namespace Flow4Tech.Api;

internal sealed record TurnoDto(
    Guid Id,
    Guid UnidadeId,
    Guid OperadorId,
    DateTimeOffset AbertoEm,
    long FundoCentavos,
    DateTimeOffset? FechadoEm,
    long? ContadoCentavos,
    long? EsperadoCentavos,
    long? DiferencaCentavos,
    string? DiferencaTexto);

internal sealed record AbrirTurnoRequest(long FundoCentavos);

internal sealed record FecharTurnoRequest(long ContadoCentavos);

internal sealed record CaixaErro(string Codigo);

internal static class CaixaEndpoints
{
    private const long Body = 1024;

    public static void MapCaixa(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/caixa/turnos/atual", async (RequestContextAccessor request, GestaoDoCaixa caixa, CancellationToken cancellationToken) =>
                await caixa.AtualAsync(request, cancellationToken) is { } turno ? Results.Ok(Dto(turno)) : Erro(ResultadoDoCaixa.SemTurnoAberto))
            .WithName("getTurnoAtual")
            .RequireSession()
            .RequirePermission(CaixaPermissions.Ver)
            .Produces<TurnoDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status404NotFound);

        app.MapGet("/caixa/turnos", async (RequestContextAccessor request, GestaoDoCaixa caixa, CancellationToken cancellationToken) =>
                Results.Ok((await caixa.ListarAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listTurnos")
            .RequireSession()
            .RequirePermission(CaixaPermissions.Ver)
            .Produces<List<TurnoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/caixa/turnos", async (AbrirTurnoRequest body, RequestContextAccessor request, GestaoDoCaixa caixa, CancellationToken cancellationToken) =>
            {
                var r = await caixa.AbrirAsync(request, body.FundoCentavos, cancellationToken);
                return r.Resultado == ResultadoDoCaixa.Ok ? Results.Json(Dto(r.Turno!), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
            })
            .WithName("abrirTurno")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CaixaPermissions.Abrir, sensitive: true)
            .Produces<TurnoDto>(StatusCodes.Status201Created)
            .Produces<CaixaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/caixa/turnos/atual/fechar", async (FecharTurnoRequest body, RequestContextAccessor request, GestaoDoCaixa caixa, CancellationToken cancellationToken) =>
            {
                var r = await caixa.FecharAsync(request, body.ContadoCentavos, cancellationToken);
                return r.Resultado == ResultadoDoCaixa.Ok ? Results.Ok(Dto(r.Turno!)) : Erro(r.Resultado);
            })
            .WithName("fecharTurno")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CaixaPermissions.Fechar, sensitive: true)
            .Produces<TurnoDto>()
            .Produces<CaixaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status404NotFound);
    }

    private static TurnoDto Dto(Turno t) =>
        new(t.Id, t.UnidadeId, t.OperadorId, t.AbertoEm, t.FundoCentavos, t.FechadoEm, t.ContadoCentavos, t.EsperadoCentavos, t.DiferencaCentavos, t.DiferencaCentavos is { } d ? RegrasDoCaixa.Palavra(d) : null);

    private static IResult Erro(ResultadoDoCaixa resultado) => resultado switch
    {
        ResultadoDoCaixa.JaAberto => Results.Json(new CaixaErro("turno-ja-aberto"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDoCaixa.SemTurnoAberto => Results.Json(new CaixaErro("sem-turno-aberto"), statusCode: StatusCodes.Status404NotFound),
        _ => Results.Json(new CaixaErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
