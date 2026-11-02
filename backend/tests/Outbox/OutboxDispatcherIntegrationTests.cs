using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Outbox.Tests;

/// <summary>Criteria 2 and 4: at-least-once delivery, and the organization context restored (under RLS) before touching data.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class OutboxDispatcherIntegrationTests(PostgresFixture postgres)
{
    private static OutboxDispatcher Dispatcher(Env env, params RecordingConsumer[] consumers) => new(env.Tenant, consumers, env.Clock);

    [Fact]
    public async Task Delivers_each_pending_event_once_and_marks_it_processed()
    {
        await using var env = await Env.CreateAsync(postgres);
        await env.CreateItemAsync(Ctx(OrgA), "one");
        await env.CreateItemAsync(Ctx(OrgA), "two");
        var consumer = new RecordingConsumer();

        Assert.Equal(2, await Dispatcher(env, consumer).DispatchAsync(Ctx(OrgA)));
        Assert.Equal(0, await Dispatcher(env, consumer).DispatchAsync(Ctx(OrgA)));

        Assert.Equal(2, consumer.Deliveries.Count);
        Assert.Equal(0, await env.PendingEventsAsync(Ctx(OrgA)));
    }

    [Fact]
    public async Task A_failure_after_delivery_and_before_the_processed_mark_causes_redelivery_not_loss()
    {
        await using var env = await Env.CreateAsync(postgres);
        var evt = await env.CreateItemAsync(Ctx(OrgA), "one");
        var consumer = new RecordingConsumer();
        var dispatcher = Dispatcher(env, consumer);
        var crash = true;
        dispatcher.BeforeMark = _ => crash ? throw new InvalidOperationException("crash before marking") : Task.CompletedTask;

        Assert.Equal(0, await dispatcher.DispatchAsync(Ctx(OrgA)));
        Assert.Single(consumer.Deliveries); // delivered ...
        Assert.Equal(1, await env.PendingEventsAsync(Ctx(OrgA))); // ... but still pending: not lost

        crash = false;
        Assert.Equal(1, await dispatcher.DispatchAsync(Ctx(OrgA)));
        Assert.Equal([evt.EventId, evt.EventId], consumer.Deliveries); // delivered again
        Assert.Equal(0, await env.PendingEventsAsync(Ctx(OrgA)));
    }

    [Fact]
    public async Task A_failing_consumer_keeps_the_event_pending_counts_attempts_and_does_not_block_the_next_event()
    {
        await using var env = await Env.CreateAsync(postgres);
        var bad = await env.CreateItemAsync(Ctx(OrgA), "bad");
        await env.CreateItemAsync(Ctx(OrgA), "good");
        var consumer = new RecordingConsumer { FailWhen = m => m.Id == bad.EventId };
        var dispatcher = Dispatcher(env, consumer);

        Assert.Equal(1, await dispatcher.DispatchAsync(Ctx(OrgA)));

        Assert.Equal(1, await env.PendingEventsAsync(Ctx(OrgA)));
        Assert.Equal(1, await env.CountAsync(Ctx(OrgA), "SELECT count(*) FROM outbox_eventos WHERE id = @id AND tentativas = 1 AND ultimo_erro = 'consumer failure'", ("id", bad.EventId)));
    }

    [Fact]
    public async Task An_event_that_keeps_failing_is_parked_after_max_attempts()
    {
        await using var env = await Env.CreateAsync(postgres);
        var bad = await env.CreateItemAsync(Ctx(OrgA), "bad");
        var consumer = new RecordingConsumer { FailWhen = _ => true };
        var dispatcher = new OutboxDispatcher(env.Tenant, [consumer], env.Clock, new OutboxOptions { MaxAttempts = 3 });

        for (var i = 0; i < 5; i++)
        {
            await dispatcher.DispatchAsync(Ctx(OrgA));
        }

        Assert.Equal(3, consumer.Deliveries.Count);
        Assert.Equal(0, await env.PendingEventsAsync(Ctx(OrgA)));
        Assert.Equal(1, await env.CountAsync(Ctx(OrgA), "SELECT count(*) FROM outbox_eventos WHERE id = @id AND falhou_em IS NOT NULL AND processado_em IS NULL", ("id", bad.EventId)));
    }

    [Fact]
    public async Task Consumers_run_under_RLS_of_the_events_organization_and_never_deliver_another_organizations_events()
    {
        await using var env = await Env.CreateAsync(postgres);
        await env.CreateItemAsync(Ctx(OrgA), "A-evento");
        await env.CreateItemAsync(Ctx(OrgB), "B-evento");
        var seen = new List<string>();
        var consumer = new RecordingConsumer
        {
            Inside = async (_, tx) => seen.AddRange(await Names(tx, "amostra_itens")),
        };

        Assert.Equal(1, await Dispatcher(env, consumer).DispatchAsync(Ctx(OrgA)));

        // Inside the delivery transaction the consumer sees A's rows only (seed "A-Chopp" + "A-evento"), never B's.
        Assert.All(seen, name => Assert.StartsWith("A-", name, StringComparison.Ordinal));
        Assert.Contains("A-evento", seen);
        Assert.Equal(1, await env.PendingEventsAsync(Ctx(OrgB)));
    }

    [Fact]
    public async Task Concurrent_dispatchers_never_deliver_the_same_event_twice()
    {
        await using var env = await Env.CreateAsync(postgres);
        for (var i = 0; i < 24; i++)
        {
            await env.CreateItemAsync(Ctx(OrgA), "n" + i);
        }

        var consumer = new RecordingConsumer { Inside = async (_, _) => await Task.Delay(5) };
        var dispatchers = Enumerable.Range(0, 4).Select(_ => Dispatcher(env, consumer).DispatchAsync(Ctx(OrgA)));
        await Task.WhenAll(dispatchers);

        Assert.Equal(24, consumer.Deliveries.Count);
        Assert.Equal(24, consumer.Deliveries.Distinct().Count());
        Assert.Equal(0, await env.PendingEventsAsync(Ctx(OrgA)));
    }

    [Fact]
    public async Task Without_an_organization_context_the_outbox_is_invisible()
    {
        await using var env = await Env.CreateAsync(postgres);
        await env.CreateItemAsync(Ctx(OrgA), "one");

        await using var connection = await env.App.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM outbox_eventos", connection);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }
}
