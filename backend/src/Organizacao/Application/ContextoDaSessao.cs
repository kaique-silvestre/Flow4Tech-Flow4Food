using Flow4Tech.Kernel;

namespace Flow4Tech.Organizacao.Application;

/// <summary>The names the app shows in its header for the session: the company, the active unit and the signed-in person.</summary>
public sealed record ContextoDaSessaoNomes(string Organizacao, string Unidade, string Usuario);

/// <summary>Port: the names behind the ids of the session. Runs inside a unit of work (RLS filters; nothing names the organization in a filter).</summary>
public interface IContextoDaSessaoStore
{
    /// <summary>Null when one of the three no longer exists (an account removed while the session lives).</summary>
    Task<ContextoDaSessaoNomes?> LerAsync(Guid organizationId, Guid unitId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>The header context of the signed-in person. Organization, unit and user come from the authenticated session only; the route asks for no permission beyond the session itself, it only names what the caller already is.</summary>
public sealed class ContextoDaSessao(IUnitOfWork unitOfWork, IContextoDaSessaoStore store)
{
    public Task<ContextoDaSessaoNomes?> LerAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.LerAsync(request.Organization.OrganizationId, request.Organization.UnitId, request.UserId, ct), cancellationToken);
    }
}
