using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Permissoes.Infrastructure;

/// <summary>
/// The rules of an organization as the engine reads them (spec 5.4.4): the factory policy (code, with the catalog) plus what is persisted for the organization: the rules of
/// each custom profile under its key (<see cref="ChavesDePerfil.Personalizado"/>), the rules a profile inherits from the template it follows, and the individual overrides
/// of each person under their private key (<see cref="ChavesDePerfil.DoUsuario"/>). Read through the tenant transaction, so RLS decides what is visible; never cached, so a change
/// is in force for the very next decision. Inside a unit of work it joins it (one connection, a consistent snapshot of the operation); outside one it opens a short read-only
/// transaction. A persisted rule that names a permission the catalog no longer has is inert (it would deny anyway) instead of stopping the host.
/// </summary>
public sealed class PostgresPolicySource(TenantDatabase tenant, PolicySet factory) : IPolicySource
{
    public async Task<PolicySet> ObterAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (TenantUnitOfWork.InProgress(organizationId))
        {
            return await LoadAsync(TenantUnitOfWork.Require(organizationId), cancellationToken);
        }

        await using var transaction = await tenant.BeginAsync(new OrganizationContext(organizationId, Guid.Empty), cancellationToken);
        return await LoadAsync(transaction, cancellationToken);
    }

    private async Task<PolicySet> LoadAsync(TenantTransaction transaction, CancellationToken cancellationToken)
    {
        await using var context = new PermissoesDbContext(transaction);
        var profiles = await context.Perfis.AsNoTracking().ToListAsync(cancellationToken);
        var profileRules = await context.RegrasDosPerfis.AsNoTracking().ToListAsync(cancellationToken);
        var userRules = await context.RegrasDosUsuarios.AsNoTracking().ToListAsync(cancellationToken);

        var rules = new List<Rule>(factory.Rules);
        foreach (var row in profileRules)
        {
            rules.Add(PostgresPerfilStore.ToRule(row).ToRule(ChavesDePerfil.Personalizado(row.IdDoPerfil)));
        }

        foreach (var row in userRules)
        {
            rules.Add(PostgresPerfilStore.ToRule(row).ToRule(ChavesDePerfil.DoUsuario(row.UsuarioId)));
        }

        // A profile that follows its template also holds the template's rules (one level: a template that itself follows another is read as its own rules only).
        var own = rules.ToLookup(r => r.Profile);
        foreach (var follower in profiles.Where(p => p.AcompanhaModelo && p.Modelo is not null))
        {
            var key = ChavesDePerfil.Personalizado(follower.Id);
            rules.AddRange(own[follower.Modelo!].Select(r => r with { Profile = key }));
        }

        return new PolicySet(factory.KnownPermissions, rules.Where(r => factory.KnownPermissions.Contains(r.Permission)));
    }
}
