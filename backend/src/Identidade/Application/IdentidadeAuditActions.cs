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

    /// <summary>An administrator generated a new temporary credential for an operational account (spec 5.2.5).</summary>
    public const string ContaRedefinirAcesso = "conta:redefinir-acesso";

    /// <summary>Someone asked to recover access to an account with e-mail (known accounts only; the answer to the requester is identical either way).</summary>
    public const string ContaSolicitarRecuperacao = "conta:solicitar-recuperacao";

    /// <summary>Access was recovered by e-mail: new password, every session ended.</summary>
    public const string ContaRecuperarAcesso = "conta:recuperar-acesso";

    public const string ContaVincularGoogle = "conta:vincular-google";

    public const string ContaDesvincularGoogle = "conta:desvincular-google";

    public const string ConviteCriar = "convite:criar";

    public const string ConviteRevogar = "convite:revogar";

    public const string ConviteAceitar = "convite:aceitar";

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
