using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Api;

/// <summary>
/// Exactly one of <c>Percentual</c> (more than 0 and at most 100, two decimals) or <c>ValorCentavos</c> (1 or more); <c>ItemId</c> absent means the whole comanda; <c>Motivo</c> is required (up to 200
/// characters). The amount taken is computed on the server from the stored comanda: the client never says how much.
/// </summary>
internal sealed record AplicarDescontoRequest(int Versao, Guid? ItemId, decimal? Percentual, long? ValorCentavos, string Motivo);

/// <summary><c>Motivo</c> is required (up to 200 characters).</summary>
internal sealed record DarComoRequest(int Versao, string Motivo);

internal sealed record DescontoDto(Guid Id, Guid ComandaId, Guid? ItemId, string Forma, decimal? Percentual, long? ValorCentavos, long AplicadoCentavos, string Motivo, Guid CriadoPor, DateTimeOffset CriadoEm);

/// <summary>The discount, the bill after it and the comanda version the next write carries.</summary>
internal sealed record DescontoAplicadoDto(DescontoDto Desconto, ContaDto Conta);

/// <summary>The item after the change (zero charge, reason kept) and the bill after it.</summary>
internal sealed record ItemDadoDto(ItemDto Item, ContaDto Conta);

/// <summary>Whether the session holds ANY rule for each action; never the limits. The app hides what would be refused; the server still decides each operation.</summary>
internal sealed record RegrasDeDescontoResponse(bool Desconto, bool Cortesia, bool Brinde);

/// <summary>
/// The three POST routes declare the plain <c>comanda:ver</c> (a route-level decision carries no operation values, so a permission with a limit would always be refused there). The LIMITED permission
/// (<c>comanda:desconto|cortesia|brinde</c>) is asked of the engine by the Application handler with the percent and the value computed from the stored comanda; a refusal there is 403 without reason and an
/// <c>acesso:negar</c> audit record, exactly like <c>cardapio:preco-alterar</c>.
/// </summary>
internal static class ComandasDescontosEndpoints
{
    private const long Body = 2048;

    public static void MapDescontosDaComanda(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/sessao/regras-de-desconto", async (RequestContextAccessor request, GestaoDeDescontos gestao, CancellationToken cancellationToken) =>
            {
                var regras = await gestao.RegrasDoOperadorAsync(request, cancellationToken);
                return Results.Ok(new RegrasDeDescontoResponse(regras.Desconto, regras.Cortesia, regras.Brinde));
            })
            .WithName("getRegrasDeDesconto")
            .RequireSession()
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<RegrasDeDescontoResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/comandas/{comandaId:guid}/descontos", async (Guid comandaId, AplicarDescontoRequest body, RequestContextAccessor request, GestaoDeDescontos gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.AplicarAsync(request, comandaId, body.Versao, body.ItemId, body.Percentual, body.ValorCentavos, body.Motivo, cancellationToken);
                return r.Resultado == ResultadoDoDesconto.Ok ? Results.Created($"/comandas/{comandaId:D}/descontos", new DescontoAplicadoDto(Dto(r.Desconto!), Conta(r))) : Erro(r);
            })
            .WithName("aplicarDescontoNaComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<DescontoAplicadoDto>(StatusCodes.Status201Created)
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapGet("/comandas/{comandaId:guid}/descontos", async (Guid comandaId, RequestContextAccessor request, GestaoDeDescontos gestao, CancellationToken cancellationToken) =>
            {
                var lista = await gestao.ListarAsync(request, comandaId, cancellationToken);
                return lista is null ? Results.Json(new ComandaErro("nao-encontrada"), statusCode: StatusCodes.Status404NotFound) : Results.Ok(lista.Select(Dto).ToList());
            })
            .WithName("listDescontosDaComanda")
            .RequireSession()
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<List<DescontoDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound);

        app.MapPost("/comandas/{comandaId:guid}/itens/{itemId:guid}/cortesia", async (Guid comandaId, Guid itemId, DarComoRequest body, RequestContextAccessor request, GestaoDeDescontos gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.DarCortesiaAsync(request, comandaId, itemId, body.Versao, body.Motivo, cancellationToken);
                return r.Resultado == ResultadoDoDesconto.Ok ? Results.Ok(new ItemDadoDto(ComandasEndpoints.ItemParaDto(r.Item!), Conta(r))) : Erro(r);
            })
            .WithName("darCortesiaNoItem")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<ItemDadoDto>()
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/comandas/{comandaId:guid}/itens/{itemId:guid}/brinde", async (Guid comandaId, Guid itemId, DarComoRequest body, RequestContextAccessor request, GestaoDeDescontos gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.DarBrindeAsync(request, comandaId, itemId, body.Versao, body.Motivo, cancellationToken);
                return r.Resultado == ResultadoDoDesconto.Ok ? Results.Ok(new ItemDadoDto(ComandasEndpoints.ItemParaDto(r.Item!), Conta(r))) : Erro(r);
            })
            .WithName("darBrindeNoItem")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<ItemDadoDto>()
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);
    }

    private static DescontoDto Dto(Desconto d) =>
        new(d.Id, d.ComandaId, d.ItemId, RegrasDeDesconto.Palavra(d.Forma), d.Percentual, d.ValorCentavos, d.AplicadoCentavos, d.Motivo, d.CriadoPor, d.CriadoEm);

    private static ContaDto Conta(ResultadoDeDescontoDaComanda r) =>
        new(r.Conta!.ConsumoCentavos, r.Conta.TaxaServicoCentavos, r.Conta.TotalCentavos, r.Conta.PagoCentavos, r.Conta.SaldoCentavos, r.Conta.TaxaServicoRemovida, r.Conta.TaxaServicoPercentual, r.Comanda!.Versao, RegrasDeComanda.Palavra(r.Comanda.Estado), r.Conta.DescontoCentavos);

    private static IResult Erro(ResultadoDeDescontoDaComanda r)
    {
        if (r.Resultado == ResultadoDoDesconto.Negado)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var (status, codigo) = r.Resultado switch
        {
            ResultadoDoDesconto.ComandaNaoEncontrada or ResultadoDoDesconto.ItemNaoEncontrado => (StatusCodes.Status404NotFound, "nao-encontrada"),
            ResultadoDoDesconto.ComandaNaoAberta => (StatusCodes.Status409Conflict, "comanda-nao-aberta"),
            ResultadoDoDesconto.ItemJaCancelado => (StatusCodes.Status409Conflict, "item-ja-cancelado"),
            ResultadoDoDesconto.ItemNaoNormal => (StatusCodes.Status409Conflict, "item-nao-normal"),
            ResultadoDoDesconto.DescontoExcede => (StatusCodes.Status409Conflict, "desconto-excede"),
            ResultadoDoDesconto.ValorJaRecebido => (StatusCodes.Status409Conflict, "valor-ja-recebido"),
            ResultadoDoDesconto.VersaoDesatualizada => (StatusCodes.Status409Conflict, "versao-desatualizada"),
            _ => (StatusCodes.Status400BadRequest, "invalido"),
        };
        return Results.Json(new ComandaErro(codigo, r.VersaoAtual), statusCode: status);
    }
}
