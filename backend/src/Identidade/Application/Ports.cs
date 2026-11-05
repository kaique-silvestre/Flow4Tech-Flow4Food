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
public sealed class ContaParaLogin(Guid contaId, Guid organizacaoId, TipoConta tipo, string senhaHash, bool deveTrocarSenha, bool ativa, int versaoCredencial, DateTimeOffset? temporariaExpiraEm)
{
    public Guid ContaId { get; } = contaId;

    public Guid OrganizacaoId { get; } = organizacaoId;

    public TipoConta Tipo { get; } = tipo;

    public string SenhaHash { get; } = senhaHash;

    public bool DeveTrocarSenha { get; } = deveTrocarSenha;

    public bool Ativa { get; } = ativa;

    public int VersaoCredencial { get; } = versaoCredencial;

    /// <summary>Until when the temporary credential works (null: not a temporary credential, or one created before migration 0008, which never expires).</summary>
    public DateTimeOffset? TemporariaExpiraEm { get; } = temporariaExpiraEm;

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
    Task<bool> InserirAsync(Guid organizacaoId, NovaConta conta, string senhaHash, bool deveTrocarSenha, DateTimeOffset? temporariaExpiraEm, CancellationToken cancellationToken);

    /// <summary>Reserves the e-mail platform-wide (one account, one organization). False when it belongs to another account.</summary>
    Task<bool> RegistrarEmailAsync(Guid organizacaoId, Guid contaId, string emailNormalizado, CancellationToken cancellationToken);

    Task<Conta?> ObterAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken);

    Task<ContaParaLogin?> ObterComCredencialAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken);

    /// <summary>
    /// The person changed their password: stores the new hash, clears <c>deve_trocar_senha</c> and bumps the credential version (ends the sessions of the old password).
    /// </summary>
    Task TrocarSenhaAsync(Guid organizacaoId, Guid contaId, string novoHash, CancellationToken cancellationToken);

    /// <summary>
    /// An administrator reset an OPERATIONAL account: stores the hash of the new temporary credential, forces the change at the next login, sets its deadline and bumps the credential version
    /// (every session of the old credential ends). False (nothing written) when the account is not an active operational one.
    /// </summary>
    Task<bool> RedefinirCredencialTemporariaAsync(Guid organizacaoId, Guid contaId, string novoHash, DateTimeOffset expiraEm, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a hash made with outdated parameters by one of the SAME password. Only if the stored hash is still <paramref name="hashAnterior"/> (a password changed meanwhile wins);
    /// keeps the credential version. False when it no longer matched.
    /// </summary>
    Task<bool> RehashAsync(Guid organizacaoId, Guid contaId, string hashAnterior, string novoHash, CancellationToken cancellationToken);

    /// <summary>Marks the account inactive and bumps the credential version. False when it was already inactive. Keeps every record (history).</summary>
    Task<bool> DesativarAsync(Guid organizacaoId, Guid contaId, DateTimeOffset quando, CancellationToken cancellationToken);

    /// <summary>Sets the owner flag of an ACTIVE account and bumps the credential version. False (nothing written) when the account is not active any more.</summary>
    Task<bool> DefinirDonoAsync(Guid organizacaoId, Guid contaId, bool dono, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the organization row (FOR NO KEY UPDATE: concurrent founders serialize, ordinary inserts that only reference the organization do not wait) until the unit
    /// of work ends and returns how many accounts it has, active or not. The founding owner may only be created when this is zero.
    /// </summary>
    Task<int> TravarOrganizacaoEContarContasAsync(Guid organizacaoId, CancellationToken cancellationToken);

    /// <summary>Locks the active owners of the organization until the unit of work ends and returns how many there are (serializes the "always one owner" rule).</summary>
    Task<int> TravarDonosAtivosAsync(Guid organizacaoId, CancellationToken cancellationToken);
}

/// <summary>
/// The breach check is required (<c>HibpIndisponivelRecusa</c>) and the service could not answer. The application services turn it into the typed result
/// <c>VerificacaoIndisponivel</c>; it never reaches a caller of those services.
/// </summary>
public sealed class VerificacaoDeSenhaIndisponivelException : Exception
{
    public VerificacaoDeSenhaIndisponivelException()
        : base("Password breach check unavailable.")
    {
    }

    public VerificacaoDeSenhaIndisponivelException(string message)
        : base(message)
    {
    }

    public VerificacaoDeSenhaIndisponivelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>How long a temporary credential works from the moment it is created (host option <c>Flow4Tech:Identidade:CredencialTemporariaHoras</c>).</summary>
public sealed record ValidadeDaCredencialTemporaria(TimeSpan Valor);
