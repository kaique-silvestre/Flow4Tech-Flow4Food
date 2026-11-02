using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Outbox.Tests;

/// <summary>Criterion 3. Unit: the wrapper against an in-memory store. Integration: the real inbox table.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class IdempotentConsumerTests(PostgresFixture postgres)
{
    private sealed class MemoryStore : IProcessedEventStore
    {
        private readonly HashSet<(string, Guid)> _seen = [];

        public Task<bool> TryMarkAsync(string consumer, OutboxMessage message, TenantTransaction transaction, CancellationToken cancellationToken) =>
            Task.FromResult(_seen.Add((consumer, message.Id)));
    }

    private static OutboxMessage Message(Guid id) => new(id, Ctx(OrgA), "amostra.item-criado", "{}", DateTimeOffset.UnixEpoch, 0);

    [Fact]
    public async Task The_same_event_twice_has_one_effect()
    {
        var effects = 0;
        var consumer = new IdempotentConsumer("c", "amostra.item-criado", new MemoryStore(), (_, _, _) =>
        {
            effects++;
            return Task.CompletedTask;
        });
        var message = Message(Guid.NewGuid());

        await consumer.HandleAsync(message, null!, CancellationToken.None);
        await consumer.HandleAsync(message, null!, CancellationToken.None);

        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task Different_events_and_different_consumers_each_apply_their_effect()
    {
        var effects = 0;
        Task Effect(OutboxMessage m, TenantTransaction t, CancellationToken c)
        {
            effects++;
            return Task.CompletedTask;
        }

        var store = new MemoryStore();
        var one = new IdempotentConsumer("one", "amostra.item-criado", store, Effect);
        var two = new IdempotentConsumer("two", "amostra.item-criado", store, Effect);
        var message = Message(Guid.NewGuid());

        await one.HandleAsync(message, null!, CancellationToken.None);
        await two.HandleAsync(message, null!, CancellationToken.None);
        await one.HandleAsync(Message(Guid.NewGuid()), null!, CancellationToken.None);

        Assert.Equal(3, effects);
    }

    [Fact]
    public async Task With_the_database_inbox_a_redelivered_event_produces_one_row_and_the_inbox_rolls_back_with_a_failed_delivery()
    {
        await using var env = await Env.CreateAsync(postgres);
        var evt = await env.CreateItemAsync(Ctx(OrgA), "origem");
        var consumer = new IdempotentConsumer("espelho", "amostra.item-criado", new DatabaseProcessedEventStore(env.Clock), async (m, tx, _) =>
            await Exec(tx, "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@i, @org, @u, @nome, 1)", ("i", Guid.NewGuid()), ("org", OrgA), ("u", UnitA), ("nome", "efeito-" + m.Id)));
        var dispatcher = new OutboxDispatcher(env.Tenant, [consumer], env.Clock);
        var crash = true;
        dispatcher.BeforeMark = _ => crash ? throw new InvalidOperationException("crash before marking") : Task.CompletedTask;

        await dispatcher.DispatchAsync(Ctx(OrgA)); // delivered, then crashed: effect and inbox mark rolled back together
        Assert.Equal(0, await env.ItemsAsync(Ctx(OrgA), "efeito-" + evt.EventId));
        crash = false;
        await dispatcher.DispatchAsync(Ctx(OrgA)); // redelivery applies the effect once

        // Replaying the same message directly (e.g. a duplicate from a future broker) is absorbed by the inbox.
        await using (var tx = await env.Tenant.BeginAsync(Ctx(OrgA)))
        {
            await consumer.HandleAsync(new OutboxMessage(evt.EventId, Ctx(OrgA), evt.EventType, "{}", env.Clock.UtcNow, 0), tx, CancellationToken.None);
            await tx.CommitAsync();
        }

        Assert.Equal(1, await env.ItemsAsync(Ctx(OrgA), "efeito-" + evt.EventId));
        Assert.Equal(1, await env.CountAsync(Ctx(OrgA), "SELECT count(*) FROM outbox_consumos WHERE evento_id = @e", ("e", evt.EventId)));
    }
}
