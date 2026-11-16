using System.Text.Json;
using Flow4Tech.Api;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Kernel;

namespace Flow4Tech.Tests;

/// <summary>F4-0139: the SSE wire format and the order rules of the comanda stream, with no database (a fake store stands in for the outbox and the comanda versions).</summary>
public sealed class FluxoDeComandasRulesTests
{
    private static readonly Guid Org = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid Unit = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid User = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_event_is_written_as_id_event_and_data_lines_ended_by_a_blank_line()
    {
        var id = Guid.Parse("0197b000-0000-7000-8000-000000000001");
        var comanda = Guid.Parse("0197b000-0000-7000-8000-000000000002");

        var text = Sse.Evento(new EventoDoFluxo(id, "comanda.rodada-enviada", Now, [new VersaoDeComanda(comanda, 3)]));

        var lines = text.Split('\n');
        Assert.Equal($"id: {id}", lines[0]);
        Assert.Equal("event: comanda", lines[1]);
        Assert.StartsWith("data: ", lines[2], StringComparison.Ordinal);
        Assert.Equal(["", ""], lines[3..]);
        using var data = JsonDocument.Parse(lines[2]["data: ".Length..]);
        Assert.Equal("comanda.rodada-enviada", data.RootElement.GetProperty("tipo").GetString());
        var first = data.RootElement.GetProperty("comandas")[0];
        Assert.Equal((comanda, 3), (first.GetProperty("comandaId").GetGuid(), first.GetProperty("versao").GetInt32()));
    }

    [Fact]
    public void A_resync_has_no_id_line_and_a_comment_is_a_colon_line()
    {
        var text = Sse.Evento(new ResyncDoFluxo("reconexao", []));

        Assert.StartsWith("event: resync\ndata: {", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id:", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", text, StringComparison.Ordinal);
        Assert.Equal(": batimento\n\n", Sse.Comentario("batimento"));
    }

    [Fact]
    public async Task Events_come_out_oldest_first_with_the_current_version_of_each_comanda_and_never_twice()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var store = new FakeStore { Versions = { [a] = 3, [b] = 1 } };
        var fluxo = Create(store);
        var (subscription, _) = await fluxo.AbrirAsync(new Request(), reconectou: false);

        store.Add("comanda.rodada-enviada", Now.AddSeconds(1), a);
        store.Add("comanda.aberta", Now.AddSeconds(2), b);
        store.Add("comanda.rodada-enviada", Now.AddSeconds(3), a);
        var first = await fluxo.LerAsync(new Request(), subscription);
        var second = await fluxo.LerAsync(new Request(), subscription);

