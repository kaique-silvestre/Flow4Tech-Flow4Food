using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Api;

internal sealed record ComandaDto(
    Guid Id,
    Guid UnidadeId,
    int Numero,
    string NumeroTexto,
    string Identificacao,
    Guid? MesaId,
    string Canal,
    Guid GarcomId,
    int? Pessoas,
    string Estado,
    decimal TaxaServicoPercentual,
    bool TaxaServicoRemovida,
    int Versao,
    DateTimeOffset AbertaEm);

/// <summary>Every field is optional on purpose: a comanda opens with a number only. Unit, waiter and channel never come from the body.</summary>
internal sealed record AbrirComandaRequest(string? Identificacao, Guid? MesaId, int? Pessoas);

/// <summary><c>VersaoAtual</c> is set when a write carried a stale version.</summary>
internal sealed record ComandaErro(string Codigo, int? VersaoAtual = null);

internal static class ComandasEndpoints
{
    private const long Body = 1024;

    public static void MapComandas(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/comandas", async (RequestContextAccessor request, GestaoDeComandas comandas, CancellationToken cancellationToken) =>
                Results.Ok((await comandas.ListarAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listComandas")
            .RequireSession()
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<List<ComandaDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/comandas", async (AbrirComandaRequest body, RequestContextAccessor request, GestaoDeComandas comandas, CancellationToken cancellationToken) =>
            {
                var r = await comandas.AbrirAsync(request, body.Identificacao, body.MesaId, body.Pessoas, cancellationToken);
                return r.Resultado == ResultadoDeComanda.Ok ? Results.Json(Dto(r.Comanda!), statusCode: StatusCodes.Status201Created) : Erro(r);
            })
            .WithName("abrirComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.Abrir)
            .Produces<ComandaDto>(StatusCodes.Status201Created)
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);
    }

    private static ComandaDto Dto(Comanda c) =>
        new(c.Id, c.UnidadeId, c.Numero, RegrasDeComanda.NumeroDeExibicao(c.Numero), c.Identificacao, c.MesaId, c.Canal, c.GarcomId, c.Pessoas, RegrasDeComanda.Palavra(c.Estado), c.TaxaServicoPercentual, c.TaxaServicoRemovida, c.Versao, c.AbertaEm);

    private static IResult Erro(ResultadoDeAbrir r) => r.Resultado switch
    {
        ResultadoDeComanda.MesasDesligadas => Results.Json(new ComandaErro("mesas-desligadas"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDeComanda.VersaoDesatualizada => Results.Json(new ComandaErro("versao-desatualizada", r.VersaoAtual), statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new ComandaErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
