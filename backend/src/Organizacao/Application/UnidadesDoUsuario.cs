using Flow4Tech.Kernel;
using Flow4Tech.Organizacao.Domain;

namespace Flow4Tech.Organizacao.Application;

/// <summary>Port: the user-unit links of the current organization. Runs inside a unit of work (the organization context comes from it, RLS does the filtering).</summary>
public interface IUnidadeVinculos
{
    Task<bool> TemVinculoAsync(Guid organizationId, Guid userId, Guid unitId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Unidade>> ListarDoUsuarioAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Units as the signed-in user sees them. Every operation runs in the unit of work of the context resolved from the session; none of them
/// builds that context, and a user only ever sees or switches to units they are linked to.
/// </summary>
public sealed class UnidadesDoUsuario(IUnitOfWork unitOfWork, IUnidadeVinculos vinculos)
{
    /// <summary>Does <paramref name="userId"/> have a link to the unit in <paramref name="session"/>? Used by the host to resolve the request context (403 when not).</summary>
    public Task<bool> TemVinculoAsync(Guid userId, OrganizationContext session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return unitOfWork.ExecuteAsync(session, ct => vinculos.TemVinculoAsync(session.OrganizationId, userId, session.UnitId, ct), cancellationToken);
    }

    /// <summary>The units the signed-in user is linked to, by name.</summary>
    public Task<IReadOnlyList<Unidade>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => vinculos.ListarDoUsuarioAsync(request.Organization.OrganizationId, request.UserId, ct), cancellationToken);
    }

    /// <summary>True when the user may make <paramref name="destino"/> the active unit: only a unit they have a link to. Another organization's unit never matches (RLS).</summary>
    public Task<bool> PodeAtivarAsync(IRequestContext request, Guid destino, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => vinculos.TemVinculoAsync(request.Organization.OrganizationId, request.UserId, destino, ct), cancellationToken);
    }
}
