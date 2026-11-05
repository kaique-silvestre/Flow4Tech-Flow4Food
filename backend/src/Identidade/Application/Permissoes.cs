using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// Permissions of the account administration routes (ticket 09, spec 5.2.10): who may invite people and who may hand an operational account a new temporary credential. Held by the owner (the whole
/// catalog) and by custom profiles that include them; the factory profile <c>conta</c> has neither.
/// </summary>
[PermissionCatalog]
public static class UsuarioPermissions
{
    /// <summary>Create and revoke invitations.</summary>
    public const string Convidar = "usuario:convidar";

    /// <summary>Generate a new temporary credential for an operational account (spec 5.2.5).</summary>
    public const string RedefinirAcesso = "usuario:redefinir-acesso";
}

/// <summary>
/// Permissions of the routes a person reaches BEFORE having a session (accept an invitation, ask for and redeem a recovery link). They are PUBLIC routes (<c>RoutePermissions.PublicWithoutSession</c>): the
/// token in the body is the credential, so the engine is not asked; the names exist so every route still declares one catalog permission (ticket 10) and the contract says what the route is.
/// </summary>
[PermissionCatalog]
public static class AcessoPublicoPermissions
{
    public const string AceitarConvite = "convite:aceitar";

    public const string SolicitarRecuperacao = "recuperacao:solicitar";

    public const string RedefinirSenha = "recuperacao:redefinir";
}

/// <summary>The signed-in person managing the Google link of THEIR OWN account (the factory profile <c>conta</c> holds it).</summary>
[PermissionCatalog]
public static class ContaPermissions
{
    public const string GerirGoogle = "conta:gerir-google";
}
