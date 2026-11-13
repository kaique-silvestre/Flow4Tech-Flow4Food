using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Api;

/// <summary><c>Versao</c> is the comanda version the screen last saw. The quantity is 1 or more (to remove an item cancel it); the note is replaced (null or blank clears it). The frozen price and options never change.</summary>
internal sealed record AlterarItemRequest(int Versao, int Quantidade, string? Observacao);

internal sealed record RenomearComandaRequest(int Versao, string Identificacao);

/// <summary><c>GarcomId</c> must be an active account of the unit's team.</summary>
internal sealed record TrocarGarcomRequest(int Versao, Guid GarcomId);

/// <summary>Both fields are replaced together: <c>Pessoas</c> 1 to 999 or null, <c>Observacao</c> up to 200 characters or null (clears).</summary>
internal sealed record AlterarDadosRequest(int Versao, int? Pessoas, string? Observacao);

/// <summary><c>Motivo</c> is a code of the standard list (cliente-desistiu, erro-de-lancamento, item-indisponivel, outro); <c>Texto</c> is required with 'outro'.</summary>
internal sealed record CancelarComandaRequest(int Versao, string Motivo, string? Texto);

internal sealed record ItemAlteradoDto(ItemDto Item, int ComandaVersao);

/// <summary>The comanda after cancelling and how many launched items were cancelled with it (same reason).</summary>
internal sealed record ComandaCanceladaDto(ComandaDto Comanda, int ItensCancelados);

/// <summary>One line of the timeline. <c>Acao</c> is the audit action (comanda:abrir, comanda:lancar-rodada, comanda:cancelar-item, pagamento:registrar...); <c>Antes</c>/<c>Depois</c> hold only simple, non-secret fields.</summary>
internal sealed record EventoDoHistoricoDto(Guid Id, string Acao, DateTimeOffset Quando, Guid? UsuarioId, string? UsuarioNome, Dictionary<string, string> Antes, Dictionary<string, string> Depois);

internal sealed record HistoricoDaComandaDto(List<EventoDoHistoricoDto> Eventos);

internal static class ComandasEdicaoEndpoints
{
    private const long Body = 1024;

