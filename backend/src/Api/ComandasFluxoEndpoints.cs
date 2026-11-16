using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Flow4Tech.Comandas.Application;

namespace Flow4Tech.Api;

/// <summary>A comanda and its version. A client that holds a lower version reloads the comanda.</summary>
internal sealed record VersaoDaComandaDto(Guid ComandaId, int Versao);

/// <summary>SSE event <c>comanda</c> (its <c>id</c> line is <c>EventoId</c>): what happened (<c>Tipo</c>, e.g. comanda.rodada-enviada) to which comandas, each with its version when the event was delivered.</summary>
internal sealed record EventoDoFluxoDto(Guid EventoId, string Tipo, DateTimeOffset Em, List<VersaoDaComandaDto> Comandas);

/// <summary>SSE event <c>resync</c>: sent on every connection (<c>Motivo</c> abertura, or reconexao when the client sent Last-Event-ID); <c>Comandas</c> are the open comandas it may see with their current versions. It reloads the ones it holds a lower version of.</summary>
internal sealed record ResyncDoFluxoDto(string Motivo, List<VersaoDaComandaDto> Comandas);

/// <summary>The wire format of Server-Sent Events: <c>id</c>, <c>event</c> and <c>data</c> lines ended by a blank line; a line starting with a colon is a comment (keep-alive).</summary>
internal static class Sse
{
    public const string ContentType = "text/event-stream";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Evento(string nome, object dados, Guid? id = null) =>
        (id is { } i ? $"id: {i:D}\n" : string.Empty) + $"event: {nome}\ndata: {JsonSerializer.Serialize(dados, Json)}\n\n";

    public static string Comentario(string texto) => $": {texto}\n\n";

    public static string Evento(EventoDoFluxo e) =>
        Evento("comanda", new EventoDoFluxoDto(e.Id, e.Tipo, e.Em, [.. e.Comandas.Select(c => new VersaoDaComandaDto(c.ComandaId, c.Versao))]), e.Id);

    public static string Evento(ResyncDoFluxo r) =>
        Evento("resync", new ResyncDoFluxoDto(r.Motivo, [.. r.Comandas.Select(c => new VersaoDaComandaDto(c.ComandaId, c.Versao))]));
}

internal static class ComandasFluxoEndpoints
{
    public static void MapComandasFluxo(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Read only: it audits nothing. Unit and organization come from the session; the only thing the client may send is Last-Event-ID (it saw events before, so it gets a resync).
        app.MapGet("/comandas/fluxo", async (HttpContext http, RequestContextAccessor request, FluxoDeComandas fluxo, FluxoOptions options, ISessionSource sessions, IPermissaoDaComanda permissoes, CancellationToken cancellationToken) =>
            {
                var reconectou = !string.IsNullOrWhiteSpace(http.Request.Headers["Last-Event-ID"].ToString());
                http.Response.ContentType = Sse.ContentType;
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["X-Accel-Buffering"] = "no";
                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

                using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limite.CancelAfter(options.DuracaoMaxima);
                try
                {
                    var (assinatura, resync) = await fluxo.AbrirAsync(request, reconectou, limite.Token);
                    await Escrever(http, Sse.Comentario("aberto") + Sse.Evento(resync), limite.Token);
                    var ultimaEscrita = DateTimeOffset.UtcNow;
                    var ultimaChecagem = ultimaEscrita;
                    while (true)
                    {
                        await Task.Delay(options.Intervalo, limite.Token);
                        if (DateTimeOffset.UtcNow - ultimaChecagem >= options.Revalidacao)
                        {
                            // Same checks as the route's gates: a revoked session or a lost permission ends the stream (the client reconnects and is refused).
                            if (await sessions.ReadAsync(http, limite.Token) is not { } atual
                                || atual.SessionId != request.SessionId
                                || atual.ActiveUnitId != request.Organization.UnitId
                                || !await permissoes.PodeAsync(request, ComandaPermissions.Ver, limite.Token))
                            {
                                break;
                            }

                            ultimaChecagem = DateTimeOffset.UtcNow;
                        }

                        var eventos = await fluxo.LerAsync(request, assinatura, limite.Token);
                        if (eventos.Count > 0)
                        {
                            await Escrever(http, string.Concat(eventos.Select(Sse.Evento)), limite.Token);
                            ultimaEscrita = DateTimeOffset.UtcNow;
                        }
                        else if (DateTimeOffset.UtcNow - ultimaEscrita >= options.Batimento)
                        {
                            await Escrever(http, Sse.Comentario("batimento"), limite.Token);
                            ultimaEscrita = DateTimeOffset.UtcNow;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // The client left or the stream reached its maximum life: an ordinary end, the client reconnects.
                }

                return Results.Empty;
            })
            .WithName("streamComandas")
            .RequireSession()
            .RequirePermission(ComandaPermissions.Ver)
            .Produces<string>(StatusCodes.Status200OK, Sse.ContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }

    private static async Task Escrever(HttpContext http, string texto, CancellationToken cancellationToken)
    {
        await http.Response.WriteAsync(texto, cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }
}
