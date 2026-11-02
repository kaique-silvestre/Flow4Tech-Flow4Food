using Flow4Tech.Kernel;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// <see cref="IUnitOfWork"/> on top of <see cref="TenantDatabase"/>: <c>ExecuteAsync</c> opens ONE tenant transaction, publishes it as the
/// ambient transaction of the code running inside <c>work</c> (an <see cref="AsyncLocal{T}"/>, so it follows awaits and parallel tasks
/// started inside and nothing outside), and commits on normal return or rolls back on any exception. Infrastructure code that must join
/// the transaction (limit store, repositories, outbox writer) calls <see cref="Require"/>; there is no way to obtain it outside a scope.
/// A nested <c>ExecuteAsync</c> for the same organization joins the open transaction; for another organization it is refused.
/// Limits to respect: (1) one connection, so NO parallel work inside one unit of work (do not share the transaction across concurrent
/// tasks); (2) a nested scope that threw marks the unit faulted, so even if the outer code swallows the exception the outer
/// <c>ExecuteAsync</c> rolls back and throws instead of committing a half-done operation; (3) once the scope ends it is closed, and a task
/// that outlived it gets an exception from <see cref="Require"/>, never the finished transaction; (4) the counter row lock taken by a capped
/// decision is held until the WHOLE operation commits, so capped operations of the same user serialize and two operations touching different
/// counters in opposite order can deadlock: keep units short and take counters in a consistent order.
/// </summary>
public sealed class TenantUnitOfWork(TenantDatabase tenant) : IUnitOfWork
{
    private static readonly AsyncLocal<Active?> Ambient = new();

    public async Task<T> ExecuteAsync<T>(OrganizationContext organization, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(work);
        if (Ambient.Value is { Closed: false } open)
        {
            if (open.OrganizationId != organization.OrganizationId)
            {
                throw new InvalidOperationException("A unit of work is already open for another organization; a unit of work never spans two tenants.");
            }

            try
            {
                return await work(cancellationToken);
            }
            catch
            {
                open.Faulted = true;
                throw;
            }
        }

        await using var transaction = await tenant.BeginAsync(organization, cancellationToken);
        var scope = new Active(transaction, organization.OrganizationId);
        Ambient.Value = scope;
        try
        {
            var result = await work(cancellationToken);
            if (scope.Faulted)
            {
                throw new InvalidOperationException("A nested unit of work failed and its exception was swallowed; the whole unit of work is rolled back.");
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            scope.Closed = true;
            Ambient.Value = null;
        }
    }

    /// <summary>True while a unit of work for this organization is open in the current flow (used to refuse work that needs a transaction of its own).</summary>
    public static bool InProgress(Guid organizationId) => Ambient.Value is { Closed: false } open && open.OrganizationId == organizationId;

    /// <summary>The transaction of the unit of work in progress for this organization. Throws outside one: capped decisions and business writes never get a transaction of their own.</summary>
    public static TenantTransaction Require(Guid organizationId)
    {
        if (Ambient.Value is not { Closed: false } open)
        {
            throw new InvalidOperationException("No unit of work in progress: wrap the operation in IUnitOfWork.ExecuteAsync.");
        }

        if (open.OrganizationId != organizationId)
        {
            throw new InvalidOperationException("The unit of work in progress belongs to another organization.");
        }

        return open.Transaction;
    }

    private sealed class Active(TenantTransaction transaction, Guid organizationId)
    {
        public TenantTransaction Transaction { get; } = transaction;

        public Guid OrganizationId { get; } = organizationId;

        public bool Faulted { get; set; }

        public bool Closed { get; set; }
    }
}
