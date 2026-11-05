using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Outbox.Tests;

/// <summary>
/// Ticket 07 (spec 5.8, closes F2 of ADR-003): the registry of organizations with pending events, kept by a trigger in the event's own transaction and read by
/// the dispatcher through restricted SECURITY DEFINER functions, with no role that bypasses RLS. Real PostgreSQL (not run in the cloud environment).
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class OutboxPendingRegistryIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Writing_an_event_registers_its_organization_and_only_that_one()
    {
        await using var env = await Env.CreateAsync(postgres);
        Assert.Empty(await env.PendingIdsAsync());

        await env.CreateItemAsync(Ctx(OrgA), "one");
        await env.CreateItemAsync(Ctx(OrgA), "two");

        Assert.Equal([OrgA], await env.PendingIdsAsync());
    }

    [Theory]
    [InlineData(Fault.AfterStateChange)]
    [InlineData(Fault.AfterOutboxWrite)]
    public async Task A_failure_after_the_event_is_written_leaves_neither_event_nor_registry_row(Fault fault)
    {
        await using var env = await Env.CreateAsync(postgres);

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.CreateItemAsync(Ctx(OrgA), "boom", fault));

        Assert.Equal(0, await env.AllEventsAsync(Ctx(OrgA)));
        Assert.Empty(await env.PendingIdsAsync());
    }

    [Fact]
    public async Task A_rejected_event_insert_rolls_back_its_registry_row_with_the_rest()
    {
        await using var env = await Env.CreateAsync(postgres);
        var first = await env.CreateItemAsync(Ctx(OrgB), "first");
        await env.Sweeper().SweepAsync(); // no consumer: delivered and marked, registry cleared
        Assert.Empty(await env.PendingIdsAsync());

        await using (var tx = await env.Tenant.BeginAsync(Ctx(OrgB)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => env.Writer.AddAsync(tx, Ctx(OrgB), first)); // duplicate event id
        }

        Assert.Empty(await env.PendingIdsAsync());
    }

    [Fact]
    public async Task The_sweep_delivers_events_of_several_organizations_each_under_its_own_context_and_clears_the_registry()
    {
        await using var env = await Env.CreateAsync(postgres);
        var a = await env.CreateItemAsync(Ctx(OrgA), "A-evento");
        var b = await env.CreateItemAsync(Ctx(OrgB), "B-evento");
        var seen = new List<(Guid Event, long VisibleItems)>();
        var consumer = new RecordingConsumer
        {
            // Inside the delivery transaction only the event's own organization is visible (RLS): the other organization's item must not be.
            Inside = async (m, tx) =>
            {
                await using var command = new NpgsqlCommand("SELECT count(*) FROM amostra_itens", tx.Connection, tx.Transaction);
                seen.Add((m.Id, (long)(await command.ExecuteScalarAsync())!));
            },
        };

        var delivered = await env.Sweeper(consumer).SweepAsync();

        Assert.Equal(2, delivered);
        Assert.Equal([a.EventId, b.EventId], consumer.Deliveries.Order());
        Assert.All(seen, s => Assert.Equal(1, s.VisibleItems));
        Assert.Empty(await env.PendingIdsAsync());
        Assert.Equal(0, await env.PendingEventsAsync(Ctx(OrgA)));
        Assert.Equal(0, await env.PendingEventsAsync(Ctx(OrgB)));
    }

    [Fact]
    public async Task An_organization_whose_event_keeps_failing_stays_registered_until_delivered()
    {
        await using var env = await Env.CreateAsync(postgres);
        var bad = await env.CreateItemAsync(Ctx(OrgA), "bad");
        await env.CreateItemAsync(Ctx(OrgB), "good");
        var failing = true;
        var consumer = new RecordingConsumer { FailWhen = m => failing && m.Id == bad.EventId };
        var sweeper = env.Sweeper(consumer);

        Assert.Equal(1, await sweeper.SweepAsync());
        Assert.Equal([OrgA], await env.PendingIdsAsync());

        failing = false;
        Assert.Equal(1, await sweeper.SweepAsync());
        Assert.Empty(await env.PendingIdsAsync());
    }

    [Fact]
    public async Task Closing_keeps_the_row_while_an_event_is_pending_and_removes_it_when_none_is()
    {
        await using var env = await Env.CreateAsync(postgres);
        await env.CreateItemAsync(Ctx(OrgA), "one");

        Assert.False(await CloseAsync(env, Ctx(OrgA)));
        Assert.Equal([OrgA], await env.PendingIdsAsync());

        await env.Sweeper().SweepAsync();
        Assert.True(await CloseAsync(env, Ctx(OrgA))); // already gone: true, nothing to do
        Assert.Empty(await env.PendingIdsAsync());
    }

    [Fact]
    public async Task Closing_does_not_remove_an_organization_whose_event_is_still_uncommitted_in_another_transaction()
    {
        await using var env = await Env.CreateAsync(postgres);
        await env.CreateItemAsync(Ctx(OrgA), "seed"); // registered
        await using (var tx = await env.Tenant.BeginAsync(Ctx(OrgA)))
        {
            await Exec(tx, "UPDATE outbox_eventos SET processado_em = now()");
            await tx.CommitAsync();
        }

        // A writer inserts a new event and has not committed yet; it holds the registry row lock through the trigger.
        await using var writer = await env.Tenant.BeginAsync(Ctx(OrgA));
        await env.Writer.AddAsync(writer, Ctx(OrgA), new ItemCriado(Guid.NewGuid(), Guid.NewGuid(), "late"));

        var closing = CloseAsync(env, Ctx(OrgA)); // blocks on the row lock until the writer ends
        await Task.Delay(300);
        Assert.False(closing.IsCompleted);
        await writer.CommitAsync();

        Assert.False(await closing); // sees the committed event, keeps the row
        Assert.Equal([OrgA], await env.PendingIdsAsync());
    }

    [Fact]
    public async Task The_discovery_function_refuses_to_run_inside_an_organization_context()
    {
        await using var env = await Env.CreateAsync(postgres);
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        await using var command = new NpgsqlCommand("SELECT * FROM outbox_organizacoes_pendentes(10)", tx.Connection, tx.Transaction);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteReaderAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Closing_without_a_context_is_refused()
    {
        await using var env = await Env.CreateAsync(postgres);
        await using var connection = await env.App.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT outbox_encerrar_pendencia()", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task The_application_role_has_no_privilege_on_the_registry_table_and_can_still_read_events_only_under_its_context()
    {
        await using var env = await Env.CreateAsync(postgres);
        await env.CreateItemAsync(Ctx(OrgA), "one");
        await using var connection = await env.App.OpenConnectionAsync();

        await using (var select = new NpgsqlCommand("SELECT count(*) FROM outbox_organizacoes_pendentes", connection))
        {
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync())).SqlState);
        }

        await using (var insert = new NpgsqlCommand("INSERT INTO outbox_organizacoes_pendentes (organizacao_id, desde) VALUES (@o, now())", connection))
        {
            insert.Parameters.AddWithValue("o", OrgB);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
        }

        // Without a context the events stay invisible even though the registry names the organization.
        await using var events = new NpgsqlCommand("SELECT count(*) FROM outbox_eventos", connection);
        Assert.Equal(0L, await events.ExecuteScalarAsync());
    }

    [Fact]
    public async Task The_discovery_function_returns_only_ids_and_instants_oldest_first()
    {
        await using var env = await Env.CreateAsync(postgres);
        env.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        await env.CreateItemAsync(Ctx(OrgB), "older");
        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(1);
        await env.CreateItemAsync(Ctx(OrgA), "newer");

        var listed = await env.Pending.ListAsync(100);

        Assert.Equal([OrgB, OrgA], listed.Select(p => p.OrganizationId));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero), listed[0].Since);
        Assert.Single(await env.Pending.ListAsync(1));
    }

    [Fact]
    public async Task No_role_that_bypasses_row_level_security_was_created()
    {
        await using var env = await Env.CreateAsync(postgres);
        await using var owner = NpgsqlDataSource.Create(env.Db.OwnerConnectionString);
        await using var connection = await owner.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_roles WHERE rolbypassrls AND NOT rolsuper", connection);

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private static async Task<bool> CloseAsync(Env env, Flow4Tech.Kernel.OrganizationContext organization)
    {
        await using var tx = await env.Tenant.BeginAsync(organization);
        await using var command = new NpgsqlCommand("SELECT outbox_encerrar_pendencia()", tx.Connection, tx.Transaction);
        var closed = (bool)(await command.ExecuteScalarAsync())!;
        await tx.CommitAsync();
        return closed;
    }
}
