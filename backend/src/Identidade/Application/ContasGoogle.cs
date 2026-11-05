using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>What an OpenID Connect provider says about the person AFTER the id token was fully validated (signature, algorithm, issuer, audience, lifetime, nonce).</summary>
public sealed record IdentidadeOidc(string Sub, string? Email, bool EmailVerificado)
{
    public override string ToString() => "[identidade oidc]";
}

/// <summary>
/// OpenID Connect seam (spec 5.2.3). The only implementation that talks to the network is <c>ProvedorOidcGoogle</c>; tests use a fake provider. <see cref="TrocarCodigoAsync"/> returns null for ANYTHING wrong
/// (bad code, bad signature, wrong issuer/audience, expired, wrong nonce, provider down): the reason is never exposed.
/// </summary>
public interface IProvedorOidc
{
    bool Configurado { get; }

    Uri UrlDeAutorizacao(string state, string nonce, string codeChallenge);

    Task<IdentidadeOidc?> TrocarCodigoAsync(string code, string codeVerifier, string nonceEsperado, CancellationToken cancellationToken);
}

/// <summary>
/// The state of ONE Google round trip, kept in a signed cookie (<see cref="ISeloDeTransacao"/>) so no server-side table is needed before there is a session. <see cref="Proposito"/> is <c>entrar</c> or
/// <c>vincular</c>; a linking carries the account and the credential version at the moment the signed-in person confirmed their password.
/// </summary>
public sealed record TransacaoOidc(string Proposito, string State, string Nonce, string Verifier, DateTimeOffset ExpiraEm, Guid? OrganizacaoId, Guid? ContaId, int? VersaoCredencial)
{
    public override string ToString() => "[transacao oidc]";
}

/// <summary>Signs (HMAC) and opens <see cref="TransacaoOidc"/>. <see cref="Abrir"/> is null for anything not made by this key, tampered, malformed or expired.</summary>
public interface ISeloDeTransacao
{
    string Selar(TransacaoOidc transacao);

    TransacaoOidc? Abrir(string? selo);
}

public sealed record InicioGoogle(Uri Url, string Selo);

/// <summary>Links between an account and an external identity of the current organization. Every member runs INSIDE a unit of work.</summary>
public interface IIdentidadesExternasStore
{
    Task<string?> ObterSubAsync(Guid organizacaoId, Guid contaId, string provedor, CancellationToken cancellationToken);

    /// <summary>False (nothing written) when the account already has a link for the provider or the provider identity is linked to another account.</summary>
    Task<bool> InserirAsync(Guid organizacaoId, Guid contaId, string provedor, string subject, string email, DateTimeOffset agora, CancellationToken cancellationToken);

    Task<bool> RemoverAsync(Guid organizacaoId, Guid contaId, string provedor, CancellationToken cancellationToken);
}

public enum StatusInicioVinculo
{
    Iniciado,
    SenhaInvalida,

    /// <summary>Operational accounts do not use Google (spec question 6), inactive accounts neither.</summary>
    ContaNaoElegivel,
    NaoConfigurado,
    Bloqueado,
}

public sealed record ResultadoInicioVinculo(StatusInicioVinculo Status, InicioGoogle? Inicio);

/// <summary>Outcome of the Google callback. Closed hierarchy; <see cref="GoogleNeutro"/> is the ONE answer for everything that is not a success, so it tells nothing about accounts.</summary>
public abstract record ResultadoGoogle
{
    private protected ResultadoGoogle()
    {
    }
}

public sealed record GoogleEntrou(Autenticado Conta) : ResultadoGoogle;

public sealed record GoogleVinculado : ResultadoGoogle;

public sealed record GoogleNeutro : ResultadoGoogle;

public sealed record GoogleBloqueado(DateTimeOffset Ate) : ResultadoGoogle;

