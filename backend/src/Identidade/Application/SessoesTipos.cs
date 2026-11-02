using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Identidade.Application;

/// <summary>Who a token is for. <see cref="App"/> is the customers' app; <see cref="Control"/> (Control Tower, phase 1B) is only a reserved value here: the realms have distinct keys and audiences and a token of one never validates in the other.</summary>
public enum RealmDeToken
{
    App,
    Control,
}

/// <summary>What an access token says. The active UNIT is deliberately not here: it lives in the session row and changes without a new token.</summary>
public sealed record ClaimsDeAcesso(Guid ContaId, Guid OrganizacaoId, Guid SessaoId, int VersaoCredencial);

/// <summary>Issues and validates access tokens (signature, algorithm, issuer, audience, lifetime). Implemented in Infrastructure over an audited library.</summary>
public interface IEmissorDeTokens
{
    TimeSpan ValidadeDoAcesso { get; }

    string EmitirAcesso(RealmDeToken realm, ClaimsDeAcesso claims);

    /// <summary>The claims of a valid token of <paramref name="realm"/>, or null for ANY problem (the caller never learns which).</summary>
    ClaimsDeAcesso? ValidarAcesso(RealmDeToken realm, string token);
}

/// <summary>The two session clocks (spec 5.3.4): inactivity slides with activity, the absolute limit never moves. Ticket 10 makes them per profile and organization.</summary>
public sealed record RelogiosDaSessao(TimeSpan Inatividade, TimeSpan Absoluta, TimeSpan IntervaloDeAtividade);

/// <summary>Where the request came from, already truncated by the caller: kept to show "where am I signed in" and for the new-login alert (ticket 08).</summary>
public sealed record ClienteDaSessao(string? Ip, string? Agente);

public sealed record SessaoEmitida(
    Guid SessaoId,
    Guid ContaId,
    Guid OrganizacaoId,
    Guid? UnidadeAtivaId,
    bool DeveTrocarSenha,
    string AccessToken,
    DateTimeOffset AccessExpiraEm,
    string RefreshToken,
    DateTimeOffset SessaoExpiraEm)
{
    /// <summary>The refresh token is a secret: it prints as a placeholder.</summary>
    public override string ToString() => "[sessao emitida]";
}

/// <summary>The authenticated session of a request, read from the database on every request.</summary>
public sealed record SessaoAutenticada(Guid SessaoId, Guid ContaId, Guid OrganizacaoId, Guid? UnidadeAtivaId, bool DeveTrocarSenha);

public sealed record SessaoResumo(Guid Id, Guid ContaId, Guid? UnidadeAtivaId, DateTimeOffset CriadaEm, DateTimeOffset UltimaAtividadeEm, DateTimeOffset ExpiraEm, string? Ip, string? Agente, bool Atual);

public enum StatusRenovacao
{
    Renovada,

    /// <summary>Unknown, malformed, expired, revoked or out-of-date (credential changed) session. The caller answers the same 401.</summary>
    Invalida,

    /// <summary>An already used refresh was presented: the session was revoked, audited and the security event raised. The caller answers the same 401 as <see cref="Invalida"/>.</summary>
    Reutilizada,
}

public sealed record ResultadoRenovacao(StatusRenovacao Status, SessaoEmitida? Sessao);

public enum ResultadoRevogacao
{
    Revogada,

    /// <summary>No such live session, or one the caller may not touch: indistinguishable on purpose.</summary>
    NaoEncontrada,
}

/// <summary>The units an account is linked to. The host adapts the Organizacao port to it, so Identidade depends on no other module (spec 5.1.4: one unit makes it active).</summary>
public interface IUnidadesDaConta
{
    /// <summary>Runs INSIDE a unit of work of <paramref name="organizacaoId"/>.</summary>
    Task<IReadOnlyList<Guid>> ListarAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken);
}

/// <summary>A session row joined with the facts about its account that decide whether the session still lives.</summary>
public sealed record SessaoParaRequisicao(
    Guid Id,
    Guid ContaId,
    Guid? UnidadeAtivaId,
    bool DeveTrocarSenha,
    int VersaoCredencialDaSessao,
    int VersaoCredencialDaConta,
    bool ContaAtiva,
    DateTimeOffset UltimaAtividadeEm,
    DateTimeOffset ExpiraInatividadeEm,
    DateTimeOffset ExpiraEm,
    DateTimeOffset? RevogadaEm)
{
    /// <summary>Not revoked, inside both clocks, the account active and its credential version still the one the session was started with.</summary>
    public bool EstaViva(DateTimeOffset agora) =>
        RevogadaEm is null && agora < ExpiraInatividadeEm && agora < ExpiraEm && ContaAtiva && VersaoCredencialDaSessao == VersaoCredencialDaConta;
}

