using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Api;

/// <summary><c>Quantidade</c> is how many units of the product a full set needs: only a combo reads it (1 when absent).</summary>
internal sealed record ProdutoDaPromocaoRequest(Guid ProdutoId, int? Quantidade);

/// <summary>
/// <c>Tipo</c> is <c>percentual</c> (<c>Percentual</c>, more than 0 and at most 100, two decimals), <c>valor</c> (<c>ValorCentavos</c> off each unit) or <c>combo</c> (<c>ValorCentavos</c> is the price of one full
/// set, at least two units in all). <c>TodasAsUnidades</c> false means the active unit of the session: the unit never comes from the client. The window is <c>ValidoDe</c> (inclusive) to <c>ValidoAte</c>
/// (exclusive), <c>DiasDaSemana</c> (0 Sunday to 6 Saturday, every day when absent) and an optional <c>HoraInicio</c>/<c>HoraFim</c> ("HH:mm", the unit's local time; a window that ends before it starts crosses
/// midnight). The discount amount is never sent: the server computes it when a round is launched.
/// </summary>
internal sealed record CriarPromocaoRequest(
    string? Nome,
    string? Tipo,
    decimal? Percentual,
    long? ValorCentavos,
    bool TodasAsUnidades,
    bool Ativo,
    DateTimeOffset? ValidoDe,
    DateTimeOffset? ValidoAte,
    List<int>? DiasDaSemana,
    string? HoraInicio,
    string? HoraFim,
    List<ProdutoDaPromocaoRequest>? Produtos);

/// <summary>The type and the products of a rule are fixed once created. <c>Versao</c> is the version the caller read.</summary>
internal sealed record AlterarPromocaoRequest(
    int Versao,
    string? Nome,
    decimal? Percentual,
    long? ValorCentavos,
    bool TodasAsUnidades,
    bool Ativo,
    DateTimeOffset? ValidoDe,
    DateTimeOffset? ValidoAte,
    List<int>? DiasDaSemana,
    string? HoraInicio,
    string? HoraFim);

internal sealed record ProdutoDaRegraDto(Guid ProdutoId, int Quantidade);

/// <summary><c>UnidadeId</c> null (and <c>TodasAsUnidades</c> true) means every unit of the organization.</summary>
internal sealed record PromocaoRegraDto(
    Guid Id,
    Guid? UnidadeId,
    bool TodasAsUnidades,
    string Nome,
    string Tipo,
    decimal? Percentual,
    long? ValorCentavos,
    bool Ativo,
    DateTimeOffset? ValidoDe,
    DateTimeOffset? ValidoAte,
    List<int> DiasDaSemana,
    string? HoraInicio,
    string? HoraFim,
    int Versao,
    List<ProdutoDaRegraDto> Produtos,
    Guid CriadoPor,
    DateTimeOffset CriadoEm,
    Guid AtualizadoPor,
    DateTimeOffset AtualizadoEm);

/// <summary>Codes: <c>invalido</c>, <c>produto-invalido</c> (400), <c>nao-encontrada</c> (404), <c>versao-desatualizada</c> (409, with the current version).</summary>
internal sealed record PromocaoErro(string Codigo, int? VersaoAtual = null);

internal static class PromocoesEndpoints
{
    private const long Body = 4096;

