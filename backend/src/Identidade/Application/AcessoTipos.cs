using System.Security.Cryptography;
using System.Text;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// Knobs of the invitation and recovery flows (host options <c>Flow4Tech:Identidade:ConviteDias</c>, <c>RecuperacaoMinutos</c> and <c>Flow4Tech:App:BaseUrl</c>; ticket 10 moves them to layered
/// settings). <see cref="UrlBase"/> is the front end that receives the link; the token travels in the URL FRAGMENT (<c>#token=</c>), which browsers do not send to servers or in referrers.
/// </summary>
public sealed record OpcoesDeAcesso(TimeSpan ValidadeDoConvite, TimeSpan ValidadeDaRecuperacao, Uri UrlBase)
{
    public string LinkDeConvite(string token) => $"{UrlBase.AbsoluteUri.TrimEnd('/')}/convite#token={token}";

    public string LinkDeRecuperacao(string token) => $"{UrlBase.AbsoluteUri.TrimEnd('/')}/recuperar#token={token}";
}

/// <summary>An implementation of <see cref="IEnviadorDeEmail"/> raises this when the provider fails. The flows treat it as "not delivered" (logged without the address or the body); any other exception is a bug and propagates.</summary>
public sealed class FalhaNoEnvioDeEmailException : Exception
{
    public FalhaNoEnvioDeEmailException()
        : base("The e-mail provider failed.")
    {
    }

    public FalhaNoEnvioDeEmailException(string message)
        : base(message)
    {
    }

    public FalhaNoEnvioDeEmailException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A store raises this when a profile or unit named by an invitation does not exist in the organization (the composite foreign key refused it).</summary>
public sealed class ReferenciaInvalidaException : Exception
{
    public ReferenciaInvalidaException()
        : base("Referenced object does not exist in this organization.")
    {
    }

    public ReferenciaInvalidaException(string message)
        : base(message)
    {
    }

    public ReferenciaInvalidaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Signal inside a unit of work: the single-use token was taken by someone else meanwhile, so what the unit wrote must roll back (a return value would not).</summary>
public sealed class TokenNaoUtilizavelException : Exception
{
    public TokenNaoUtilizavelException()
        : base("The token can no longer be used.")
    {
    }

    public TokenNaoUtilizavelException(string message)
        : base(message)
    {
    }

    public TokenNaoUtilizavelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Single-use tokens of the invitation (<c>c1</c>) and recovery (<c>a1</c>) flows: <c>prefix.&lt;organization id, 32 hex&gt;.&lt;256 random bits, base64url&gt;</c>, the same shape as the refresh token. The organization is a LOCATOR
/// (the lookup by hash runs under RLS and needs it; it is not a secret and a wrong one finds nothing); the random part is the secret. ONLY the SHA-256 of the whole string is stored.
/// </summary>
public static class TokenDeUso
{
    public const string Convite = "c1";

    public const string Recuperacao = "a1";

    public static (string Token, byte[] Hash) Criar(string prefixo, Guid organizacaoId)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefixo);
        var token = $"{prefixo}.{organizacaoId:N}.{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        return (token, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool TentarLer(string? token, string prefixo, out Guid organizacaoId, out byte[] hash)
    {
        organizacaoId = Guid.Empty;
        hash = [];
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != prefixo)
        {
            return false;
        }

