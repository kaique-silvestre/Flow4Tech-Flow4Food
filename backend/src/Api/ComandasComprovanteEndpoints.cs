using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Api;

/// <summary>One launched item: <c>UnitarioCentavos</c> is the frozen product price plus the frozen options; <c>TotalCentavos</c> is what the line charges (zero when <c>Tipo</c> is cortesia or brinde).</summary>
internal sealed record ComprovanteItemDto(string Descricao, int Quantidade, long UnitarioCentavos, long TotalCentavos, string Tipo, string? Observacao, List<string> Opcoes);

/// <summary>A discount given. <c>Alvo</c> is the item name, or "Comanda" for the whole bill.</summary>
internal sealed record ComprovanteDescontoDto(string Alvo, string Forma, decimal? Percentual, long AplicadoCentavos, string Motivo);

/// <summary>The bill in cents, in the fixed order of ADR-018: consumption, discounts, service fee on what is left, total, paid and balance.</summary>
internal sealed record ComprovanteContaDto(long ConsumoCentavos, long DescontoCentavos, long TaxaServicoCentavos, bool TaxaServicoRemovida, decimal TaxaServicoPercentual, long TotalCentavos, long PagoCentavos, long SaldoCentavos);

/// <summary>A payment line without who took it. <c>DivisaoParte</c> is the split part it paid.</summary>
internal sealed record ComprovantePagamentoDto(string MetodoNome, long AplicadoCentavos, long TrocoCentavos, long GorjetaCentavos, DateTimeOffset RegistradoEm, int? DivisaoParte);

/// <summary>A part of the recorded split: what it owes of the bill, its share of the tip, and what was already applied to it.</summary>
internal sealed record ComprovanteParteDto(int Numero, long ConsumoCentavos, long TaxaServicoCentavos, long TotalCentavos, long GorjetaCentavos, long PagoCentavos);

/// <summary>
/// The pre-bill (<c>Tipo</c> pre-conta) or the second copy of the receipt (<c>Tipo</c> recibo) of a comanda. Not a fiscal document (<c>Aviso</c> says so). <c>Partes</c> is set only when a split exists and still
/// matches the bill; <c>ContaPedidaEm</c> is the "bill requested" mark. It holds nothing the comanda does not hold: no account, waiter or secret.
/// </summary>
internal sealed record ComprovanteDto(
    string Tipo,
    bool SegundaVia,
    string Aviso,
    int Numero,
    string NumeroTexto,
    string Identificacao,
    DateOnly DiaOperacional,
    DateTimeOffset AbertaEm,
    DateTimeOffset? FechadaEm,
    DateTimeOffset? ContaPedidaEm,
    DateTimeOffset GeradaEm,
    List<ComprovanteItemDto> Itens,
    List<ComprovanteDescontoDto> Descontos,
    ComprovanteContaDto Conta,
    List<ComprovantePagamentoDto> Pagamentos,
    List<ComprovanteParteDto>? Partes);

internal static class ComandasComprovanteEndpoints
{
    private const string Aviso = "Sem valor fiscal";

