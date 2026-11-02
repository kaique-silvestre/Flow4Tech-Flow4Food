using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Infrastructure.Outbox;

/// <summary>An event as the dispatcher hands it to consumers. <see cref="Id"/> is the idempotency key.</summary>
public sealed record OutboxMessage(Guid Id, OrganizationContext Organization, string EventType, string Payload, DateTimeOffset OccurredAt, int Attempts);

/// <summary>
/// Receives events of one type, at least once. It runs INSIDE the tenant transaction the dispatcher opened with the event's organization
/// (<see cref="TenantDatabase.BeginAsync"/>), so its data access is under RLS for that organization and commits or rolls back with the
/// "delivered" mark. Effects outside the database must be idempotent on <see cref="OutboxMessage.Id"/> themselves.
/// </summary>
public interface IOutboxConsumer
{
    string Name { get; }

    string EventType { get; }

    Task HandleAsync(OutboxMessage message, TenantTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>Remembers which consumer already applied which event (the inbox).</summary>
public interface IProcessedEventStore
{
    /// <summary>True the first time this consumer sees this event, false for any repeat. Recorded on the same transaction as the effect.</summary>
    Task<bool> TryMarkAsync(string consumer, OutboxMessage message, TenantTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>Inbox in <c>outbox_consumos</c>: the mark commits or rolls back with the consumer's own writes.</summary>
public sealed class DatabaseProcessedEventStore(IClock clock) : IProcessedEventStore
{
    public async Task<bool> TryMarkAsync(string consumer, OutboxMessage message, TenantTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var context = new OutboxContext(transaction);
        var now = clock.UtcNow;
        var org = message.Organization.OrganizationId;
        var inserted = await context.Database.ExecuteSqlAsync(
            $"INSERT INTO outbox_consumos (organizacao_id, evento_id, consumidor, consumido_em) VALUES ({org}, {message.Id}, {consumer}, {now}) ON CONFLICT DO NOTHING",
            cancellationToken);
        return inserted == 1;
    }
}

/// <summary>Applies <paramref name="effect"/> at most once per event id, however many times the event is delivered.</summary>
public sealed class IdempotentConsumer(
    string name,
    string eventType,
    IProcessedEventStore store,
    Func<OutboxMessage, TenantTransaction, CancellationToken, Task> effect) : IOutboxConsumer
{
    public string Name { get; } = name;

    public string EventType { get; } = eventType;

    public async Task HandleAsync(OutboxMessage message, TenantTransaction transaction, CancellationToken cancellationToken)
    {
        if (await store.TryMarkAsync(Name, message, transaction, cancellationToken))
        {
            await effect(message, transaction, cancellationToken);
        }
    }
}
