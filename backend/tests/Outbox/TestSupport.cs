using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Outbox.Tests;

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
}

internal sealed record ItemCriado(Guid EventId, Guid ItemId, string Nome) : IDomainEvent
{
    public string EventType => "amostra.item-criado";
}

/// <summary>Where to blow up inside <see cref="Env.CreateItemAsync"/>.</summary>
public enum Fault
{
    None,
    AfterStateChange,
    AfterOutboxWrite,
}

internal sealed class Env : IAsyncDisposable
{
    private Env(NpgsqlDataSource app, TestDatabase db)
    {
        App = app;
        Db = db;
        Tenant = new TenantDatabase(app);
        Writer = new OutboxWriter(Clock);
        Pending = new PendingOrganizationsDatabase(app);
    }

    public NpgsqlDataSource App { get; }

    public TestDatabase Db { get; }

    public TenantDatabase Tenant { get; }

    public FixedClock Clock { get; } = new();

    public OutboxWriter Writer { get; }

    public PendingOrganizationsDatabase Pending { get; }

    public OutboxSweeper Sweeper(params IOutboxConsumer[] consumers) => new(Pending, Tenant, new OutboxDispatcher(Tenant, consumers, Clock));

    public async Task<Guid[]> PendingIdsAsync() => [.. (await Pending.ListAsync(100)).Select(p => p.OrganizationId)];

    public static async Task<Env> CreateAsync(PostgresFixture postgres)
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        return new Env(NpgsqlDataSource.Create(db.AppConnectionString), db);
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();

    /// <summary>The state change (insert an item) and its event, in one transaction; <paramref name="fault"/> throws at the chosen step.</summary>
    public async Task<ItemCriado> CreateItemAsync(OrganizationContext org, string nome, Fault fault = Fault.None)
    {
        var itemId = EntityId.New().Value;
        var evt = new ItemCriado(EntityId.New().Value, itemId, nome);
        await using var tx = await Tenant.BeginAsync(org);
        await Exec(
            tx,
            "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@i, @org, @u, @nome, 1)",
            ("i", itemId), ("org", org.OrganizationId), ("u", org.UnitId), ("nome", nome));
        if (fault == Fault.AfterStateChange)
        {
            throw new InvalidOperationException("forced failure after the state change");
        }

        await Writer.AddAsync(tx, org, evt);
        if (fault == Fault.AfterOutboxWrite)
        {
            throw new InvalidOperationException("forced failure after the outbox write");
        }

        await tx.CommitAsync();
        return evt;
    }

    public async Task<long> CountAsync(OrganizationContext org, string sql, params (string Name, object Value)[] args)
    {
        await using var tx = await Tenant.BeginAsync(org);
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    public Task<long> ItemsAsync(OrganizationContext org, string nome) =>
        CountAsync(org, "SELECT count(*) FROM amostra_itens WHERE nome = @n", ("n", nome));

    public Task<long> PendingEventsAsync(OrganizationContext org) =>
        CountAsync(org, "SELECT count(*) FROM outbox_eventos WHERE processado_em IS NULL AND falhou_em IS NULL");

    public Task<long> AllEventsAsync(OrganizationContext org) => CountAsync(org, "SELECT count(*) FROM outbox_eventos");
}

/// <summary>A consumer that records deliveries and optionally fails; the effect is a row per delivery in amostra_itens (visible, countable).</summary>
internal sealed class RecordingConsumer(string name = "rec") : IOutboxConsumer
{
    public string Name { get; } = name;

    public string EventType => "amostra.item-criado";

    public List<Guid> Deliveries { get; } = [];

    public Func<OutboxMessage, bool> FailWhen { get; set; } = _ => false;

    public Func<OutboxMessage, TenantTransaction, Task>? Inside { get; set; }

    public async Task HandleAsync(OutboxMessage message, TenantTransaction transaction, CancellationToken cancellationToken)
    {
        lock (Deliveries)
        {
            Deliveries.Add(message.Id);
        }

        if (Inside is not null)
        {
            await Inside(message, transaction);
        }

        if (FailWhen(message))
        {
            throw new InvalidOperationException("consumer failure");
        }
    }
}