    public static void MapPromocoes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/promocoes", async (RequestContextAccessor request, GestaoDePromocoes gestao, CancellationToken cancellationToken) =>
                Results.Ok((await gestao.ListarAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listPromocoes")
            .RequireSession()
            .RequirePermission(PromocaoPermissions.Ver)
            .Produces<List<PromocaoRegraDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/promocoes/{promocaoId:guid}", async (Guid promocaoId, RequestContextAccessor request, GestaoDePromocoes gestao, CancellationToken cancellationToken) =>
            {
                var promocao = await gestao.AcharAsync(request, promocaoId, cancellationToken);
                return promocao is null ? Results.Json(new PromocaoErro("nao-encontrada"), statusCode: StatusCodes.Status404NotFound) : Results.Ok(Dto(promocao));
            })
            .WithName("getPromocao")
            .RequireSession()
            .RequirePermission(PromocaoPermissions.Ver)
            .Produces<PromocaoRegraDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<PromocaoErro>(StatusCodes.Status404NotFound);

        app.MapPost("/promocoes", async (CriarPromocaoRequest body, RequestContextAccessor request, GestaoDePromocoes gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.CriarAsync(
                    request,
                    new PromocaoParaSalvar(body.Nome, body.Tipo, body.Percentual, body.ValorCentavos, body.TodasAsUnidades, body.Ativo, body.ValidoDe, body.ValidoAte, body.DiasDaSemana, body.HoraInicio, body.HoraFim, body.Produtos?.Select(p => new ProdutoDaPromocao(p.ProdutoId, p.Quantidade ?? 1)).ToList()),
                    cancellationToken);
                return r.Resultado == ResultadoDePromocao.Ok ? Results.Created($"/promocoes/{r.Promocao!.Id:D}", Dto(r.Promocao)) : Erro(r);
            })
            .WithName("criarPromocao")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(PromocaoPermissions.Gerenciar)
            .Produces<PromocaoRegraDto>(StatusCodes.Status201Created)
            .Produces<PromocaoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPut("/promocoes/{promocaoId:guid}", async (Guid promocaoId, AlterarPromocaoRequest body, RequestContextAccessor request, GestaoDePromocoes gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.AlterarAsync(
                    request,
                    promocaoId,
                    body.Versao,
                    new PromocaoParaSalvar(body.Nome, null, body.Percentual, body.ValorCentavos, body.TodasAsUnidades, body.Ativo, body.ValidoDe, body.ValidoAte, body.DiasDaSemana, body.HoraInicio, body.HoraFim, null),
                    cancellationToken);
                return r.Resultado == ResultadoDePromocao.Ok ? Results.Ok(Dto(r.Promocao!)) : Erro(r);
            })
            .WithName("alterarPromocao")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(PromocaoPermissions.Gerenciar)
            .Produces<PromocaoRegraDto>()
            .Produces<PromocaoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<PromocaoErro>(StatusCodes.Status404NotFound)
            .Produces<PromocaoErro>(StatusCodes.Status409Conflict);
    }

    private static PromocaoRegraDto Dto(Promocao p) =>
        new(
            p.Id,
            p.UnidadeId,
            p.UnidadeId is null,
            p.Nome,
            RegrasDePromocao.Palavra(p.Tipo),
            p.Percentual,
            p.ValorCentavos,
            p.Ativo,
            p.ValidoDe,
            p.ValidoAte,
            [.. RegrasDePromocao.DiasDaMascara(p.DiasDaSemana)],
            p.HoraInicio is { } i ? RegrasDePromocao.Texto(i) : null,
            p.HoraFim is { } f ? RegrasDePromocao.Texto(f) : null,
            p.Versao,
            [.. p.Produtos.Select(x => new ProdutoDaRegraDto(x.ProdutoId, x.Quantidade))],
            p.CriadoPor,
            p.CriadoEm,
            p.AtualizadoPor,
            p.AtualizadoEm);

    private static IResult Erro(ResultadoDePromocaoComDados r) => r.Resultado switch
    {
        ResultadoDePromocao.NaoEncontrada => Results.Json(new PromocaoErro("nao-encontrada"), statusCode: StatusCodes.Status404NotFound),
        ResultadoDePromocao.VersaoDesatualizada => Results.Json(new PromocaoErro("versao-desatualizada", r.VersaoAtual), statusCode: StatusCodes.Status409Conflict),
        ResultadoDePromocao.ProdutoInvalido => Results.Json(new PromocaoErro("produto-invalido"), statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new PromocaoErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
