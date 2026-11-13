using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Api;

/// <summary>A payment method of the organization. <c>Chave</c> is the stable key of a factory method (null for one the organization created); <c>Dinheiro</c> marks cash (the only one with change).</summary>
internal sealed record MetodoDePagamentoDto(Guid Id, string? Chave, string Nome, bool Dinheiro, bool Ativo, int Ordem);

internal sealed record CriarMetodoRequest(string? Nome);

internal sealed record AlterarMetodoRequest(string? Nome, bool Ativo);

/// <summary>Codes: <c>invalido</c> (400), <c>nao-encontrado</c> (404), <c>ja-existe</c>, <c>indisponivel-na-fase</c> (409).</summary>
internal sealed record MetodoErro(string Codigo);

internal static class PagamentosEndpoints
{
    private const long Body = 1024;

    public static void MapPagamentos(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/pagamentos/metodos", async (RequestContextAccessor request, GestaoDeMetodosDePagamento gestao, CancellationToken cancellationToken) =>
                Results.Ok((await gestao.ListarAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listMetodosDePagamento")
            .RequireSession()
            .RequirePermission(PagamentosPermissions.Ver)
            .Produces<List<MetodoDePagamentoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/pagamentos/metodos", async (CriarMetodoRequest body, RequestContextAccessor request, GestaoDeMetodosDePagamento gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.CriarAsync(request, body.Nome, cancellationToken);
                return r.Resultado == ResultadoDoMetodo.Ok ? Results.Json(Dto(r.Metodo!), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
            })
            .WithName("criarMetodoDePagamento")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(PagamentosPermissions.GerenciarMetodos)
            .Produces<MetodoDePagamentoDto>(StatusCodes.Status201Created)
            .Produces<MetodoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MetodoErro>(StatusCodes.Status409Conflict);

        app.MapPut("/pagamentos/metodos/{metodoId:guid}", async (Guid metodoId, AlterarMetodoRequest body, RequestContextAccessor request, GestaoDeMetodosDePagamento gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.AlterarAsync(request, metodoId, body.Nome, body.Ativo, cancellationToken);
                return r.Resultado == ResultadoDoMetodo.Ok ? Results.Ok(Dto(r.Metodo!)) : Erro(r.Resultado);
            })
            .WithName("alterarMetodoDePagamento")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(PagamentosPermissions.GerenciarMetodos)
            .Produces<MetodoDePagamentoDto>()
            .Produces<MetodoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MetodoErro>(StatusCodes.Status404NotFound)
            .Produces<MetodoErro>(StatusCodes.Status409Conflict);
    }

    private static MetodoDePagamentoDto Dto(MetodoDoSalao m) => new(m.Id, m.Chave, m.Nome, m.Dinheiro, m.Ativo, m.Ordem);

    private static IResult Erro(ResultadoDoMetodo r) => r switch
    {
        ResultadoDoMetodo.NaoEncontrado => Results.Json(new MetodoErro("nao-encontrado"), statusCode: StatusCodes.Status404NotFound),
        ResultadoDoMetodo.JaExiste => Results.Json(new MetodoErro("ja-existe"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDoMetodo.IndisponivelNaFase => Results.Json(new MetodoErro("indisponivel-na-fase"), statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new MetodoErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