/// <summary>
/// Google sign-in and linking (spec 5.2.3, ticket 09). Rules, each with a test:
/// <list type="bullet">
/// <item>Only a VERIFIED e-mail counts. The provider's claim is never trusted blindly: the id token is validated (<c>ProvedorOidcGoogle</c>), then <c>email_verified</c> must be true.</item>
/// <item>It NEVER creates an account and never a link by itself. An account exists only through an invitation; a link exists only after the signed-in person confirmed it with their password AND the verified Google
/// e-mail equals the account's e-mail. A Google account with no link gets the neutral answer (<see cref="GoogleNeutro"/>), the same as an unknown e-mail.</item>
/// <item>Signing in needs the link to match the Google <c>sub</c> (the stable identity), not just the e-mail.</item>
/// <item>Operational accounts do not use Google (no e-mail; also refused in the database by the composite foreign key on the account type).</item>
/// <item>CSRF / code injection: the round trip carries <c>state</c> (compared with the signed cookie), <c>nonce</c> (compared with the id token) and PKCE (<c>code_verifier</c> goes only to the token endpoint).</item>
/// <item>Failures count in the login limiter under the e-mail key (shared with the password login), and every outcome waits the response-time floor.</item>
/// </list>
/// </summary>
public sealed class ContasGoogle(
    IProvedorOidc provedor,
    ISeloDeTransacao selo,
    IPreAuthLookup lookup,
    IContaStore contas,
    IIdentidadesExternasStore vinculos,
    IPasswordHasher hasher,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ILimitadorDeLogin limitador,
    IResponseTimeFloor floor,
    IClock clock,
    ITechnicalLog log)
{
    private const string Google = "google";
    private const string PropositoEntrar = "entrar";
    private const string PropositoVincular = "vincular";
    private static readonly TimeSpan ValidadeDaTransacao = TimeSpan.FromMinutes(10);

    public bool Configurado => provedor.Configurado;

    /// <summary>Starts a sign-in. Null when no provider is configured.</summary>
    public InicioGoogle? IniciarEntrada() => provedor.Configurado ? Iniciar(PropositoEntrar, null, null, null) : null;

    /// <summary>
    /// A signed-in person asks to link their Google account. Confirmation: the CURRENT password (a stolen session alone cannot link an attacker's Google account). Wrong passwords count in the login limiter under
    /// the account's key.
    /// </summary>
    public async Task<ResultadoInicioVinculo> IniciarVinculoAsync(IRequestContext request, string senhaAtual, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(senhaAtual);
        if (!provedor.Configurado)
        {
            return new ResultadoInicioVinculo(StatusInicioVinculo.NaoConfigurado, null);
        }

        var organizationId = request.Organization.OrganizationId;
        var (conta, credencial) = await unitOfWork.ExecuteAsync(
            request,
            async ct => (await contas.ObterAsync(organizationId, request.UserId, ct), await contas.ObterComCredencialAsync(organizationId, request.UserId, ct)),
            cancellationToken);
        if (conta is not { Ativa: true, Tipo: TipoConta.Email, Email: not null } || credencial is null)
        {
            return new ResultadoInicioVinculo(StatusInicioVinculo.ContaNaoElegivel, null);
        }

        var chave = new ChaveDeLogin(TipoConta.Email, conta.Email);
        if (limitador.Avaliar(chave, null) is Bloqueada)
        {
            return new ResultadoInicioVinculo(StatusInicioVinculo.Bloqueado, null);
        }

        if (senhaAtual.Length > PoliticaDeSenha.LimiteDeEntrada || !await hasher.VerifyAsync(PoliticaDeSenha.Normalizar(senhaAtual), credencial.SenhaHash, cancellationToken))
        {
            limitador.RegistrarFalha(chave, null);
            return new ResultadoInicioVinculo(StatusInicioVinculo.SenhaInvalida, null);
        }

        limitador.RegistrarSucesso(chave);
        return new ResultadoInicioVinculo(StatusInicioVinculo.Iniciado, Iniciar(PropositoVincular, organizationId, conta.Id, conta.VersaoCredencial));
    }

    /// <summary>Unlinks the Google account of the caller's own account.</summary>
    public Task<bool> DesvincularAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organizationId = request.Organization.OrganizationId;
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await vinculos.RemoverAsync(organizationId, request.UserId, Google, ct))
                {
                    return false;
                }

                await audit.RecordAsync(request, new AuditEntry { Action = IdentidadeAuditActions.ContaDesvincularGoogle, ObjectType = "conta", ObjectId = request.UserId.ToString("D"), After = new JsonObject { ["provedor"] = Google } }, ct);
                return true;
            },
            cancellationToken);
    }

    /// <summary>The callback of both round trips. Which one it is comes from the SIGNED cookie, never from the query.</summary>
    public async Task<ResultadoGoogle> ConcluirAsync(string? seloDaTransacao, string? state, string? code, ContextoDeLogin? contexto, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await ProcessarAsync(seloDaTransacao, state, code, contexto ?? ContextoDeLogin.Nenhum, cancellationToken);
        await floor.AguardarAsync(started, cancellationToken);
        return result;
    }

    private async Task<ResultadoGoogle> ProcessarAsync(string? seloDaTransacao, string? state, string? code, ContextoDeLogin contexto, CancellationToken cancellationToken)
    {
        var neutro = new GoogleNeutro();
        var transacao = selo.Abrir(seloDaTransacao);
        if (transacao is null || transacao.ExpiraEm <= clock.UtcNow || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code) || code.Length > 2048 || !IguaisEmTempoConstante(state, transacao.State))
        {
            return neutro;
        }

        var identidade = await provedor.TrocarCodigoAsync(code, transacao.Verifier, transacao.Nonce, cancellationToken);
        if (identidade is not { EmailVerificado: true, Email: not null } || !EmailDeConta.TryNormalize(identidade.Email, out var email))
        {
            return neutro;
        }

        return transacao.Proposito == PropositoVincular
            ? await VincularAsync(transacao, identidade, email, cancellationToken)
            : await EntrarAsync(identidade, email, contexto, cancellationToken);
    }

    private async Task<ResultadoGoogle> EntrarAsync(IdentidadeOidc identidade, string email, ContextoDeLogin contexto, CancellationToken cancellationToken)
    {
        var chave = new ChaveDeLogin(TipoConta.Email, email);
        if (limitador.Avaliar(chave, contexto.Ip) is Bloqueada bloqueada)
        {
            return new GoogleBloqueado(bloqueada.Ate);
        }

        var conta = await lookup.LocalizarPorEmailAsync(email, cancellationToken);
        if (conta is not { Ativa: true, Tipo: TipoConta.Email, DeveTrocarSenha: false })
        {
            limitador.RegistrarFalha(chave, contexto.Ip);
            return new GoogleNeutro();
        }

        var context = OrganizationContext.WithoutUnit(conta.OrganizacaoId);
        var vinculado = await unitOfWork.ExecuteAsync(context, ct => vinculos.ObterSubAsync(conta.OrganizacaoId, conta.ContaId, Google, ct), cancellationToken);
        if (vinculado is null || !IguaisEmTempoConstante(vinculado, identidade.Sub))
        {
            limitador.RegistrarFalha(chave, contexto.Ip);
            await audit.RecordDetachedAsync(
                context,
                new AuditEntry
                {
                    Action = IdentidadeAuditActions.AutenticacaoFalhar,
                    Result = AuditResult.Negado,
                    ObjectType = "conta",
                    ObjectId = conta.ContaId.ToString("D"),
                    After = new JsonObject { ["motivo"] = vinculado is null ? "google-sem-vinculo" : "google-identidade-diferente" },
                },
                cancellationToken);
            return new GoogleNeutro();
        }

        limitador.RegistrarSucesso(chave);
        return new GoogleEntrou(new Autenticado(conta.ContaId, conta.OrganizacaoId, conta.Tipo, conta.VersaoCredencial));
    }

    private async Task<ResultadoGoogle> VincularAsync(TransacaoOidc transacao, IdentidadeOidc identidade, string email, CancellationToken cancellationToken)
    {
        if (transacao.OrganizacaoId is not { } organizationId || transacao.ContaId is not { } contaId)
        {
            return new GoogleNeutro();
        }

        var context = OrganizationContext.WithoutUnit(organizationId);
        var vinculou = await unitOfWork.ExecuteAsync(
            context,
            async ct =>
            {
                var conta = await contas.ObterAsync(organizationId, contaId, ct);
                // The account must be the same one, still active, with the same e-mail and the SAME credential version the person confirmed: a password change in between cancels the linking.
                if (conta is not { Ativa: true, Tipo: TipoConta.Email, Email: not null } || conta.Email != email || conta.VersaoCredencial != transacao.VersaoCredencial)
                {
                    return false;
                }

                if (!await vinculos.InserirAsync(organizationId, contaId, Google, identidade.Sub, email, clock.UtcNow, ct))
                {
                    return false;
                }

                await audit.RecordAsync(context, new AuditEntry { Action = IdentidadeAuditActions.ContaVincularGoogle, UserId = contaId, ObjectType = "conta", ObjectId = contaId.ToString("D"), After = new JsonObject { ["provedor"] = Google } }, ct);
                return true;
            },
            cancellationToken);
        if (!vinculou)
        {
            log.Write(LogSeverity.Warning, "google.vinculo-recusado", new Dictionary<string, object?> { ["contaId"] = contaId });
        }

        return vinculou ? new GoogleVinculado() : new GoogleNeutro();
    }

    private InicioGoogle Iniciar(string proposito, Guid? organizationId, Guid? contaId, int? versaoCredencial)
    {
        var state = TokenDeUso.Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = TokenDeUso.Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = TokenDeUso.Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = TokenDeUso.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var transacao = new TransacaoOidc(proposito, state, nonce, verifier, clock.UtcNow + ValidadeDaTransacao, organizationId, contaId, versaoCredencial);
        return new InicioGoogle(provedor.UrlDeAutorizacao(state, nonce, challenge), selo.Selar(transacao));
    }

    private static bool IguaisEmTempoConstante(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
