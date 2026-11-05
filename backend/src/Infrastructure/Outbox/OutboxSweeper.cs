using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Infrastructure.Outbox;

/// <summary>
/// Finds the organizations with pending events (the registry of migration 0011, read through <see cref="PendingOrganizationsDatabase"/>) and runs
/// <see cref="OutboxDispatcher.DispatchAsync"/> for each, one organization context at a time; so events of every organization are delivered without
/// any role that bypasses RLS and without reading another organization's data. After an organization is drained its registry row is removed in
/// that organization's own transaction (<c>outbox_encerrar_pendencia</c>), which keeps it if an event arrived meanwhile. An organization whose
/// events keep failing stays registered until they are delivered or parked.
/// </summary>
public sealed class OutboxSweeper(PendingOrganizationsDatabase registry, TenantDatabase tenant, OutboxDispatcher dispatcher)
{
    /// <summary>Organizations looked at per sweep (the rest are found by the next one).</summary>
    public const int DefaultBatch = 100;

    /// <summary>Delivers the pending events of up to <paramref name="batch"/> registered organizations; returns how many events were delivered.</summary>
    public async Task<int> SweepAsync(int batch = DefaultBatch, CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        foreach (var pending in await registry.ListAsync(batch, cancellationToken))
        {
            var organization = new OrganizationContext(pending.OrganizationId, Guid.Empty);
            delivered += await dispatcher.DispatchAsync(organization, cancellationToken);
            await CloseAsync(organization, cancellationToken);
        }

        return delivered;
    }

    private async Task CloseAsync(OrganizationContext organization, CancellationToken cancellationToken)
    {
        await using var tx = await tenant.BeginAsync(organization, cancellationToken);
        await using var context = new OutboxContext(tx);
        _ = await context.Database.SqlQuery<bool>($"SELECT outbox_encerrar_pendencia() AS \"Value\"").SingleAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
