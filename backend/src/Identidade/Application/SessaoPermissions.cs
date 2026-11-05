using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// Permissions declared by the session routes (ticket 04). Today the route only DECLARES them (ticket 05 turns the declaration into a decision through the permission handler):
/// <c>sessao:entrar</c>, <c>sessao:renovar</c> and <c>sessao:sair</c> are the three PUBLIC routes (login and the two cookie routes), so a handler must treat a route that
/// is also in the public allowlist as needing no subject. <c>sessao:revogar</c> is for sessions of OTHER people; ending your own session is always allowed.
/// </summary>
[PermissionCatalog]
public static class SessaoPermissions
{
    public const string Entrar = "sessao:entrar";

    public const string Renovar = "sessao:renovar";

    public const string Sair = "sessao:sair";

    public const string TrocarSenha = "sessao:trocar-senha";

    public const string VerProprias = "sessao:ver-proprias";

    public const string VerTodas = "sessao:ver-todas";

    /// <summary>Ending one of the caller's OWN sessions (the route <c>POST /sessoes/{id}/revogar</c> declares this one; ending someone else's is <see cref="Revogar"/>).</summary>
    public const string RevogarPropria = "sessao:revogar-propria";

    /// <summary>Ending a session of ANOTHER person. Until the application handler asks the engine for it (ticket 06), the service still lets only an owner do it.</summary>
    public const string Revogar = "sessao:revogar";
}