public sealed record NovaSessao(
    Guid Id,
    Guid ContaId,
    Guid? UnidadeAtivaId,
    Guid FamiliaId,
    bool DeveTrocarSenha,
    int VersaoCredencial,
    DateTimeOffset CriadaEm,
    DateTimeOffset ExpiraInatividadeEm,
    DateTimeOffset ExpiraEm,
    string? Ip,
    string? Agente);

public sealed record RefreshEncontrado(Guid Id, Guid SessaoId, DateTimeOffset? UsadoEm);

/// <summary>Session storage of the current organization. EVERY member runs inside a unit of work (RLS filters; nothing filters on the organization by hand).</summary>
public interface ISessaoStore
{
    Task InserirAsync(Guid organizacaoId, NovaSessao sessao, CancellationToken cancellationToken);

    Task InserirRefreshAsync(Guid organizacaoId, Guid id, Guid sessaoId, byte[] hash, DateTimeOffset agora, CancellationToken cancellationToken);

    Task<SessaoParaRequisicao?> ObterParaRequisicaoAsync(Guid organizacaoId, Guid sessaoId, CancellationToken cancellationToken);

    /// <summary>Finds the refresh row by the SHA-256 of the token and LOCKS it (FOR UPDATE) until the unit of work ends: two requests with the same token serialize here, and the second one reads the first one's <c>usado_em</c>.</summary>
    Task<RefreshEncontrado?> ObterRefreshTravadoAsync(Guid organizacaoId, byte[] hash, CancellationToken cancellationToken);

    Task MarcarRefreshUsadoAsync(Guid organizacaoId, Guid refreshId, DateTimeOffset agora, CancellationToken cancellationToken);

    Task PodarRefreshUsadosAsync(Guid organizacaoId, Guid sessaoId, DateTimeOffset usadosAntesDe, CancellationToken cancellationToken);

    Task TocarAtividadeAsync(Guid organizacaoId, Guid sessaoId, DateTimeOffset agora, DateTimeOffset expiraInatividadeEm, CancellationToken cancellationToken);

    /// <summary>Revokes a LIVE session. False when it was already revoked (nothing written).</summary>
    Task<bool> RevogarAsync(Guid organizacaoId, Guid sessaoId, string motivo, DateTimeOffset agora, CancellationToken cancellationToken);

    Task RevogarOutrasDaContaAsync(Guid organizacaoId, Guid contaId, Guid exceto, string motivo, DateTimeOffset agora, CancellationToken cancellationToken);

    Task DefinirUnidadeAtivaAsync(Guid organizacaoId, Guid sessaoId, Guid unidadeId, CancellationToken cancellationToken);

    Task<SessaoParaListar?> ObterVivaAsync(Guid organizacaoId, Guid sessaoId, DateTimeOffset agora, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessaoParaListar>> ListarVivasAsync(Guid organizacaoId, Guid? contaId, DateTimeOffset agora, CancellationToken cancellationToken);
}

public sealed record SessaoParaListar(Guid Id, Guid ContaId, Guid? UnidadeAtivaId, DateTimeOffset CriadaEm, DateTimeOffset UltimaAtividadeEm, DateTimeOffset ExpiraEm, string? Ip, string? Agente);

/// <summary>Raises a security event through the outbox on the CURRENT unit of work (it commits or rolls back with it).</summary>
public interface IEventosDeSeguranca
{
    Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when a refresh token that had already been used is presented again (spec 5.3.2, 5.5.6). No consumer yet (alerts are ticket 08). Stable name: never rename it once published.
/// Carries ids only, never a token.
/// </summary>
public sealed record RefreshReutilizado(Guid EventId, Guid OrganizacaoId, Guid SessaoId, Guid ContaId, DateTimeOffset DetectadoEm) : IDomainEvent
{
    public const string Type = "seguranca.refresh-reutilizado";

    public string EventType => Type;
}
