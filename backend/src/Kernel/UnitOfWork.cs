namespace Flow4Tech.Kernel;

/// <summary>
/// The business transaction, as the Application layer sees it (ADR-006 owner decision C). Everything done inside <c>work</c> (the
/// authorization decision with its cumulative-limit reservation, the state change, the outbox write) shares ONE database transaction for
/// the organization: it commits when <c>work</c> returns and rolls back, reservation included, when it throws. The contract names no
/// database type; the implementation lives in Infrastructure (<c>TenantUnitOfWork</c>).
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(OrganizationContext organization, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
}

public static class UnitOfWorkExtensions
{
    public static Task ExecuteAsync(this IUnitOfWork unitOfWork, OrganizationContext organization, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(work);
        return unitOfWork.ExecuteAsync<object?>(
            organization,
            async ct =>
            {
                await work(ct);
                return null;
            },
            cancellationToken);
    }
}
