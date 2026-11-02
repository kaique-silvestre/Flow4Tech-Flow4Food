using Flow4Tech.Identidade.Domain;

namespace Flow4Tech.Identidade.Application;

/// <summary>Password hashing (Argon2id, ADR-005). Hashes are opaque encoded strings; callers never look inside.</summary>
public interface IPasswordHasher
{
    Task<string> HashAsync(string password, CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(string password, string encodedHash, CancellationToken cancellationToken = default);

    /// <summary>Does the SAME work as <see cref="VerifyAsync"/> against a fake hash and answers false: used when there is no account, so the response time does not tell (spec 5.2.8).</summary>
    Task VerifyDummyAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>True when the stored hash was made with parameters other than the current ones (rehash on the next successful login).</summary>
    bool NeedsRehash(string encodedHash);
}

/// <summary>Is this password in a known breach? k-anonymity: an implementation never sends the password (spec 5.2.2).</summary>
public interface ICompromisedPasswordCheck
{
    Task<bool> IsCompromisedAsync(string password, CancellationToken cancellationToken = default);
}

public interface ITemporaryCredentialGenerator
{
    string Generate();
}

/// <summary>A temporary credential: shown ONCE, to the person who created the account. Only its hash is stored. Printing it never reveals the value.</summary>
public sealed class CredencialTemporaria(string valor)
{
    public string Valor { get; } = valor;

    public override string ToString() => "[credencial temporaria]";
}

/// <summary>What the login needs about an account, including the password hash. Never serialized or logged: it prints as a placeholder.</summary>
public sealed class ContaParaLogin(Guid contaId, Guid organizacaoId, TipoConta tipo, string senhaHash, bool deveTrocarSenha, bool ativa, int versaoCredencial)
{
    public Guid ContaId { get; } = contaId;

    public Guid OrganizacaoId { get; } = organizacaoId;

    public TipoConta Tipo { get; } = tipo;

    public string SenhaHash { get; } = senhaHash;

    public bool DeveTrocarSenha { get; } = deveTrocarSenha;

    public bool Ativa { get; } = ativa;

    public int VersaoCredencial { get; } = versaoCredencial;

    public override string ToString() => "[conta para login]";
}

/// <summary>
/// Pre-authentication lookup (spec 5.2.4): finds ONE account by an exact key BEFORE any organization context exists. Backed by minimal SECURITY DEFINER
/// functions; there is no listing and no search.
/// </summary>
public interface IPreAuthLookup
{
    Task<ContaParaLogin?> LocalizarOperacionalAsync(Guid organizacaoId, string usuarioNormalizado, CancellationToken cancellationToken);

    Task<ContaParaLogin?> LocalizarPorEmailAsync(string emailNormalizado, CancellationToken cancellationToken);
}

public sealed record NovaConta(Guid Id, TipoConta Tipo, string Nome, string? Email, string? Usuario, bool Dono, DateTimeOffset CriadaEm);

/// <summary>Account storage of the current organization. Every member runs INSIDE a unit of work (RLS does the filtering).</summary>
public interface IContaStore
{
    /// <summary>Inserts the account and its credential. False when the user name (or e-mail) is already taken; nothing is written then.</summary>
    Task<bool> InserirAsync(Guid organizacaoId, NovaConta conta, string senhaHash, bool deveTrocarSenha, CancellationToken cancellationToken);

    /// <summary>Reserves the e-mail platform-wide (one account, one organization). False when it belongs to another account.</summary>
    Task<bool> RegistrarEmailAsync(Guid organizacaoId, Guid contaId, string emailNormalizado, CancellationToken cancellationToken);

    Task<Conta?> ObterAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken);

    Task<ContaParaLogin?> ObterComCredencialAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken);

    /// <summary>
    /// The person changed their password: stores the new hash, clears <c>deve_trocar_senha</c> and bumps the credential version (ends the sessions of the old password).
    /// </summary>
    Task TrocarSenhaAsync(Guid organizacaoId, Guid contaId, string novoHash, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a hash made with outdated parameters by one of the SAME password. Only if the stored hash is still <paramref name="hashAnterior"/> (a password changed meanwhile wins);
    /// keeps the credential version. False when it no longer matched.
    /// </summary>
    Task<bool> RehashAsync(Guid organizacaoId, Guid contaId, string hashAnterior, string novoHash, CancellationToken cancellationToken);

    /// <summary>Marks the account inactive and bumps the credential version. False when it was already inactive. Keeps every record (history).</summary>
    Task<bool> DesativarAsync(Guid organizacaoId, Guid contaId, DateTimeOffset quando, CancellationToken cancellationToken);

    Task DefinirDonoAsync(Guid organizacaoId, Guid contaId, bool dono, CancellationToken cancellationToken);

    /// <summary>Locks the active owners of the organization until the unit of work ends and returns how many there are (serializes the "always one owner" rule).</summary>
    Task<int> TravarDonosAtivosAsync(Guid organizacaoId, CancellationToken cancellationToken);
}
