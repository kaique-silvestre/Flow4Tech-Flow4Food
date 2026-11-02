using System.Text.Json;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Infrastructure.Outbox;

/// <summary>
/// Writes a domain event into the outbox ON THE CALLER'S tenant transaction: the event commits or rolls back together with the state
/// change made in that transaction. It never commits and never opens a transaction of its own.
/// </summary>
public sealed class OutboxWriter(IClock clock)
{
    public async Task AddAsync(TenantTransaction transaction, OrganizationContext organization, IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(domainEvent);

        await using var context = new OutboxContext(transaction);
        context.Eventos.Add(new OutboxEventRow
        {
            Id = domainEvent.EventId,
            OrganizacaoId = organization.OrganizationId,
            Tipo = domainEvent.EventType,
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
            OcorridoEm = clock.UtcNow,
        });
        await context.SaveChangesAsync(cancellationToken);
    }
}