    public static void MapEdicaoDaComanda(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/comandas/{comandaId:guid}/itens/{itemId:guid}/alterar", async (Guid comandaId, Guid itemId, AlterarItemRequest body, RequestContextAccessor request, GestaoDaEdicaoDaComanda edicao, CancellationToken cancellationToken) =>
            {
                var r = await edicao.AlterarItemAsync(request, comandaId, itemId, body.Versao, body.Quantidade, body.Observacao, cancellationToken);
                return r.Resultado == ResultadoDaEdicao.Ok ? Results.Ok(new ItemAlteradoDto(ComandasEndpoints.ItemParaDto(r.Item!), r.Comanda!.Versao)) : Erro(r);
            })
            .WithName("alterarItemDaComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.AlterarItem)
            .Produces<ItemAlteradoDto>()
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/comandas/{comandaId:guid}/renomear", async (Guid comandaId, RenomearComandaRequest body, RequestContextAccessor request, GestaoDaEdicaoDaComanda edicao, CancellationToken cancellationToken) =>
            {
                var r = await edicao.RenomearAsync(request, comandaId, body.Versao, body.Identificacao, cancellationToken);
                return r.Resultado == ResultadoDaEdicao.Ok ? Results.Ok(ComandasEndpoints.Dto(r.Comanda!)) : Erro(r);
            })
            .WithName("renomearComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.Renomear)
            .Produces<ComandaDto>()
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/comandas/{comandaId:guid}/trocar-garcom", async (Guid comandaId, TrocarGarcomRequest body, RequestContextAccessor request, GestaoDaEdicaoDaComanda edicao, CancellationToken cancellationToken) =>
            {
                var r = await edicao.TrocarGarcomAsync(request, comandaId, body.Versao, body.GarcomId, cancellationToken);
                return r.Resultado == ResultadoDaEdicao.Ok ? Results.Ok(ComandasEndpoints.Dto(r.Comanda!)) : Erro(r);
            })
            .WithName("trocarGarcomDaComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.TrocarGarcom)
            .Produces<ComandaDto>()
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/comandas/{comandaId:guid}/dados", async (Guid comandaId, AlterarDadosRequest body, RequestContextAccessor request, GestaoDaEdicaoDaComanda edicao, CancellationToken cancellationToken) =>
            {
                var r = await edicao.AlterarDadosAsync(request, comandaId, body.Versao, body.Pessoas, body.Observacao, cancellationToken);
                return r.Resultado == ResultadoDaEdicao.Ok ? Results.Ok(ComandasEndpoints.Dto(r.Comanda!)) : Erro(r);
            })
            .WithName("alterarDadosDaComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.AlterarDados)
            .Produces<ComandaDto>()
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/comandas/{comandaId:guid}/cancelar", async (Guid comandaId, CancelarComandaRequest body, RequestContextAccessor request, GestaoDaEdicaoDaComanda edicao, CancellationToken cancellationToken) =>
            {
                var r = await edicao.CancelarAsync(request, comandaId, body.Versao, body.Motivo, body.Texto, cancellationToken);
                return r.Resultado == ResultadoDaEdicao.Ok ? Results.Ok(new ComandaCanceladaDto(ComandasEndpoints.Dto(r.Comanda!), r.ItensCancelados)) : Erro(r);
            })
            .WithName("cancelarComanda")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ComandaPermissions.Cancelar, sensitive: true)
            .Produces<ComandaCanceladaDto>()
            .Produces<ComandaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound)
            .Produces<ComandaErro>(StatusCodes.Status409Conflict);

        app.MapGet("/comandas/{comandaId:guid}/historico", async (Guid comandaId, RequestContextAccessor request, ConsultaDoHistoricoDaComanda historico, CancellationToken cancellationToken) =>
            {
                var eventos = await historico.ListarAsync(request, comandaId, cancellationToken);
                return eventos is null
                    ? Results.Json(new ComandaErro("nao-encontrada"), statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(new HistoricoDaComandaDto(eventos.Select(e => new EventoDoHistoricoDto(e.Id, e.Acao, e.Quando, e.UsuarioId, e.UsuarioNome, e.Antes.ToDictionary(x => x.Key, x => x.Value), e.Depois.ToDictionary(x => x.Key, x => x.Value))).ToList()));
            })
            .WithName("getHistoricoDaComanda")
            .RequireSession()
            .RequirePermission(ComandaPermissions.VerHistorico)
            .Produces<HistoricoDaComandaDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ComandaErro>(StatusCodes.Status404NotFound);
    }

    private static IResult Erro(ResultadoDaEdicaoDaComanda r)
    {
        var (status, codigo) = r.Resultado switch
        {
            ResultadoDaEdicao.ComandaNaoEncontrada or ResultadoDaEdicao.ItemNaoEncontrado => (StatusCodes.Status404NotFound, "nao-encontrada"),
            ResultadoDaEdicao.ComandaNaoAberta => (StatusCodes.Status409Conflict, "comanda-nao-aberta"),
            ResultadoDaEdicao.ItemJaCancelado => (StatusCodes.Status409Conflict, "item-ja-cancelado"),
            ResultadoDaEdicao.VersaoDesatualizada => (StatusCodes.Status409Conflict, "versao-desatualizada"),
            ResultadoDaEdicao.ValorJaRecebido => (StatusCodes.Status409Conflict, "valor-ja-recebido"),
            ResultadoDaEdicao.ComPagamentos => (StatusCodes.Status409Conflict, "com-pagamentos"),
            ResultadoDaEdicao.GarcomInvalido => (StatusCodes.Status400BadRequest, "garcom-invalido"),
            _ => (StatusCodes.Status400BadRequest, "invalido"),
        };
        return Results.Json(new ComandaErro(codigo, r.VersaoAtual), statusCode: status);
    }
}
