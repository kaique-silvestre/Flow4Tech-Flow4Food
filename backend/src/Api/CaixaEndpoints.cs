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
    string? DiferencaTexto,
    long RecebidoDinheiroCentavos,
    string? Situacao = null,
    long? SuprimentosCentavos = null,
    long? SangriasCentavos = null,
    bool EsperadoOculto = false,
    List<ContagemDto>? Contagens = null);

internal sealed record ContagemDto(Guid MetodoId, string MetodoNome, long ContadoCentavos, long EsperadoCentavos, long DiferencaCentavos);

/// <summary>The float is required: an absent field is a 400, never read as zero.</summary>
internal sealed record AbrirTurnoRequest(long? FundoCentavos);

/// <summary>The counted cash is required: an absent field is a 400, never read as zero (a zero count is a real, valid count).</summary>
internal sealed record FecharTurnoRequest(long? ContadoCentavos, List<ContagemRequest>? Contagens = null);

/// <summary>The count of one NON-cash method (the cash count is <c>contadoCentavos</c>).</summary>
internal sealed record ContagemRequest(Guid MetodoId, long ContadoCentavos);

internal sealed record CaixaErro(string Codigo);

internal static class CaixaEndpoints
{
    private const long Body = 4096;

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
                var r = body.FundoCentavos is { } fundo ? await caixa.AbrirAsync(request, fundo, cancellationToken) : new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
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
                var r = body.ContadoCentavos is { } contado ? await caixa.FecharAsync(request, contado, ParaPedidos(body.Contagens), cancellationToken) : new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
                return r.Resultado == ResultadoDoCaixa.Ok ? Results.Ok(Dto(r.Turno!, r.Contagens)) : Erro(r.Resultado);
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

        app.MapCaixaConferencia();
    }

    internal static IReadOnlyList<PedidoDeContagem>? ParaPedidos(List<ContagemRequest>? contagens) => contagens?.Select(c => new PedidoDeContagem(c.MetodoId, c.ContadoCentavos)).ToList();

    internal static TurnoDto Dto(Turno t) => Dto(t, null);

    internal static TurnoDto Dto(Turno t, IReadOnlyList<ContagemDoMetodo>? contagens) =>
        new(
            t.Id,
            t.UnidadeId,
            t.OperadorId,
            t.AbertoEm,
            t.FundoCentavos,
            t.FechadoEm,
            t.ContadoCentavos,
            t.EsperadoCentavos,
            t.DiferencaCentavos,
            t.DiferencaCentavos is { } d ? RegrasDoCaixa.Palavra(d) : null,
            t.RecebidoDinheiroCentavos,
            t.Situacao is { } s ? GestaoDoCaixa.Palavra(s) : null,
            t.SuprimentosCentavos,
            t.SangriasCentavos,
            t.EsperadoOculto,
            contagens?.Select(DtoDaContagem).ToList());

    internal static ContagemDto DtoDaContagem(ContagemDoMetodo c) => new(c.MetodoId, c.MetodoNome, c.ContadoCentavos, c.EsperadoCentavos, c.DiferencaCentavos);

    internal static IResult Erro(ResultadoDoCaixa resultado) => resultado switch
    {
        ResultadoDoCaixa.JaAberto => Results.Json(new CaixaErro("turno-ja-aberto"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDoCaixa.SemTurnoAberto => Results.Json(new CaixaErro("sem-turno-aberto"), statusCode: StatusCodes.Status404NotFound),
        ResultadoDoCaixa.NaoEncontrado => Results.Json(new CaixaErro("nao-encontrado"), statusCode: StatusCodes.Status404NotFound),
        ResultadoDoCaixa.SaldoInsuficiente => Results.Json(new CaixaErro("saldo-insuficiente"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDoCaixa.NaoPendente => Results.Json(new CaixaErro("nao-pendente"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDoCaixa.AutoaprovacaoNegada => Results.Json(new CaixaErro("autoaprovacao-nao-permitida"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDoCaixa.Negado => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.Json(new CaixaErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
