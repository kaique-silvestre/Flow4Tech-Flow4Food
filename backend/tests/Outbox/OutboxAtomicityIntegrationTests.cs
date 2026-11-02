using Flow4Tech.Isolation.Tests;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Outbox.Tests;

/// <summary>Criterion 1: the state change and its outbox row are one atomic unit.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class OutboxAtomicityIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Commit_writes_the_state_change_and_the_outbox_row_together()
    {
        await using var env = await Env.CreateAsync(postgres);
        var evt = await env.CreateItemAsync(Ctx(OrgA), "ok");

        Assert.Equal(1, await env.ItemsAsync(Ctx(OrgA), "ok"));
        Assert.Equal(1, await env.CountAsync(Ctx(OrgA), "SELECT count(*) FROM outbox_eventos WHERE id = @id AND tipo = 'amostra.item-criado' AND payload ->> 'Nome' = 'ok'", ("id", evt.EventId)));
    }

    [Theory]
    [InlineData(Fault.AfterStateChange)]
    [InlineData(Fault.AfterOutboxWrite)]
    public async Task A_failure_between_the_two_writes_or_before_commit_leaves_neither(Fault fault)
    {
        await using var env = await Env.CreateAsync(postgres);

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.CreateItemAsync(Ctx(OrgA), "boom", fault));

        Assert.Equal(0, await env.ItemsAsync(Ctx(OrgA), "boom"));
        Assert.Equal(0, await env.AllEventsAsync(Ctx(OrgA)));
    }

    [Fact]
    public async Task A_failing_outbox_write_rolls_back_the_state_change_too()
    {
        await using var env = await Env.CreateAsync(postgres);
        var first = await env.CreateItemAsync(Ctx(OrgA), "first");

        // Same event id again: the outbox insert violates the primary key, after the item insert succeeded.
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        await Exec(tx, "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@i, @org, @u, 'second', 1)", ("i", Guid.NewGuid()), ("org", OrgA), ("u", UnitA));
        await Assert.ThrowsAnyAsync<Exception>(() => env.Writer.AddAsync(tx, Ctx(OrgA), first));
        await tx.DisposeAsync();

        Assert.Equal(0, await env.ItemsAsync(Ctx(OrgA), "second"));
        Assert.Equal(1, await env.AllEventsAsync(Ctx(OrgA)));
    }
}