        var valid = parts[1].Length == 32 && Guid.TryParseExact(parts[1], "N", out organizacaoId) && organizacaoId != Guid.Empty && parts[2].Length == 43 && parts[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        if (!valid)
        {
            organizacaoId = Guid.Empty;
            return false;
        }

        hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return true;
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// ---- invitations -------------------------------------------------------------------------------------------------------------------------------

public sealed record NovoConvite(Guid Id, string Email, string Nome, Guid? PerfilId, Guid? UnidadeId, ReadOnlyMemory<byte> TokenHash, Guid CriadoPor, DateTimeOffset CriadoEm, DateTimeOffset ExpiraEm)
{
    /// <summary>The hash is not a secret but is not worth printing either.</summary>
    public override string ToString() => "[convite]";
}

public sealed record ConviteEncontrado(Guid Id, string Email, string Nome, Guid? PerfilId, Guid? UnidadeId, Guid CriadoPor, DateTimeOffset ExpiraEm, DateTimeOffset? UsadoEm, DateTimeOffset? RevogadoEm);

/// <summary>Invitations of the current organization. Every member runs INSIDE a unit of work (RLS filters; nothing filters on the organization by hand).</summary>
public interface IConvitesStore
{
    /// <summary>Is there an account with this e-mail in THIS organization (RLS: other organizations are invisible, so nothing leaks across tenants).</summary>
    Task<bool> EmailJaEhMembroAsync(Guid organizacaoId, string email, CancellationToken cancellationToken);

    /// <summary>Revokes the pending invitations of the e-mail (one pending invitation per address).</summary>
    Task RevogarPendentesDoEmailAsync(Guid organizacaoId, string email, DateTimeOffset agora, CancellationToken cancellationToken);

    /// <summary>Raises <see cref="ReferenciaInvalidaException"/> when the profile or the unit is not of this organization.</summary>
    Task InserirAsync(Guid organizacaoId, NovoConvite convite, CancellationToken cancellationToken);

    Task<ConviteEncontrado?> ObterPorHashAsync(Guid organizacaoId, byte[] hash, CancellationToken cancellationToken);

    /// <summary>Atomic single use: marks the invitation used by the new account only if it is still pending and alive. False when it is not (nothing written).</summary>
    Task<bool> MarcarUsadoAsync(Guid organizacaoId, Guid conviteId, Guid contaId, DateTimeOffset agora, CancellationToken cancellationToken);

    /// <summary>Revokes a PENDING invitation. False when it was already used, revoked or does not exist.</summary>
    Task<bool> RevogarAsync(Guid organizacaoId, Guid conviteId, DateTimeOffset agora, CancellationToken cancellationToken);
}

public enum StatusConvite
{
    Criado,
    EmailInvalido,
    NomeInvalido,

    /// <summary>The address already has an account in the inviter's own organization.</summary>
    JaEhMembro,
    Negado,
    PerfilOuUnidadeInvalidos,
}

public sealed record ResultadoConvite(StatusConvite Status, Guid? ConviteId, DateTimeOffset? ExpiraEm, bool EmailEnviado);

public enum StatusAceite
{
    Aceito,

    /// <summary>Unknown, malformed, expired, revoked or already used: ONE answer for all of them.</summary>
    TokenInvalido,
    SenhaInvalida,
    SenhaVazada,
    VerificacaoIndisponivel,

    /// <summary>The address already belongs to an account (this or another organization). Only the holder of the token, who owns the mailbox, sees this.</summary>
    EmailIndisponivel,
}

public sealed record ResultadoAceite(StatusAceite Status, Guid? ContaId, IReadOnlyList<ProblemaDeSenha> Problemas);

/// <summary>An invitation was accepted: the account exists. Carries what the invitation asked for (profile, initial unit) and who invited, so the side that owns profiles and units applies it with the INVITER's rights.</summary>
public sealed record ConviteAceito(Guid EventId, Guid OrganizacaoId, Guid ContaId, Guid ConviteId, Guid? PerfilId, Guid? UnidadeId, Guid ConvidadoPor, DateTimeOffset AceitoEm) : IDomainEvent
{
    public const string Type = "identidade.convite-aceito";

    public string EventType => Type;
}

// ---- recovery ------------------------------------------------------------------------------------------------------------------------------------

public sealed record RecuperacaoEncontrada(Guid Id, Guid ContaId, DateTimeOffset ExpiraEm, DateTimeOffset? UsadoEm);

public interface IRecuperacoesStore
{
    /// <summary>Marks every unused recovery token of the account as used (a new request, or a finished recovery, supersedes them).</summary>
    Task InvalidarPendentesAsync(Guid organizacaoId, Guid contaId, DateTimeOffset agora, CancellationToken cancellationToken);

    Task InserirAsync(Guid organizacaoId, Guid id, Guid contaId, byte[] hash, DateTimeOffset agora, DateTimeOffset expiraEm, CancellationToken cancellationToken);

    Task<RecuperacaoEncontrada?> ObterPorHashAsync(Guid organizacaoId, byte[] hash, CancellationToken cancellationToken);

    /// <summary>Atomic single use: marks the token used only if it is unused and alive. False when it is not (nothing written).</summary>
    Task<bool> ConsumirAsync(Guid organizacaoId, Guid id, DateTimeOffset agora, CancellationToken cancellationToken);
}

/// <summary>Ends every session of an account (the recovery proved the previous credential cannot be trusted).</summary>
public interface IRevogacaoDeSessoes
{
    Task RevogarTodasDaContaAsync(Guid organizacaoId, Guid contaId, string motivo, DateTimeOffset agora, CancellationToken cancellationToken);
}

public enum StatusRecuperacao
{
    Redefinida,

    /// <summary>Unknown, malformed, expired, used or superseded, or the account is gone/inactive: ONE answer.</summary>
    TokenInvalido,
    SenhaInvalida,
    SenhaVazada,
    VerificacaoIndisponivel,
}

public sealed record ResultadoRecuperacao(StatusRecuperacao Status, IReadOnlyList<ProblemaDeSenha> Problemas);

/// <summary>Access was recovered by e-mail: the password was replaced and every session ended. The person is told by e-mail (consumer in Infrastructure).</summary>
public sealed record AcessoRecuperado(Guid EventId, Guid OrganizacaoId, Guid ContaId, DateTimeOffset RecuperadoEm) : IDomainEvent
{
    public const string Type = "seguranca.acesso-recuperado";

    public string EventType => Type;
}

// ---- credential reset by an administrator -----------------------------------------------------------------------------------------------------

public enum StatusRedefinicao
{
    Redefinida,
    Negada,
    NaoEncontrada,

    /// <summary>Accounts with e-mail recover their access by e-mail, nobody generates a credential for them.</summary>
    ContaComEmail,
}

public sealed record ResultadoRedefinicao(StatusRedefinicao Status, CredencialTemporaria? Credencial, DateTimeOffset? ExpiraEm);
