using Flow4Tech.Cardapio.Application;
using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Kernel;

namespace Flow4Tech.Api;

internal sealed record CategoriaDto(Guid Id, string Nome, bool Ativa, int Versao, Guid? CategoriaPaiId);

/// <summary>A promotional price valid from <c>Inicio</c> (inclusive) to <c>Fim</c> (exclusive), UTC.</summary>
internal sealed record PromocaoDto(long PrecoCentavos, DateTimeOffset Inicio, DateTimeOffset Fim);

/// <summary><c>PrecoEfetivoCentavos</c> is what a launch charges now in the session's unit (unit override, else promotion while valid, else base); <c>PrecoDaUnidadeCentavos</c> is the override of that unit, if any.</summary>
internal sealed record ProdutoDto(
    Guid Id,
    Guid CategoriaId,
    string Codigo,
    string Nome,
    long PrecoCentavos,
    string Moeda,
    bool Ativo,
    int Versao,
    bool Indisponivel,
    PromocaoDto? Promocao,
    long? PrecoDaUnidadeCentavos,
    long PrecoEfetivoCentavos);

internal sealed record CriarCategoriaRequest(string? Nome, Guid? CategoriaPaiId = null);

internal sealed record AlterarCategoriaRequest(string? Nome, bool Ativa, int Versao, Guid? CategoriaPaiId = null);

internal sealed record CriarProdutoRequest(string? Codigo, string? Nome, long PrecoCentavos, Guid CategoriaId, bool Ativo = true, bool Indisponivel = false, PromocaoDto? Promocao = null);

internal sealed record AlterarProdutoRequest(string? Codigo, string? Nome, long PrecoCentavos, Guid CategoriaId, bool Ativo, int Versao, bool Indisponivel = false, PromocaoDto? Promocao = null);

internal sealed record DefinirPrecoDaUnidadeRequest(long PrecoCentavos);

internal sealed record CardapioErro(string Codigo);

internal sealed record OpcaoDto(Guid Id, string Nome, long PrecoCentavos, bool Ativa);

/// <summary><c>Obrigatorio</c> = at least one choice is required (<c>Minimo</c> &gt;= 1).</summary>
internal sealed record GrupoDeOpcaoDto(Guid Id, string Nome, int Minimo, int Maximo, bool Obrigatorio, bool Ativo, int Versao, List<OpcaoDto> Opcoes);

/// <summary><c>Id</c> null creates the option; an id of this group updates it; existing options left out are turned off.</summary>
internal sealed record OpcaoRequest(Guid? Id, string? Nome, long PrecoCentavos, bool Ativa = true);

internal sealed record CriarGrupoDeOpcaoRequest(string? Nome, int Minimo, int Maximo, bool Ativo = true, List<OpcaoRequest>? Opcoes = null);

internal sealed record AlterarGrupoDeOpcaoRequest(string? Nome, int Minimo, int Maximo, bool Ativo, int Versao, List<OpcaoRequest>? Opcoes = null);

internal sealed record DefinirGruposDoProdutoRequest(List<Guid>? GrupoIds);

/// <summary>An option with how many times it was launched with the product in the active unit.</summary>
internal sealed record OpcaoFrequenteDto(Guid GrupoId, string GrupoNome, OpcaoDto Opcao, long Lancamentos);

internal static class CardapioEndpoints
{
    private const long Body = 4096;

