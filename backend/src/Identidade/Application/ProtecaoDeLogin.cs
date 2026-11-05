using Flow4Tech.Identidade.Domain;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// The knobs of the login protection (spec 5.5.1, 5.5.2). Defaults are provisional until the layered settings of ticket 10 (spec 5.7 item 5). A lock starts when an account (or an
/// address) reaches its limit of failures and grows with every further failure: <c>base * 2^(failures - limit)</c> up to <see cref="BloqueioMaximo"/>.
/// </summary>
public sealed record LimitesDeLogin
{
    /// <summary>Failures in a row (inside <see cref="JanelaDeFalhas"/>) before an account with e-mail locks.</summary>
    public int FalhasParaBloquearEmail { get; init; } = 5;

    /// <summary>Operational accounts share devices at the counter and are typed fast, so the limit is higher; they also reach fewer things.</summary>
    public int FalhasParaBloquearOperacional { get; init; } = 8;

    public TimeSpan BloqueioBase { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BloqueioMaximo { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Failures further apart than this do not add up.</summary>
    public TimeSpan JanelaDeFalhas { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Failures of ANY accounts from one address before the address locks (a restaurant behind one NAT stays well below it).</summary>
    public int FalhasParaBloquearIp { get; init; } = 30;

    /// <summary>Credential stuffing: this many DIFFERENT accounts failing from one address lock that address for <see cref="BloqueioDeStuffing"/>.</summary>
    public int ContasDistintasPorIp { get; init; } = 10;

    /// <summary>Credential stuffing: this many DIFFERENT addresses failing against one account lock that account for <see cref="BloqueioDeStuffing"/>.</summary>
    public int IpsDistintosPorConta { get; init; } = 5;

    public TimeSpan BloqueioDeStuffing { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Failures of one account after which a CAPTCHA is asked (only if a provider is configured).</summary>
    public int FalhasParaCaptchaConta { get; init; } = 3;

    /// <summary>Failures from one address after which a CAPTCHA is asked (only if a provider is configured).</summary>
    public int FalhasParaCaptchaIp { get; init; } = 10;

    /// <summary>Upper bound of tracked accounts (and of tracked addresses): the memory cannot be exhausted by typing random names.</summary>
    public int MaximoDeRegistros { get; init; } = 100_000;

    /// <summary>Refuses limits that would lock nobody or lock forever (a configuration error stops the host).</summary>
    public void Validar()
    {
        if (FalhasParaBloquearEmail < 1 || FalhasParaBloquearOperacional < 1 || FalhasParaBloquearIp < 1 || ContasDistintasPorIp < 2 || IpsDistintosPorConta < 2 || FalhasParaCaptchaConta < 1 || FalhasParaCaptchaIp < 1
            || BloqueioBase <= TimeSpan.Zero || BloqueioMaximo < BloqueioBase || JanelaDeFalhas <= TimeSpan.Zero || BloqueioDeStuffing <= TimeSpan.Zero || MaximoDeRegistros < 1)
        {
            throw new InvalidOperationException("The login protection limits are not valid.");
        }
    }
}

/// <summary>
/// What is being tried: the kind of login (decides the limit) and the normalized identifier typed (<c>organization:user</c> or the e-mail). It is the TYPED identifier, never
/// the account found, so an unknown name is throttled exactly like a known one and the lock tells nothing about existence.
/// </summary>
public readonly record struct ChaveDeLogin(TipoConta Tipo, string Identificador);

public enum MotivoDeBloqueio
{
    /// <summary>Too many failures of the account.</summary>
    TentativasDaConta,

    /// <summary>Too many failures from the address.</summary>
    TentativasDoIp,

    /// <summary>Credential stuffing: many different accounts failing from one address.</summary>
    ContasDemaisPorIp,

    /// <summary>Credential stuffing: many different addresses failing against one account.</summary>
    IpsDemaisPorConta,
}

public abstract record DecisaoDeEntrada
{
    private protected DecisaoDeEntrada()
    {
    }
}

public sealed record Livre : DecisaoDeEntrada;

/// <summary>Allowed, but the person must pass a CAPTCHA first (adaptive; only asked when a provider exists).</summary>
public sealed record ExigeCaptcha : DecisaoDeEntrada;

public sealed record Bloqueada(DateTimeOffset Ate, MotivoDeBloqueio Motivo) : DecisaoDeEntrada;

/// <summary>A lock that STARTED with this failure (reported once, not on every failure inside it).</summary>
public sealed record BloqueioIniciado(MotivoDeBloqueio Motivo, DateTimeOffset Ate);

public sealed record EfeitoDaFalha(BloqueioIniciado? Conta, BloqueioIniciado? Ip);

/// <summary>
/// Progressive lock by account AND by address, with credential-stuffing detection (spec 5.5.1, 5.5.2). The state is per process (see the in-memory implementation for what that
/// means). Synchronous on purpose: the in-memory state needs no I/O; a shared store would change this port.
/// </summary>
public interface ILimitadorDeLogin
{
    /// <summary>Asked BEFORE any lookup or hashing. A blocked attempt does no work and does not count as another failure.</summary>
    DecisaoDeEntrada Avaliar(ChaveDeLogin chave, string? ip);

    /// <summary>A failure of the password (known or unknown account, wrong or expired credential). <paramref name="ip"/> null: the address is unknown and is not tracked.</summary>
    EfeitoDaFalha RegistrarFalha(ChaveDeLogin chave, string? ip);

    /// <summary>The password was right: the account's failures are cleared. The address's are NOT (a thief with one valid account would reset them).</summary>
    void RegistrarSucesso(ChaveDeLogin chave);
}

/// <summary>
/// CAPTCHA seam (spec 5.5.2): no provider is bound (README premise). While <see cref="Configurado"/> is false the login never asks for a CAPTCHA (it could not be solved); a provider
/// implements this and the adaptive rule starts working with no other change.
/// </summary>
public interface ICaptcha
{
    bool Configurado { get; }

    Task<bool> ValidarAsync(string? resposta, string? ip, CancellationToken cancellationToken);
}

/// <summary>What the login needs to know about the request: the address (already decided by the host: see the trusted proxy option) and the CAPTCHA answer, if the client sent one.</summary>
public sealed record ContextoDeLogin(string? Ip, string? CaptchaResposta)
{
    public static ContextoDeLogin Nenhum { get; } = new(null, null);
}

/// <summary>An e-mail the platform sends (alerts now; invitations and recovery in ticket 09). The body of a message may carry a secret link later: nothing logs it.</summary>
public sealed record MensagemDeEmail(string Para, string Assunto, string Texto)
{
    public override string ToString() => "[email]";
}

/// <summary>E-mail seam (spec 5.2.9): the real provider is an open question (4); development records in memory.</summary>
public interface IEnviadorDeEmail
{
    Task EnviarAsync(MensagemDeEmail mensagem, CancellationToken cancellationToken);
}

/// <summary>The result of recording that an account signed in from this device and this network (spec 5.5.5).</summary>
public sealed record VisitaRegistrada(bool TinhaHistorico, bool AparelhoNovo, bool RedeNova)
{
    /// <summary>The first login of an account only sets the baseline: alerting on it would alert on every account's first access.</summary>
    public bool DeveAlertar => TinhaHistorico && (AparelhoNovo || RedeNova);
}

/// <summary>Devices and networks already seen for an account. Runs INSIDE a unit of work of the organization.</summary>
public interface IAcessosConhecidos
{
    Task<VisitaRegistrada> RegistrarAsync(Guid organizacaoId, Guid contaId, string? aparelho, string? rede, DateTimeOffset agora, CancellationToken cancellationToken);
}
