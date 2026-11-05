using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Permissoes.Application;

/// <summary>
/// Names of the factory profiles. They are DATA: only <see cref="PoliticaDeFabrica"/> (the rules) and the interim profile source of the host (until ticket 06 persists profiles
/// per organization) may name them; an architecture test (<c>ProfileBranchingTests</c>) fails if any other code does, so nothing can branch on a profile name.
/// </summary>
public static class PerfisDeFabrica
{
    /// <summary>The organization owner: everything in the catalog.</summary>
    public const string Dono = "dono";

    /// <summary>Any other active account: only what concerns the account itself (its sessions, its password, its units). Nothing administrative, financial or of export (spec 5.5.4).</summary>
    public const string Conta = "conta";
}

/// <summary>
/// The rules of the factory profiles, as <see cref="PolicySet"/> data (spec 5.4.3). The persisted profiles of an organization are added to these rules by the policy source (ticket 06). The catalog is generated from the code; a permission named here that the catalog lacks stops the host at startup (<see cref="PolicySet"/> refuses it), so a rename
/// can never silently grant nothing.
/// </summary>
public static class PoliticaDeFabrica
{
    /// <summary>What every account may do for itself.</summary>
    private static readonly string[] DaPropriaConta =
    [
        "organizacao:ver-unidades",
        "organizacao:trocar-unidade",
        "sessao:trocar-senha",
        "sessao:ver-proprias",
        "sessao:revogar-propria",
        "conta:gerir-google",
    ];

    public static PolicySet Criar(IEnumerable<string> catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        var known = catalogo.ToList();
        var rules = known.Select(permission => new Rule(RuleEffect.Allow, PerfisDeFabrica.Dono, permission))
            .Concat(DaPropriaConta.Select(permission => new Rule(RuleEffect.Allow, PerfisDeFabrica.Conta, permission)))
            .ToList();
        return new PolicySet(known, rules);
    }
}

/// <summary>
/// Port: the profile keys a person holds in an organization (<see cref="ChavesDePerfil"/>): the factory profile of the account, the custom profiles assigned to it and its
/// private override profile. The host asks it per decision; the engine, not the caller, interprets the keys. An account that is gone or inactive holds none (everything is denied).
/// Takes the person explicitly so the simulator can ask for someone other than the caller.
/// </summary>
public interface IPerfisDoUsuario
{
    Task<IReadOnlyList<string>> ListarAsync(OrganizationContext organization, Guid userId, CancellationToken cancellationToken);
}
