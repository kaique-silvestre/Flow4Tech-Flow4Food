using Flow4Tech.Caixa.Application;
using Flow4Tech.Caixa.Domain;

namespace Flow4Tech.Api;

internal sealed record MovimentoDto(Guid Id, Guid TurnoId, string Tipo, long ValorCentavos, string Motivo, string? Destino, Guid OperadorId, DateTimeOffset CriadoEm);

internal sealed record AprovacaoDto(Guid Id, string Tipo, Guid AprovadorId, string Motivo, long DiferencaAntesCentavos, long DiferencaDepoisCentavos, DateTimeOffset CriadoEm);

internal sealed record EsperadoDoMetodoDto(Guid MetodoId, string MetodoNome, long? EsperadoCentavos);

/// <summary>The open shift before counting. With <c>cego</c> the expected cash and the expected value of each method are null (and <c>turno.recebidoDinheiroCentavos</c> is 0).</summary>
internal sealed record ResumoDoTurnoDto(TurnoDto Turno, bool Cego, long SuprimentosCentavos, long SangriasCentavos, long? EsperadoCentavos, List<EsperadoDoMetodoDto> PorMetodo);

internal sealed record TurnoDetalheDto(TurnoDto Turno, List<ContagemDto> Contagens, List<MovimentoDto> Movimentos, List<AprovacaoDto> Aprovacoes);

/// <summary>Value in cents, reason and (required for a withdrawal) destination. Absent fields are 400.</summary>
internal sealed record MovimentoRequest(long? ValorCentavos, string? Motivo, string? Destino);

internal sealed record AprovarDiferencaRequest(string? Motivo);

internal sealed record RecontarDiferencaRequest(long? ContadoCentavos, string? Motivo, List<ContagemRequest>? Contagens);

/// <summary>Supply, withdrawal, the blind summary, the history and the approval of a difference (F4-0065). The route declares the plain permission; the limited one is asked of the engine in the handler.</summary>
internal static class CaixaConferenciaEndpoints
{
    private const long Body = 4096;

