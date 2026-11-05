using Flow4Tech.Cardapio.Application;
using Flow4Tech.Cardapio.Domain;

namespace Flow4Tech.Api;

internal sealed record CategoriaDto(Guid Id, string Nome, bool Ativa, int Versao);

internal sealed record ProdutoDto(Guid Id, Guid CategoriaId, string Codigo, string Nome, long PrecoCentavos, string Moeda, bool Ativo, int Versao);

internal sealed record CriarCategoriaRequest(string? Nome);

internal sealed record AlterarCategoriaRequest(string? Nome, bool Ativa, int Versao);

internal sealed record CriarProdutoRequest(string? Codigo, string? Nome, long PrecoCentavos, Guid CategoriaId, bool Ativo = true);

internal sealed record AlterarProdutoRequest(string? Codigo, string? Nome, long PrecoCentavos, Guid CategoriaId, bool Ativo, int Versao);

internal sealed record CardapioErro(string Codigo);

internal static class CardapioEndpoints
{
    private const long Body = 4096;

    public static void MapCardapio(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/cardapio/categorias", async (RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
                Results.Ok((await cardapio.ListarCategoriasAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listCategorias")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<CategoriaDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/cardapio/categorias", async (CriarCategoriaRequest body, RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
            {
                var r = await cardapio.CriarCategoriaAsync(request, body.Nome, cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Json(Dto(r.Categoria!), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
            })
            .WithName("criarCategoria")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<CategoriaDto>(StatusCodes.Status201Created)
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status409Conflict);

        app.MapPut("/cardapio/categorias/{id:guid}", async (Guid id, AlterarCategoriaRequest body, RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
            {
                var r = await cardapio.AlterarCategoriaAsync(request, id, body.Nome, body.Ativa, body.Versao, cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Ok(Dto(r.Categoria!)) : Erro(r.Resultado);
            })
            .WithName("alterarCategoria")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<CategoriaDto>()
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound)
            .Produces<CardapioErro>(StatusCodes.Status409Conflict);

        // Active products only: what can be launched. The manager's full list (with inactive ones) is /cardapio/produtos/gestao.
        app.MapGet("/cardapio/produtos", async (RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
                Results.Ok((await cardapio.ListarProdutosAsync(request, null, false, cancellationToken)).Select(Dto).ToList()))
            .WithName("listProdutos")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<ProdutoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/cardapio/produtos/busca", async (string? q, RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
                q is { Length: > 80 }
                    ? Erro(ResultadoDoCardapio.Invalido)
                    : Results.Ok((await cardapio.ListarProdutosAsync(request, q, false, cancellationToken)).Select(Dto).ToList()))
            .WithName("buscarProdutos")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<ProdutoDto>>()
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/cardapio/produtos/frequentes", async (RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
                Results.Ok((await cardapio.FrequentesAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listProdutosFrequentes")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<ProdutoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/cardapio/produtos/gestao", async (RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
                Results.Ok((await cardapio.ListarProdutosAsync(request, null, true, cancellationToken)).Select(Dto).ToList()))
            .WithName("listProdutosGestao")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<List<ProdutoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/cardapio/produtos", async (CriarProdutoRequest body, RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
            {
                var r = await cardapio.CriarProdutoAsync(request, new NovoProduto(body.Codigo, body.Nome, body.PrecoCentavos, body.CategoriaId, body.Ativo), cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Json(Dto(r.Produto!), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
            })
            .WithName("criarProduto")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<ProdutoDto>(StatusCodes.Status201Created)
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound)
            .Produces<CardapioErro>(StatusCodes.Status409Conflict);

        app.MapPut("/cardapio/produtos/{id:guid}", async (Guid id, AlterarProdutoRequest body, RequestContextAccessor request, GestaoDoCardapio cardapio, CancellationToken cancellationToken) =>
            {
                var r = await cardapio.AlterarProdutoAsync(request, id, new AlteracaoDeProduto(body.Codigo, body.Nome, body.PrecoCentavos, body.CategoriaId, body.Ativo, body.Versao), cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Ok(Dto(r.Produto!)) : Erro(r.Resultado);
            })
            .WithName("alterarProduto")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<ProdutoDto>()
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound)
            .Produces<CardapioErro>(StatusCodes.Status409Conflict);
    }

    private static CategoriaDto Dto(Categoria c) => new(c.Id, c.Nome, c.Ativa, c.Versao);

    private static ProdutoDto Dto(Produto p) => new(p.Id, p.CategoriaId, p.Codigo, p.Nome, p.PrecoCentavos, p.Moeda, p.Ativo, p.Versao);

    private static IResult Erro(ResultadoDoCardapio resultado) => resultado switch
    {
        ResultadoDoCardapio.NaoEncontrado or ResultadoDoCardapio.CategoriaInexistente => Results.Json(new CardapioErro(Codigo(resultado)), statusCode: StatusCodes.Status404NotFound),
        ResultadoDoCardapio.EmUso or ResultadoDoCardapio.VersaoDesatualizada => Results.Json(new CardapioErro(Codigo(resultado)), statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new CardapioErro(Codigo(resultado)), statusCode: StatusCodes.Status400BadRequest),
    };

    private static string Codigo(ResultadoDoCardapio resultado) => resultado switch
    {
        ResultadoDoCardapio.NaoEncontrado => "nao-encontrado",
        ResultadoDoCardapio.CategoriaInexistente => "categoria-inexistente",
        ResultadoDoCardapio.EmUso => "em-uso",
        ResultadoDoCardapio.VersaoDesatualizada => "versao-desatualizada",
        _ => "invalido",
    };
}
