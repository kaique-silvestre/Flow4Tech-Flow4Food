using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Permissoes.Application;

/// <summary>
/// Where the engine reads the rules of an organization from (spec 5.4.4). The persisted implementation merges the factory rules (code) with the profiles, rules and
/// overrides of the organization, read through the tenant transaction (RLS); it never caches, so a change is visible to the next decision.
/// </summary>
public interface IPolicySource
{
    Task<PolicySet> ObterAsync(Guid organizationId, CancellationToken cancellationToken);
}

/// <summary>A fixed policy for every organization: the factory policy in tests and tools that need no database.</summary>
public sealed class StaticPolicySource(PolicySet policies) : IPolicySource
{
    public Task<PolicySet> ObterAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(policies);
}

/// <summary>How the profile keys the engine sees are spelled. A custom profile is its id; the overrides of one person are one more private profile, so the engine needs no special case.</summary>
public static class ChavesDePerfil
{
    public static string Personalizado(Guid id) => id.ToString("D");

    public static string DoUsuario(Guid userId) => "usuario:" + userId.ToString("D");
}
