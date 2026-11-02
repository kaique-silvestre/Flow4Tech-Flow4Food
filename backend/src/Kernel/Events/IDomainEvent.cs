namespace Flow4Tech.Kernel.Events;

/// <summary>
/// A fact that happened inside a module's transaction. Written to the outbox in that same transaction and delivered later,
/// at least once: <see cref="EventId"/> is the idempotency key consumers deduplicate on.
/// Serialized as JSON, so keep implementations simple records.
/// </summary>
public interface IDomainEvent
{
    /// <summary>Stable identity of this occurrence (UUID v7). A redelivery carries the same id.</summary>
    Guid EventId { get; }

    /// <summary>Stable name consumers subscribe to, e.g. <c>amostra.item-criado</c>. Never rename once published.</summary>
    string EventType { get; }
}