    public static void MapCaixaConferencia(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/caixa/turnos/atual/resumo", async (RequestContextAccessor request, GestaoDoCaixa caixa, CancellationToken cancellationToken) =>
                await caixa.ResumoAsync(request, cancellationToken) is { } r
                    ? Results.Ok(new ResumoDoTurnoDto(CaixaEndpoints.Dto(r.Turno), r.Cego, r.SuprimentosCentavos, r.SangriasCentavos, r.EsperadoCentavos, [.. r.PorMetodo.Select(m => new EsperadoDoMetodoDto(m.MetodoId, m.MetodoNome, m.EsperadoCentavos))]))
                    : CaixaEndpoints.Erro(ResultadoDoCaixa.SemTurnoAberto))
            .WithName("getResumoDoTurno")
            .RequireSession()
            .RequirePermission(CaixaPermissions.Ver)
            .Produces<ResumoDoTurnoDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status404NotFound);

        app.MapGet("/caixa/turnos/atual/movimentos", async (RequestContextAccessor request, GestaoDeMovimentosDoCaixa movimentos, CancellationToken cancellationToken) =>
                Results.Ok((await movimentos.ListarAsync(request, cancellationToken)).Select(Dto).ToList()))
            .WithName("listMovimentosDoTurno")
            .RequireSession()
            .RequirePermission(CaixaPermissions.Ver)
            .Produces<List<MovimentoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/caixa/turnos/atual/suprimentos", async (MovimentoRequest body, RequestContextAccessor request, GestaoDeMovimentosDoCaixa movimentos, CancellationToken cancellationToken) =>
                Resposta(body.ValorCentavos is { } valor ? await movimentos.SuprirAsync(request, valor, body.Motivo, body.Destino, cancellationToken) : new ResultadoDoMovimento(ResultadoDoCaixa.Invalido)))
            .WithName("suprirCaixa")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CaixaPermissions.Ver)
            .Produces<MovimentoDto>(StatusCodes.Status201Created)
            .Produces<CaixaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status404NotFound);

        app.MapPost("/caixa/turnos/atual/sangrias", async (MovimentoRequest body, RequestContextAccessor request, GestaoDeMovimentosDoCaixa movimentos, CancellationToken cancellationToken) =>
                Resposta(body.ValorCentavos is { } valor ? await movimentos.SangrarAsync(request, valor, body.Motivo, body.Destino, cancellationToken) : new ResultadoDoMovimento(ResultadoDoCaixa.Invalido)))
            .WithName("sangrarCaixa")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CaixaPermissions.Ver)
            .Produces<MovimentoDto>(StatusCodes.Status201Created)
            .Produces<CaixaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status404NotFound)
            .Produces<CaixaErro>(StatusCodes.Status409Conflict);

        app.MapGet("/caixa/historico", async (Guid? operadorId, DateTimeOffset? de, DateTimeOffset? ate, RequestContextAccessor request, GestaoDoCaixa caixa, CancellationToken cancellationToken) =>
                de is { } a && ate is { } b && b <= a
                    ? CaixaEndpoints.Erro(ResultadoDoCaixa.Invalido)
                    : Results.Ok((await caixa.HistoricoAsync(request, operadorId, de, ate, null, cancellationToken)).Select(CaixaEndpoints.Dto).ToList()))
            .WithName("getHistoricoDoCaixa")
            .RequireSession()
            .RequirePermission(CaixaPermissions.Ver)
            .Produces<List<TurnoDto>>()
            .Produces<CaixaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/caixa/turnos/pendentes", async (RequestContextAccessor request, GestaoDaDiferenca diferencas, CancellationToken cancellationToken) =>
                Results.Ok((await diferencas.PendentesAsync(request, cancellationToken)).Select(CaixaEndpoints.Dto).ToList()))
            .WithName("listTurnosPendentes")
            .RequireSession()
            .RequirePermission(CaixaPermissions.VerTodos)
            .Produces<List<TurnoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/caixa/turnos/{id:guid}", async (Guid id, RequestContextAccessor request, GestaoDoCaixa caixa, CancellationToken cancellationToken) =>
                await caixa.DetalheAsync(request, id, cancellationToken) is { } d
                    ? Results.Ok(new TurnoDetalheDto(CaixaEndpoints.Dto(d.Turno), [.. d.Contagens.Select(CaixaEndpoints.DtoDaContagem)], [.. d.Movimentos.Select(Dto)], [.. d.Aprovacoes.Select(Dto)]))
                    : CaixaEndpoints.Erro(ResultadoDoCaixa.NaoEncontrado))
            .WithName("getTurno")
            .RequireSession()
            .RequirePermission(CaixaPermissions.Ver)
            .Produces<TurnoDetalheDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status404NotFound);

        app.MapPost("/caixa/turnos/{id:guid}/diferenca/aprovar", async (Guid id, AprovarDiferencaRequest body, RequestContextAccessor request, GestaoDaDiferenca diferencas, CancellationToken cancellationToken) =>
                Resposta(await diferencas.AprovarAsync(request, id, body.Motivo, cancellationToken)))
            .WithName("aprovarDiferenca")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CaixaPermissions.VerTodos, sensitive: true)
            .Produces<TurnoDto>()
            .Produces<CaixaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status404NotFound)
            .Produces<CaixaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/caixa/turnos/{id:guid}/diferenca/recontar", async (Guid id, RecontarDiferencaRequest body, RequestContextAccessor request, GestaoDaDiferenca diferencas, CancellationToken cancellationToken) =>
                Resposta(body.ContadoCentavos is { } contado ? await diferencas.RecontarAsync(request, id, body.Motivo, contado, CaixaEndpoints.ParaPedidos(body.Contagens), cancellationToken) : new ResultadoDoTurno(ResultadoDoCaixa.Invalido)))
            .WithName("recontarDiferenca")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(CaixaPermissions.VerTodos, sensitive: true)
            .Produces<TurnoDto>()
            .Produces<CaixaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<CaixaErro>(StatusCodes.Status404NotFound)
            .Produces<CaixaErro>(StatusCodes.Status409Conflict);
    }

    private static MovimentoDto Dto(MovimentoDoCaixa m) => new(m.Id, m.TurnoId, m.Tipo == TipoDeMovimento.Sangria ? "sangria" : "suprimento", m.ValorCentavos, m.Motivo, m.Destino, m.OperadorId, m.CriadoEm);

    private static AprovacaoDto Dto(AprovacaoDeDiferenca a) => new(a.Id, a.Tipo, a.AprovadorId, a.Motivo, a.DiferencaAntesCentavos, a.DiferencaDepoisCentavos, a.CriadoEm);

    private static IResult Resposta(ResultadoDoMovimento r) =>
        r.Resultado == ResultadoDoCaixa.Ok ? Results.Json(Dto(r.Movimento!), statusCode: StatusCodes.Status201Created) : CaixaEndpoints.Erro(r.Resultado);

    private static IResult Resposta(ResultadoDoTurno r) =>
        r.Resultado == ResultadoDoCaixa.Ok ? Results.Ok(CaixaEndpoints.Dto(r.Turno!, r.Contagens)) : CaixaEndpoints.Erro(r.Resultado);
}
