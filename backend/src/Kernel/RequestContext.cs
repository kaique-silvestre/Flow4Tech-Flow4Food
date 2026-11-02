namespace Flow4Tech.Kernel;

/// <summary>
/// Who is asking and in which organization/unit, resolved ONCE per request from the authenticated session by the host (never from the
/// route, body, query or a client header; spec 5.1). Business code takes this and hands it to <see cref="IUnitOfWork"/>; it does not build
/// an <see cref="OrganizationContext"/> itself. Reading it outside a request that resolved a context throws: there is no ambient default.
/// </summary>
public interface IRequestContext
{
    Guid UserId { get; }

    OrganizationContext Organization { get; }
}

public static class RequestContextUnitOfWorkExtensions
{
    /// <summary>Runs <paramref name="work"/> in the unit of work of the request's own organization (see <see cref="IUnitOfWork"/>).</summary>
    public static Task<T> ExecuteAsync<T>(this IUnitOfWork unitOfWork, IRequestContext request, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request.Organization, work, cancellationToken);
    }
}
