using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>Audit actions of the Identidade module (published names are never renamed, see backend/audit.catalog.txt).</summary>
[AuditCatalog]
public static class IdentidadeAuditActions
{
    public const string ContaCriar = "conta:criar";

    public const string ContaDesativar = "conta:desativar";

    public const string ContaDefinirDono = "conta:definir-dono";

    public const string ContaTrocarSenha = "conta:trocar-senha";

    public const string AutenticacaoFalhar = "autenticacao:falhar";

    /// <summary>An account was locked by the login protection (too many failures or credential stuffing, spec 5.5).</summary>
    public const string AutenticacaoBloquear = "autenticacao:bloquear";

    /// <summary>A session started (login success, or the new session after a password change). The unit switch is <c>sessao:trocar-unidade</c> (Kernel).</summary>
    public const string SessaoIniciar = "sessao:iniciar";

    /// <summary>Logout: the person ended their own session with the refresh cookie.</summary>
    public const string SessaoEncerrar = "sessao:encerrar";

    /// <summary>A session was revoked from the sessions list, by its owner or (for someone else's) by an administrator.</summary>
    public const string SessaoRevogar = "sessao:revogar";

    /// <summary>A refresh token that had already been used was presented again: the whole session was revoked (spec 5.3.2).</summary>
    public const string SessaoRefreshReutilizado = "sessao:refresh-reutilizado";
}
