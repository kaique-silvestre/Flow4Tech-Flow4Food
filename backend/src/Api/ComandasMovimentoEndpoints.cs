using Flow4Tech.Comandas.Application;

namespace Flow4Tech.Api;

/// <summary>Moves the selected launched items (1 to 200 distinct ids) from the comanda of the route to <c>DestinoId</c>. <c>Versao</c> and <c>DestinoVersao</c> are the versions the screen last saw of each comanda.</summary>
internal sealed record TransferirItensRequest(int Versao, Guid DestinoId, int DestinoVersao, List<Guid> ItemIds);

/// <summary>Merges <c>OrigemId</c> into the comanda of the route: its launched items move here and it closes as merged. <c>Versao</c> is the version of the route's comanda, <c>OrigemVersao</c> of the merged one.</summary>
internal sealed record JuntarComandasRequest(int Versao, Guid OrigemId, int OrigemVersao);

/// <summary>Separates the selected launched items of the comanda of the route into a new comanda; <c>Identificacao</c> is optional (default "Comanda N").</summary>
internal sealed record SepararItensRequest(int Versao, List<Guid> ItemIds, string? Identificacao);

/// <summary>Both comandas after the write (new versions) and how many items changed comanda. For a separation <c>Destino</c> is the new comanda; for a merge <c>Origem</c> is the merged one (state "juntada").</summary>
internal sealed record MovimentoDeItensDto(ComandaDto Origem, ComandaDto Destino, int ItensMovidos);

/// <summary>On a stale version <c>ComandaId</c> is the comanda whose version was stale and <c>VersaoAtual</c> its current version; otherwise <c>ComandaId</c> is the comanda that caused the refusal when there is one.</summary>
internal sealed record MovimentoErro(string Codigo, Guid? ComandaId = null, int? VersaoAtual = null);

internal static class ComandasMovimentoEndpoints
{
    private const long Body = 8192;

    public static void MapMovimentoDeItens(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/comandas/{comandaId:guid}/itens/transferir", async (Guid comandaId, TransferirItensRequest body, RequestContextAccessor request, GestaoDaTransferenciaDeItens gestao, CancellationToken cancellationToken) =>
                Resposta(await gestao.TransferirAsync(request, comandaId, body.Versao, body.DestinoId, body.DestinoVersao, body.ItemIds, cancellationToken)))
            .WithName("transferirItensDaComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.TransferirItem)
            .Produces<MovimentoDeItensDto>()
            .Produces<MovimentoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MovimentoErro>(StatusCodes.Status404NotFound)
            .Produces<MovimentoErro>(StatusCodes.Status409Conflict);

        app.MapPost("/comandas/{comandaId:guid}/juntar", async (Guid comandaId, JuntarComandasRequest body, RequestContextAccessor request, GestaoDaTransferenciaDeItens gestao, CancellationToken cancellationToken) =>
                Resposta(await gestao.JuntarAsync(request, comandaId, body.Versao, body.OrigemId, body.OrigemVersao, cancellationToken)))
            .WithName("juntarComandas")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.Juntar)
            .Produces<MovimentoDeItensDto>()
            .Produces<MovimentoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MovimentoErro>(StatusCodes.Status404NotFound)
            .Produces<MovimentoErro>(StatusCodes.Status409Conflict);

        app.MapPost("/comandas/{comandaId:guid}/itens/separar", async (Guid comandaId, SepararItensRequest body, RequestContextAccessor request, GestaoDaTransferenciaDeItens gestao, CancellationToken cancellationToken) =>
                Resposta(await gestao.SepararAsync(request, comandaId, body.Versao, body.ItemIds, body.Identificacao, cancellationToken), StatusCodes.Status201Created))
            .WithName("separarItensDaComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.TransferirItem)
            .Produces<MovimentoDeItensDto>(StatusCodes.Status201Created)
            .Produces<MovimentoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<MovimentoErro>(StatusCodes.Status404NotFound)
            .Produces<MovimentoErro>(StatusCodes.Status409Conflict);
    }

    private static IResult Resposta(ResultadoDoMovimentoDeItens r, int statusDeSucesso = StatusCodes.Status200OK)
    {
        if (r.Resultado == ResultadoDoMovimento.Ok)
        {
            return Results.Json(new MovimentoDeItensDto(ComandasEndpoints.Dto(r.Origem!), ComandasEndpoints.Dto(r.Destino!), r.ItensMovidos), statusCode: statusDeSucesso);
        }

        var (status, codigo) = r.Resultado switch
        {
            ResultadoDoMovimento.ComandaNaoEncontrada or ResultadoDoMovimento.ItemNaoEncontrado => (StatusCodes.Status404NotFound, "nao-encontrada"),
            ResultadoDoMovimento.ComandaNaoAberta => (StatusCodes.Status409Conflict, "comanda-nao-aberta"),
            ResultadoDoMovimento.ItemJaCancelado => (StatusCodes.Status409Conflict, "item-ja-cancelado"),
            ResultadoDoMovimento.ItemComDesconto => (StatusCodes.Status409Conflict, "item-com-desconto"),
            ResultadoDoMovimento.ComDesconto => (StatusCodes.Status409Conflict, "comanda-com-desconto"),
            ResultadoDoMovimento.ValorJaRecebido => (StatusCodes.Status409Conflict, "valor-ja-recebido"),
            ResultadoDoMovimento.VersaoDesatualizada => (StatusCodes.Status409Conflict, "versao-desatualizada"),
            _ => (StatusCodes.Status400BadRequest, "invalido"),
        };
        return Results.Json(new MovimentoErro(codigo, r.ComandaId, r.VersaoAtual), statusCode: status);
    }
}