    private const long BodyGrupo = 32 * 1024;

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
                var r = await cardapio.CriarCategoriaAsync(request, body.Nome, body.CategoriaPaiId, cancellationToken);
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
                var r = await cardapio.AlterarCategoriaAsync(request, id, body.Nome, body.Ativa, body.Versao, body.CategoriaPaiId, cancellationToken);
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
        app.MapGet("/cardapio/produtos", async (RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
                Results.Ok((await cardapio.ListarProdutosAsync(request, false, cancellationToken)).Select(p => Dto(p, clock.UtcNow)).ToList()))
            .WithName("listProdutos")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<ProdutoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        // The launch search: exact short code first, then names that start with the text, then names that contain it (accents and case ignored); at most 20.
        app.MapGet("/cardapio/produtos/busca", async (string? q, RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
            {
                var achados = await cardapio.BuscarAsync(request, q, cancellationToken);
                return achados is null ? Erro(ResultadoDoCardapio.Invalido) : Results.Ok(achados.Select(p => Dto(p, clock.UtcNow)).ToList());
            })
            .WithName("buscarProdutos")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<ProdutoDto>>()
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        // The caller's own favorites in the active unit (what THEY launched most), never another waiter's.
        app.MapGet("/cardapio/produtos/favoritos", async (RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
                Results.Ok((await cardapio.FavoritosAsync(request, cancellationToken)).Select(p => Dto(p, clock.UtcNow)).ToList()))
            .WithName("listProdutosFavoritos")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<ProdutoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/cardapio/produtos/frequentes", async (RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
                Results.Ok((await cardapio.FrequentesAsync(request, cancellationToken)).Select(p => Dto(p, clock.UtcNow)).ToList()))
            .WithName("listProdutosFrequentes")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<ProdutoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/cardapio/produtos/gestao", async (RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
                Results.Ok((await cardapio.ListarProdutosAsync(request, true, cancellationToken)).Select(p => Dto(p, clock.UtcNow)).ToList()))
            .WithName("listProdutosGestao")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<List<ProdutoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/cardapio/produtos", async (CriarProdutoRequest body, RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
            {
                var r = await cardapio.CriarProdutoAsync(request, new NovoProduto(body.Codigo, body.Nome, body.PrecoCentavos, body.CategoriaId, body.Ativo, body.Indisponivel, Domain(body.Promocao)), cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Json(Dto(r.Produto!, clock.UtcNow), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
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

        app.MapPut("/cardapio/produtos/{id:guid}", async (Guid id, AlterarProdutoRequest body, RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
            {
                var r = await cardapio.AlterarProdutoAsync(request, id, new AlteracaoDeProduto(body.Codigo, body.Nome, body.PrecoCentavos, body.CategoriaId, body.Ativo, body.Versao, body.Indisponivel, Domain(body.Promocao)), cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Ok(Dto(r.Produto!, clock.UtcNow)) : Erro(r.Resultado);
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

        // The unit is ALWAYS the session's active unit; changing a price needs produto:gerenciar here and cardapio:preco-alterar (percent limit) asked of the engine by the handler.
        app.MapPut("/cardapio/produtos/{id:guid}/preco-unidade", async (Guid id, DefinirPrecoDaUnidadeRequest body, RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
            {
                var r = await cardapio.DefinirPrecoDaUnidadeAsync(request, id, body.PrecoCentavos, cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Ok(Dto(r.Produto!, clock.UtcNow)) : Erro(r.Resultado);
            })
            .WithName("definirPrecoDaUnidade")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<ProdutoDto>()
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound);

        app.MapDelete("/cardapio/produtos/{id:guid}/preco-unidade", async (Guid id, RequestContextAccessor request, GestaoDoCardapio cardapio, IClock clock, CancellationToken cancellationToken) =>
            {
                var r = await cardapio.RemoverPrecoDaUnidadeAsync(request, id, cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Ok(Dto(r.Produto!, clock.UtcNow)) : Erro(r.Resultado);
            })
            .WithName("removerPrecoDaUnidade")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<ProdutoDto>()
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound);

        app.MapGet("/cardapio/grupos-opcao", async (RequestContextAccessor request, GestaoDeOpcoes opcoes, CancellationToken cancellationToken) =>
                Results.Ok((await opcoes.ListarGruposAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listGruposDeOpcao")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<List<GrupoDeOpcaoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/cardapio/grupos-opcao", async (CriarGrupoDeOpcaoRequest body, RequestContextAccessor request, GestaoDeOpcoes opcoes, CancellationToken cancellationToken) =>
            {
                var r = await opcoes.CriarGrupoAsync(request, new NovoGrupoDeOpcao(body.Nome, body.Minimo, body.Maximo, body.Ativo, Domain(body.Opcoes)), cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Json(Dto(r.Grupo!), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
            })
            .WithName("criarGrupoDeOpcao")
            .RequireSession()
            .WithBodyLimit(BodyGrupo)
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<GrupoDeOpcaoDto>(StatusCodes.Status201Created)
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPut("/cardapio/grupos-opcao/{id:guid}", async (Guid id, AlterarGrupoDeOpcaoRequest body, RequestContextAccessor request, GestaoDeOpcoes opcoes, CancellationToken cancellationToken) =>
            {
                var r = await opcoes.AlterarGrupoAsync(request, id, new AlteracaoDeGrupoDeOpcao(body.Nome, body.Minimo, body.Maximo, body.Ativo, body.Versao, Domain(body.Opcoes)), cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Ok(Dto(r.Grupo!)) : Erro(r.Resultado);
            })
            .WithName("alterarGrupoDeOpcao")
            .RequireSession()
            .WithBodyLimit(BodyGrupo)
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<GrupoDeOpcaoDto>()
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound)
            .Produces<CardapioErro>(StatusCodes.Status409Conflict);

        // What a launch offers for the product: its ACTIVE groups with all their options (inactive ones are marked so the screen can hide them).
        app.MapGet("/cardapio/produtos/{id:guid}/grupos-opcao", async (Guid id, RequestContextAccessor request, GestaoDeOpcoes opcoes, CancellationToken cancellationToken) =>
            {
                var grupos = await opcoes.GruposDoProdutoAsync(request, id, cancellationToken);
                return grupos is null ? Erro(ResultadoDoCardapio.NaoEncontrado) : Results.Ok(grupos.Select(Dto).ToList());
            })
            .WithName("listGruposDeOpcaoDoProduto")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<GrupoDeOpcaoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound);

        app.MapPut("/cardapio/produtos/{id:guid}/grupos-opcao", async (Guid id, DefinirGruposDoProdutoRequest body, RequestContextAccessor request, GestaoDeOpcoes opcoes, CancellationToken cancellationToken) =>
            {
                var r = await opcoes.DefinirGruposDoProdutoAsync(request, id, body.GrupoIds, cancellationToken);
                return r.Resultado == ResultadoDoCardapio.Ok ? Results.Ok(r.Grupos!.Select(Dto).ToList()) : Erro(r.Resultado);
            })
            .WithName("definirGruposDeOpcaoDoProduto")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CardapioPermissions.Gerenciar)
            .Produces<List<GrupoDeOpcaoDto>>()
            .Produces<CardapioErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound);

        // Feeds "most used add-ons" of the launch screen: counted per product in the session's unit.
        app.MapGet("/cardapio/produtos/{id:guid}/opcoes-frequentes", async (Guid id, RequestContextAccessor request, GestaoDeOpcoes opcoes, CancellationToken cancellationToken) =>
            {
                var frequentes = await opcoes.FrequentesAsync(request, id, cancellationToken);
                return frequentes is null
                    ? Erro(ResultadoDoCardapio.NaoEncontrado)
                    : Results.Ok(frequentes.Select(f => new OpcaoFrequenteDto(f.GrupoId, f.GrupoNome, Dto(f.Opcao), f.Lancamentos)).ToList());
            })
            .WithName("listOpcoesFrequentes")
            .RequireSession()
            .RequirePermission(CardapioPermissions.Ver)
            .Produces<List<OpcaoFrequenteDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CardapioErro>(StatusCodes.Status404NotFound);

        app.MapImportacaoDoCardapio();
    }

    private static OpcaoDto Dto(Opcao o) => new(o.Id, o.Nome, o.PrecoCentavos, o.Ativa);

    private static GrupoDeOpcaoDto Dto(GrupoDeOpcao g) => new(g.Id, g.Nome, g.Minimo, g.Maximo, g.Obrigatorio, g.Ativo, g.Versao, g.Opcoes.Select(Dto).ToList());

    private static List<OpcaoNova>? Domain(List<OpcaoRequest>? opcoes) => opcoes?.Select(o => new OpcaoNova(o?.Id, o?.Nome, o?.PrecoCentavos ?? 0, o?.Ativa ?? true)).ToList();

    private static CategoriaDto Dto(Categoria c) => new(c.Id, c.Nome, c.Ativa, c.Versao, c.CategoriaPaiId);

    private static ProdutoDto Dto(Produto p, DateTimeOffset agora) => new(
        p.Id,
        p.CategoriaId,
        p.Codigo,
        p.Nome,
        p.PrecoCentavos,
        p.Moeda,
        p.Ativo,
        p.Versao,
        p.Indisponivel,
        p.Promocao is { } promocao ? new PromocaoDto(promocao.PrecoCentavos, promocao.Inicio, promocao.Fim) : null,
        p.PrecoDaUnidadeCentavos,
        p.PrecoEfetivo(agora));

    private static Promocao? Domain(PromocaoDto? dto) => dto is null ? null : new Promocao(dto.PrecoCentavos, dto.Inicio.ToUniversalTime(), dto.Fim.ToUniversalTime());

    private static IResult Erro(ResultadoDoCardapio resultado) => resultado switch
    {
        ResultadoDoCardapio.Negado => Results.StatusCode(StatusCodes.Status403Forbidden),
        ResultadoDoCardapio.NaoEncontrado or ResultadoDoCardapio.CategoriaInexistente => Results.Json(new CardapioErro(Codigo(resultado)), statusCode: StatusCodes.Status404NotFound),
        ResultadoDoCardapio.EmUso or ResultadoDoCardapio.VersaoDesatualizada => Results.Json(new CardapioErro(Codigo(resultado)), statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new CardapioErro(Codigo(resultado)), statusCode: StatusCodes.Status400BadRequest),
    };

    private static string Codigo(ResultadoDoCardapio resultado) => resultado switch
    {
        ResultadoDoCardapio.NaoEncontrado => "nao-encontrado",
        ResultadoDoCardapio.CategoriaInexistente => "categoria-inexistente",
        ResultadoDoCardapio.EmUso => "em-uso",
        ResultadoDoCardapio.NivelInvalido => "nivel-invalido",
        ResultadoDoCardapio.VersaoDesatualizada => "versao-desatualizada",
        _ => "invalido",
    };
}