    public static void MapComprovanteDaComanda(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/comandas/{comandaId:guid}/pre-conta", async (Guid comandaId, RequestContextAccessor request, GestaoDoComprovante gestao, CancellationToken cancellationToken) =>
                Resposta(await gestao.PreContaAsync(request, comandaId, cancellationToken), c => Results.Ok(Dto(c))))
            .WithName("getPreContaDaComanda")
            .RequireSession()
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<ComprovanteDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapGet("/comandas/{comandaId:guid}/pre-conta/texto", async (Guid comandaId, RequestContextAccessor request, GestaoDoComprovante gestao, CancellationToken cancellationToken) =>
                Resposta(await gestao.PreContaAsync(request, comandaId, cancellationToken), c => Results.Text(RegrasDeComprovante.Texto(c), "text/plain; charset=utf-8")))
            .WithName("getPreContaEmTextoDaComanda")
            .RequireSession()
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/comandas/{comandaId:guid}/pedir-conta", async (Guid comandaId, RequestContextAccessor request, GestaoDoComprovante gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.PedirContaAsync(request, comandaId, cancellationToken);
                return r.Resultado switch
                {
                    ResultadoDoComprovante.Ok => Results.Json(ComandasEndpoints.Dto(r.Comanda!), statusCode: StatusCodes.Status201Created),
                    ResultadoDoComprovante.Repetida => Results.Ok(ComandasEndpoints.Dto(r.Comanda!)),
                    _ => Erro(r),
                };
            })
            .WithName("pedirContaDaComanda")
            .RequireSession()
            .WithBodyLimit(1024)
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<ComandaDto>(StatusCodes.Status201Created)
            .Produces<ComandaDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapGet("/comandas/{comandaId:guid}/recibo", async (Guid comandaId, RequestContextAccessor request, GestaoDoComprovante gestao, CancellationToken cancellationToken) =>
                Resposta(await gestao.ReciboAsync(request, comandaId, cancellationToken), c => Results.Ok(Dto(c))))
            .WithName("getReciboDaComanda")
            .RequireSession()
            .RequirePermission(ComandaPermissions.Reimprimir)
            .Produces<ComprovanteDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapGet("/comandas/{comandaId:guid}/recibo/texto", async (Guid comandaId, RequestContextAccessor request, GestaoDoComprovante gestao, CancellationToken cancellationToken) =>
                Resposta(await gestao.ReciboAsync(request, comandaId, cancellationToken), c => Results.Text(RegrasDeComprovante.Texto(c), "text/plain; charset=utf-8")))
            .WithName("getReciboEmTextoDaComanda")
            .RequireSession()
            .RequirePermission(ComandaPermissions.Reimprimir)
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);
    }

    private static IResult Resposta(ResultadoDoComprovanteCompleto r, Func<Comprovante, IResult> ok) =>
        r.Resultado == ResultadoDoComprovante.Ok ? ok(r.Comprovante!) : Erro(r);

    private static IResult Erro(ResultadoDoComprovanteCompleto r) => r.Resultado switch
    {
        ResultadoDoComprovante.ComandaNaoEncontrada => Results.Json(new ComandaErro("nao-encontrada"), statusCode: StatusCodes.Status404NotFound),
        ResultadoDoComprovante.ComandaNaoFechada => Results.Json(new ComandaErro("comanda-nao-fechada"), statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new ComandaErro("comanda-nao-aberta"), statusCode: StatusCodes.Status409Conflict),
    };

    private static ComprovanteDto Dto(Comprovante c) =>
        new(
            c.Tipo == TipoDeComprovante.PreConta ? "pre-conta" : "recibo",
            c.SegundaVia,
            Aviso,
            c.Numero,
            RegrasDeComanda.NumeroDeExibicao(c.Numero),
            c.Identificacao,
            c.DiaOperacional,
            c.AbertaEm,
            c.FechadaEm,
            c.ContaPedidaEm,
            c.GeradaEm,
            [.. c.Itens.Select(i => new ComprovanteItemDto(i.Descricao, i.Quantidade, i.UnitarioCentavos, i.TotalCentavos, i.Tipo, i.Observacao, [.. i.Opcoes]))],
            [.. c.Descontos.Select(d => new ComprovanteDescontoDto(d.Alvo, d.Forma, d.Percentual, d.AplicadoCentavos, d.Motivo))],
            new ComprovanteContaDto(c.Conta.ConsumoCentavos, c.Conta.DescontoCentavos, c.Conta.TaxaServicoCentavos, c.Conta.TaxaServicoRemovida, c.Conta.TaxaServicoPercentual, c.Conta.TotalCentavos, c.Conta.PagoCentavos, c.Conta.SaldoCentavos),
            [.. c.Pagamentos.Select(p => new ComprovantePagamentoDto(p.MetodoNome, p.AplicadoCentavos, p.TrocoCentavos, p.GorjetaCentavos, p.RegistradoEm, p.DivisaoParte))],
            c.Partes is null ? null : [.. c.Partes.Select(p => new ComprovanteParteDto(p.Numero, p.ConsumoCentavos, p.TaxaServicoCentavos, p.TotalCentavos, p.GorjetaCentavos, p.PagoCentavos))]);
}