        Assert.Equal([(a, 3), (b, 1), (a, 3)], first.Select(e => (e.Comandas.Single().ComandaId, e.Comandas.Single().Versao)));
        Assert.Equal(first.Select(e => e.Id).Order(), first.Select(e => e.Id).Order().Distinct());
        Assert.Empty(second);
    }

    [Fact]
    public async Task An_event_that_committed_late_with_an_earlier_timestamp_is_still_delivered_once()
    {
        var comanda = Guid.CreateVersion7();
        var store = new FakeStore { Versions = { [comanda] = 2 } };
        var fluxo = Create(store);
        var (subscription, _) = await fluxo.AbrirAsync(new Request(), reconectou: false);
        store.Add("comanda.rodada-enviada", Now.AddSeconds(5), comanda);
        Assert.Single(await fluxo.LerAsync(new Request(), subscription));

        var late = store.Add("comanda.item-alterado", Now.AddSeconds(3), comanda);
        var delivered = await fluxo.LerAsync(new Request(), subscription);

        Assert.Equal(late, Assert.Single(delivered).Id);
        Assert.Empty(await fluxo.LerAsync(new Request(), subscription));
    }

    [Fact]
    public async Task What_happened_before_the_connection_is_not_delivered_and_every_connection_gets_a_resync_with_the_current_versions()
    {
        var comanda = Guid.CreateVersion7();
        var store = new FakeStore { Versions = { [comanda] = 7 } };
        store.Add("comanda.rodada-enviada", Now.AddSeconds(-2), comanda);
        var fluxo = Create(store);

        var (fresh, first) = await fluxo.AbrirAsync(new Request(), reconectou: false);
        var (again, resync) = await fluxo.AbrirAsync(new Request(), reconectou: true);

        Assert.Equal("abertura", first.Motivo);
        Assert.Equal([new VersaoDeComanda(comanda, 7)], first.Comandas);
        Assert.Empty(await fluxo.LerAsync(new Request(), fresh));
        Assert.Empty(await fluxo.LerAsync(new Request(), again));
        Assert.Equal("reconexao", resync!.Motivo);
        Assert.Equal([new VersaoDeComanda(comanda, 7)], resync.Comandas);
    }

    [Fact]
    public async Task A_transfer_or_merge_names_both_comandas_and_an_event_of_no_visible_comanda_is_dropped()
    {
        var from = Guid.CreateVersion7();
        var to = Guid.CreateVersion7();
        var hidden = Guid.CreateVersion7();
        var store = new FakeStore { Versions = { [from] = 4, [to] = 2 } };
        var fluxo = Create(store);
        var (subscription, _) = await fluxo.AbrirAsync(new Request(), reconectou: false);
        store.AddRaw("comanda.transferida", Now.AddSeconds(1), $$"""{"UnidadeId":"{{Unit}}","OrigemId":"{{from}}","DestinoId":"{{to}}"}""");
        store.Add("comanda.rodada-enviada", Now.AddSeconds(2), hidden);

        var delivered = await fluxo.LerAsync(new Request(), subscription);

        Assert.Equal([(from, 4), (to, 2)], Assert.Single(delivered).Comandas.Select(c => (c.ComandaId, c.Versao)));
    }

    private static FluxoDeComandas Create(FakeStore store) =>
        new(new InlineUnitOfWork(), store, new AllowAll(), new FixedClock(), new FluxoOptions());

    private sealed class Request : IRequestContext
    {
        public Guid UserId => User;

        public Guid SessionId => Guid.Empty;

        public OrganizationContext Organization => new(Org, Unit);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class AllowAll : IPermissaoDaComanda
    {
        public Task<bool> PodeAsync(IRequestContext request, string permission, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class InlineUnitOfWork : IUnitOfWork
    {
        public Task<T> ExecuteAsync<T>(OrganizationContext organization, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) => work(cancellationToken);
    }

    private sealed class FakeStore : IFluxoDeComandasStore
    {
        private readonly List<EventoBrutoDoFluxo> _events = [];

        public Dictionary<Guid, int> Versions { get; } = [];

        public Guid Add(string type, DateTimeOffset at, Guid comanda) => AddRaw(type, at, $$"""{"UnidadeId":"{{Unit}}","ComandaId":"{{comanda}}"}""");

        public Guid AddRaw(string type, DateTimeOffset at, string payload)
        {
            var id = Guid.CreateVersion7();
            _events.Add(new EventoBrutoDoFluxo(id, type, payload, at));
            return id;
        }

        public Task<IReadOnlyList<EventoBrutoDoFluxo>> LerAsync(Guid organizationId, Guid unitId, DateTimeOffset desde, IReadOnlyCollection<Guid> jaEntregues, int limite, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventoBrutoDoFluxo>>([.. _events.Where(e => e.OcorridoEm >= desde && !jaEntregues.Contains(e.Id)).OrderBy(e => e.OcorridoEm).ThenBy(e => e.Id).Take(limite)]);

        public Task<IReadOnlyList<VersaoDeComanda>> VersoesAsync(Guid organizationId, Guid unitId, IReadOnlyCollection<Guid> comandaIds, Guid? garcomId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersaoDeComanda>>([.. Versions.Where(p => comandaIds.Contains(p.Key)).Select(p => new VersaoDeComanda(p.Key, p.Value))]);

        public Task<IReadOnlyList<VersaoDeComanda>> VersoesDasAbertasAsync(Guid organizationId, Guid unitId, Guid? garcomId, int limite, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersaoDeComanda>>([.. Versions.Select(p => new VersaoDeComanda(p.Key, p.Value))]);
    }
}
