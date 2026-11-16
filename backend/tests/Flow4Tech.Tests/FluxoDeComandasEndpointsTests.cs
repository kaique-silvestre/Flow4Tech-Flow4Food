using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0139: the comanda stream (<c>GET /comandas/fluxo</c>, Server-Sent Events) through the real authenticated route, real PostgreSQL, application credential (RLS on), real sessions (same fixture as the comandas).
/// Covers events in version order from another request's change, another organization and another unit never heard, the resync on reconnect, visibility of other waiters' comandas, and 401/403.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private static readonly TimeSpan StreamWait = TimeSpan.FromSeconds(20);

    private sealed record Frame(string? Event, string? Id, string? Data, string? Comment)
    {
        public JsonElement Json => JsonDocument.Parse(Data!).RootElement;

        public List<(Guid Comanda, int Versao)> Versions => [.. Json.GetProperty("comandas").EnumerateArray().Select(c => (c.GetProperty("comandaId").GetGuid(), c.GetProperty("versao").GetInt32()))];
    }

    private sealed class SseClient(HttpResponseMessage response, Stream body) : IDisposable
    {
        private readonly StreamReader _reader = new(body);

        public Frame Resync { get; private set; } = new(null, null, null, null);

        public static async Task<SseClient> ConnectAsync(HttpClient client, string? token, string? lastEventId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/comandas/fluxo", UriKind.Relative));
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (lastEventId is not null)
            {
                request.Headers.Add("Last-Event-ID", lastEventId);
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            SseClient? sse = null;
            try
            {
                sse = new SseClient(response, await response.Content.ReadAsStreamAsync());
                Assert.Equal("aberto", (await sse.NextAsync()).Comment);
                sse.Resync = await sse.NextAsync();
                Assert.Equal("resync", sse.Resync.Event);
                var ready = sse;
                sse = null;
                return ready;
            }
            finally
            {
                sse?.Dispose();
            }
        }

        public async Task<Frame> NextAsync()
        {
            using var timeout = new CancellationTokenSource(StreamWait);
            string? name = null, id = null, data = null, comment = null;
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("the stream ended");
                if (line.Length == 0)
                {
                    return new Frame(name, id, data, comment);
                }

                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line["event: ".Length..];
                }
                else if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = line["id: ".Length..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line["data: ".Length..];
                }
                else if (line.StartsWith(": ", StringComparison.Ordinal))
                {
                    comment = line[": ".Length..];
                }
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            response.Dispose();
        }
    }

    private Api StreamApi() => new(_appConnection, configure: services =>
    {
        services.RemoveAll<FluxoOptions>();
        services.AddSingleton(new FluxoOptions { Intervalo = TimeSpan.FromMilliseconds(100) });
    });

    [Fact]
    public async Task A_client_hears_the_changes_another_request_makes_in_the_order_of_the_comanda_version()
    {
        await using var api = StreamApi();
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        using var stream = await SseClient.ConnectAsync(client, t.Supervisor);

        var comanda = await OpenAsync(client, t.Garcom);
        var aberta = await stream.NextAsync();
        var primeira = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);
        var enviada = await stream.NextAsync();
        var segunda = await LaunchOkAsync(client, t.Garcom, comanda.Id, primeira.ComandaVersao, produto.Id);
        var outra = await stream.NextAsync();

        Assert.Equal(["comanda.aberta", "comanda.rodada-enviada", "comanda.rodada-enviada"], new[] { aberta, enviada, outra }.Select(f => f.Json.GetProperty("tipo").GetString()));
        Assert.All(new[] { aberta, enviada, outra }, f => Assert.Equal("comanda", f.Event));
        Assert.Equal([(comanda.Id, 1), (comanda.Id, primeira.ComandaVersao), (comanda.Id, segunda.ComandaVersao)], new[] { aberta, enviada, outra }.Select(f => Assert.Single(f.Versions)));
        Assert.Equal([1, 2, 3], new[] { aberta, enviada, outra }.Select(f => f.Versions.Single().Versao));
        Assert.All(new[] { aberta, enviada, outra }, f => Assert.True(Guid.TryParse(f.Id, out _)));

        // A burst read in one go: the events keep their order and every one carries a version at least as new as the change it announces.
        var terceira = await LaunchOkAsync(client, t.Garcom, comanda.Id, segunda.ComandaVersao, produto.Id);
        var quarta = await LaunchOkAsync(client, t.Garcom, comanda.Id, terceira.ComandaVersao, produto.Id);
        var rajada = new[] { await stream.NextAsync(), await stream.NextAsync() };
        var versions = rajada.Select(f => f.Versions.Single().Versao).ToList();
        Assert.Equal(versions.Order(), versions);
        Assert.Equal(quarta.ComandaVersao, versions[^1]);
        Assert.True(versions[0] >= terceira.ComandaVersao);
    }

    [Fact]
    public async Task Another_organization_and_another_unit_never_hear_the_event()
    {
        await using var api = StreamApi();
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        using var other = await SseClient.ConnectAsync(client, t.DonoB);
        using var mine = await SseClient.ConnectAsync(client, t.Supervisor);

        var comanda = await OpenAsync(client, t.Garcom);
        // An event of a sibling unit of the SAME organization, written straight into the outbox.
        await InsertOutboxEventAsync(OrgA, UnitA, "comanda.rodada-enviada", $$"""{"UnidadeId":"{{UnitA2}}","ComandaId":"{{comanda.Id}}"}""");
        var depois = await OpenAsync(client, t.Garcom);
        var theirs = await OpenAsync(client, t.DonoB);

        // Same order of commits on both streams: if A's event had leaked to B (or the sibling unit's to A), it would come first.
        var b = await other.NextAsync();
        Assert.Equal([(theirs.Id, 1)], b.Versions);
        var a1 = await mine.NextAsync();
        var a2 = await mine.NextAsync();
        Assert.Equal([comanda.Id, depois.Id], new[] { a1, a2 }.Select(f => f.Versions.Single().Comanda));
    }

    [Fact]
    public async Task A_client_that_reconnects_gets_a_resync_with_the_current_versions_and_then_only_what_is_new()
    {
        await using var api = StreamApi();
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);
        var outra = await OpenAsync(client, t.Garcom2);

        using var stream = await SseClient.ConnectAsync(client, t.Supervisor, lastEventId: Guid.CreateVersion7().ToString());
        var resync = stream.Resync;

        Assert.Equal("resync", resync.Event);
        Assert.Null(resync.Id);
        Assert.Equal("reconexao", resync.Json.GetProperty("motivo").GetString());
        Assert.Equal(new[] { (comanda.Id, rodada.ComandaVersao), (outra.Id, 1) }.Order(), resync.Versions.Order());
        var nova = await LaunchOkAsync(client, t.Garcom, comanda.Id, rodada.ComandaVersao, produto.Id);
        var next = await stream.NextAsync();
        Assert.Equal("comanda", next.Event);
        Assert.Equal([(comanda.Id, nova.ComandaVersao)], next.Versions);
    }

    [Fact]
    public async Task A_first_connection_with_no_header_also_gets_a_resync_with_the_open_comandas_it_may_see()
    {
        await using var api = StreamApi();
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mine = await OpenAsync(client, t.Garcom);
        await OpenAsync(client, t.Garcom2);

        using var stream = await SseClient.ConnectAsync(client, t.Garcom);

        Assert.Null(stream.Resync.Id);
        Assert.Equal("abertura", stream.Resync.Json.GetProperty("motivo").GetString());
        Assert.Equal([(mine.Id, 1)], stream.Resync.Versions);
    }

    [Fact]
    public async Task A_session_revoked_in_the_middle_of_the_stream_stops_receiving_events()
    {
        await using var api = new Api(_appConnection, configure: services =>
        {
            services.RemoveAll<FluxoOptions>();
            services.AddSingleton(new FluxoOptions { Intervalo = TimeSpan.FromMilliseconds(100), Revalidacao = TimeSpan.FromMilliseconds(300) });
        });
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        using var stream = await SseClient.ConnectAsync(client, t.Supervisor);
        await OpenAsync(client, t.Garcom);
        Assert.Equal("comanda", (await stream.NextAsync()).Event);

        await using (var source = NpgsqlDataSource.Create(_appConnection))
        await using (var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA)))
        {
            await Exec(tx, "UPDATE sessoes SET revogada_em = now(), revogada_motivo = 'teste' WHERE conta_id = @c", ("c", Supervisor));
            await tx.CommitAsync();
        }

        // The stream ends at the next check (the reader sees the end), so a change made after that is never delivered.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await OpenAsync(client, t.Garcom);
            while (true)
            {
                Assert.NotEqual("comanda", (await stream.NextAsync()).Event);
            }
        });
    }

    [Fact]
    public async Task Without_see_others_permission_a_waiter_hears_only_the_comandas_they_hold()
    {
        await using var api = StreamApi();
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        using var stream = await SseClient.ConnectAsync(client, t.Garcom2);

        await OpenAsync(client, t.Garcom);
        var mine = await OpenAsync(client, t.Garcom2);

        Assert.Equal([(mine.Id, 1)], (await stream.NextAsync()).Versions);
    }

    [Fact]
    public async Task Without_a_session_the_stream_is_refused_and_without_the_permission_it_is_forbidden()
    {
        await using var api = StreamApi();
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var anonymous = await SendAsync(client, HttpMethod.Get, "/comandas/fluxo", null);
        using var mudo = await SendAsync(client, HttpMethod.Get, "/comandas/fluxo", t.Mudo);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, mudo.StatusCode);
        Assert.Empty(await mudo.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_stream_takes_no_organization_or_unit_from_the_client()
    {
        await using var api = StreamApi();
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        using var stream = await SseClient.ConnectAsync(client, t.DonoB);

        // The route has no parameters: a unit or organization of someone else in the query string changes nothing.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/comandas/fluxo?organizacaoId={OrgA:D}&unidadeId={UnitA:D}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.DonoB);
        using var attempt = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        await OpenAsync(client, t.Garcom);
        var theirs = await OpenAsync(client, t.DonoB);

        Assert.Equal([(theirs.Id, 1)], (await stream.NextAsync()).Versions);
    }

    private async Task InsertOutboxEventAsync(Guid org, Guid unit, string type, string payload)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
        await Exec(tx, "INSERT INTO outbox_eventos (id, organizacao_id, tipo, payload, ocorrido_em) VALUES (@i, @o, @t, @p::jsonb, now())", ("i", Guid.CreateVersion7()), ("o", org), ("t", type), ("p", payload));
        await tx.CommitAsync();
    }
}
