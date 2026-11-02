namespace Flow4Tech.Identidade.Domain;

/// <summary>The two kinds of account (spec 5.2.1): with e-mail (owner, manager, administrative) and operational (waiter, cashier: user name + password, no e-mail).</summary>
public enum TipoConta
{
    Email,
    Operacional,
}

/// <summary>
/// An account as the rules see it. Never carries the password hash (that lives in the credential, which only the authentication path reads).
/// <see cref="VersaoCredencial"/> grows on every event that must end the account's sessions (password change, deactivation, owner flag change): ticket 04 revokes sessions
/// that carry an older value.
/// </summary>
public sealed record Conta(
    Guid Id,
    Guid OrganizacaoId,
    TipoConta Tipo,
    string Nome,
    string? Email,
    string? Usuario,
    bool Dono,
    bool Ativa,
    int VersaoCredencial);
