using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Api;

/// <summary>A payment method of the organization. <c>Chave</c> is the stable key of a factory method (null for one the organization created); <c>Dinheiro</c> marks cash (the only one with change).</summary>
internal sealed record MetodoDePagamentoDto(Guid Id, string? Chave, string Nome, bool Dinheiro, bool Ativo, int Ordem);

internal sealed record CriarMetodoRequest(string? Nome);

internal sealed record AlterarMetodoRequest(string? Nome, bool Ativo);

/// <summary>Codes: <c>invalido</c> (400), <c>nao-encontrado</c> (404), <c>ja-existe</c>, <c>indisponivel-na-fase</c> (409).</summary>
internal sealed record MetodoErro(string Codigo);

/// <summary>
/// <c>ValorCentavos</c> absent means a total refund (all that is still refundable); present it is 1 or more and at most the refundable balance. <c>Motivo</c> is required (up to 200 characters). The limit and the shift
/// are never in the request: the server measures the one and picks the other.
/// </summary>
internal sealed record EstornarPagamentoRequest(long? ValorCentavos, string Motivo);

/// <summary>A refund. <c>ValorCentavos</c> is NEGATIVE: the cents handed back.</summary>
internal sealed record EstornoDto(Guid Id, Guid PagamentoId, Guid ComandaId, Guid? TurnoId, string Metodo, long ValorCentavos, string Motivo, Guid OperadorId, DateTimeOffset RegistradoEm);

/// <summary>The refund written and what is still refundable of the payment afterwards.</summary>
internal sealed record EstornoFeitoDto(EstornoDto Estorno, long SaldoEstornavelCentavos);

/// <summary>A payment with its refunds: what it applied, what was refunded (positive total) and what is still refundable.</summary>
internal sealed record EstornosDoPagamentoDto(Guid PagamentoId, Guid ComandaId, long AplicadoCentavos, long EstornadoCentavos, long SaldoEstornavelCentavos, List<EstornoDto> Estornos);

/// <summary>Codes: <c>invalido</c>, <c>excede-saldo-estornavel</c> (400), <c>negado</c> (403), <c>nao-encontrado</c> (404), <c>pagamento-nao-confirmado</c>, <c>sem-turno-aberto</c>, <c>caixa-sem-dinheiro</c> (409).</summary>
internal sealed record EstornoErro(string Codigo, long? SaldoEstornavelCentavos = null);

internal static class PagamentosEndpoints
{
    private const long Body = 1024;

    public static void MapPagamentos(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The route declares the plain pagamento:ver: a route-level decision carries no operation values, so the LIMITED pagamento:estornar would always be refused there. The use case asks the engine with the
        // cents it measured from the stored payment (ADR-018 pattern); a refusal is 403 (typed, no reason) with an acesso:negar audit record.
        app.MapPost("/pagamentos/{pagamentoId:guid}/estornos", async (Guid pagamentoId, EstornarPagamentoRequest body, RequestContextAccessor request, GestaoDePagamentos gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.EstornarAsync(request, pagamentoId, body.ValorCentavos, body.Motivo, cancellationToken);
                return r.Resultado == ResultadoDoEstorno.Ok
                    ? Results.Created($"/pagamentos/{pagamentoId:D}/estornos", new EstornoFeitoDto(Dto(r.Estornos[0]), r.SaldoEstornavelCentavos))
                    : Erro(r);
            })
            .WithName("estornarPagamento")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(PagamentosPermissions.Ver)
            .Produces<EstornoFeitoDto>(StatusCodes.Status201Created)
            .Produces<EstornoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<EstornoErro>(StatusCodes.Status403Forbidden)
            .Produces<EstornoErro>(StatusCodes.Status404NotFound)
            .Produces<EstornoErro>(StatusCodes.Status409Conflict);

        app.MapGet("/pagamentos/{pagamentoId:guid}/estornos", async (Guid pagamentoId, RequestContextAccessor request, GestaoDePagamentos gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.ListarEstornosAsync(request, pagamentoId, cancellationToken);
                return r is null
                    ? Results.Json(new EstornoErro("nao-encontrado"), statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(new EstornosDoPagamentoDto(r.Pagamento.Id, r.Pagamento.ComandaId, r.Pagamento.AplicadoCentavos, r.EstornadoCentavos, r.SaldoEstornavelCentavos, [.. r.Estornos.Select(Dto)]));
            })
            .WithName("listEstornosDoPagamento")
            .RequireSession()
            .RequirePermission(PagamentosPermissions.Ver)
            .Produces<EstornosDoPagamentoDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<EstornoErro>(StatusCodes.Status404NotFound);

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

    private static EstornoDto Dto(EstornoFeito e) => new(e.Id, e.PagamentoId, e.ComandaId, e.TurnoId, e.Metodo, e.ValorCentavos, e.Motivo, e.OperadorId, e.RegistradoEm);

    private static IResult Erro(ResultadoDeEstorno r)
    {
        var (status, codigo) = r.Resultado switch
        {
            ResultadoDoEstorno.NaoEncontrado => (StatusCodes.Status404NotFound, "nao-encontrado"),
            ResultadoDoEstorno.ExcedeSaldoEstornavel => (StatusCodes.Status400BadRequest, "excede-saldo-estornavel"),
            ResultadoDoEstorno.NaoConfirmado => (StatusCodes.Status409Conflict, "pagamento-nao-confirmado"),
            ResultadoDoEstorno.SemTurnoAberto or ResultadoDoEstorno.TurnoEncerrado => (StatusCodes.Status409Conflict, "sem-turno-aberto"),
            ResultadoDoEstorno.CaixaSemDinheiro => (StatusCodes.Status409Conflict, "caixa-sem-dinheiro"),
            ResultadoDoEstorno.Negado => (StatusCodes.Status403Forbidden, "negado"),
            _ => (StatusCodes.Status400BadRequest, "invalido"),
        };
        return Results.Json(new EstornoErro(codigo, r.Resultado == ResultadoDoEstorno.ExcedeSaldoEstornavel ? r.SaldoEstornavelCentavos : null), statusCode: status);
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
