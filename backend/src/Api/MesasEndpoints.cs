using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Api;

internal sealed record MesaDto(Guid Id, Guid UnidadeId, string Identificacao, int? Capacidade, bool Ativa);

/// <summary>Both fields nullable on purpose: an absent identification is a 400, never a default.</summary>
internal sealed record CriarMesaRequest(string? Identificacao, int? Capacidade);

internal sealed record MesaErro(string Codigo);

internal static class MesasEndpoints
{
    private const long Body = 1024;

    public static void MapMesas(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/mesas", async (RequestContextAccessor request, GestaoDeMesas mesas, CancellationToken cancellationToken) =>
                Results.Ok((await mesas.ListarAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listMesas")
            .RequireSession()
            .RequirePermission(MesaPermissions.Ver)
            .Produces<List<MesaDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/mesas", async (CriarMesaRequest body, RequestContextAccessor request, GestaoDeMesas mesas, CancellationToken cancellationToken) =>
            {
                var r = await mesas.CriarAsync(request, body.Identificacao, body.Capacidade, cancellationToken);
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
            .Produces<MesaErro>(StatusCodes.Status409Conflict);
    }

    private static MesaDto Dto(Mesa m) => new(m.Id, m.UnidadeId, m.Identificacao, m.Capacidade, m.Ativa);

    private static IResult Erro(ResultadoDeMesa resultado) => resultado switch
    {
        ResultadoDeMesa.JaExiste => Results.Json(new MesaErro("ja-existe"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDeMesa.MesasDesligadas => Results.Json(new MesaErro("mesas-desligadas"), statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new MesaErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
