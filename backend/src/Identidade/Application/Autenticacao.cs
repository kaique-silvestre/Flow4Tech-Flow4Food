using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// Validates a credential (spec 5.2.1, 5.2.4, 5.2.8). It issues NO token and starts NO session (ticket 04) and has no HTTP route yet. Properties:
/// <list type="bullet">
/// <item>One failure for everything: unknown organization, user or e-mail, wrong password, deactivated account.</item>
/// <item>The same hashing work on every path: with no account, the password is verified against a fake hash (<see cref="IPasswordHasher.VerifyDummyAsync"/>).</item>
/// <item>Every outcome waits for the same response-time floor (<see cref="IResponseTimeFloor"/>), which also hides the cost of the audit write of a known account.</item>
/// <item>A right TEMPORARY credential yields <see cref="DeveTrocarSenha"/>: nothing but <see cref="TrocaDeSenha"/> is allowed. Past its deadline (<c>temporaria_expira_em</c>) it is the generic failure; the reset that brings the account back is ticket 09.</item>
/// <item>Failures of KNOWN accounts are audited (<c>autenticacao:falhar</c>, <c>negado</c>, no secret); an unknown organization has no audit chain to write to.</item>
/// </list>
/// Login protection (ticket 08, spec 5.5): BEFORE any lookup or hashing the attempt is judged by <see cref="ILimitadorDeLogin"/> (progressive lock by account and by address,
/// credential stuffing, adaptive CAPTCHA); a blocked attempt does no work at all. The key is what was TYPED, so unknown names are throttled like known ones. A lock of a known
/// account is audited (<c>autenticacao:bloquear</c>) and raises the outbox event <c>seguranca.conta-bloqueada</c>; a lock of an address has no organization and goes to the technical log.
/// An exception from the audit store of a known account would surface as an error; accepted, it is an outage.
/// </summary>
public sealed class Autenticacao(
    IPreAuthLookup lookup,
    IPasswordHasher hasher,
    IUnitOfWork unitOfWork,
    IContaStore contas,
    IAuditLog audit,
    IResponseTimeFloor floor,
    IClock clock,
    ILimitadorDeLogin limitador,
    ICaptcha captcha,
    IEventosDeSeguranca eventos,
    ITechnicalLog log)
{
    public Task<ResultadoAutenticacao> AutenticarOperacionalAsync(Guid organizationId, string usuario, string senha, CancellationToken cancellationToken = default) =>
        AutenticarOperacionalAsync(organizationId, usuario, senha, ContextoDeLogin.Nenhum, cancellationToken);

    public Task<ResultadoAutenticacao> AutenticarOperacionalAsync(Guid organizationId, string usuario, string senha, ContextoDeLogin contexto, CancellationToken cancellationToken = default)
    {
        var valido = NomeDeUsuario.TryNormalize(usuario, out var normalizado);
        return AutenticarAsync(
            new ChaveDeLogin(TipoConta.Operacional, $"{organizationId:N}:{(valido ? normalizado : Bruto(usuario))}"),
            senha,
            contexto,
            ct => valido ? lookup.LocalizarOperacionalAsync(organizationId, normalizado, ct) : Task.FromResult<ContaParaLogin?>(null),
            cancellationToken);
    }

    public Task<ResultadoAutenticacao> AutenticarPorEmailAsync(string email, string senha, CancellationToken cancellationToken = default) =>
        AutenticarPorEmailAsync(email, senha, ContextoDeLogin.Nenhum, cancellationToken);

    public Task<ResultadoAutenticacao> AutenticarPorEmailAsync(string email, string senha, ContextoDeLogin contexto, CancellationToken cancellationToken = default)
    {
        var valido = EmailDeConta.TryNormalize(email, out var normalizado);
        return AutenticarAsync(
            new ChaveDeLogin(TipoConta.Email, valido ? normalizado : Bruto(email)),
            senha,
            contexto,
            ct => valido ? lookup.LocalizarPorEmailAsync(normalizado, ct) : Task.FromResult<ContaParaLogin?>(null),
            cancellationToken);
    }

    /// <summary>An identifier that does not even parse still has a key (trimmed, lower case, bounded), so spraying malformed names is throttled too.</summary>
    private static string Bruto(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim().ToUpperInvariant();
        return "?" + (trimmed.Length <= 80 ? trimmed : trimmed[..80]);
    }

    private async Task<ResultadoAutenticacao> AutenticarAsync(ChaveDeLogin chave, string? senha, ContextoDeLogin? contexto, Func<CancellationToken, Task<ContaParaLogin?>> find, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var ctx = contexto ?? ContextoDeLogin.Nenhum;
        var result = await ProtegeAsync(chave, senha, ctx, find, cancellationToken);
        await floor.AguardarAsync(started, cancellationToken);
        return result;
    }

    private async Task<ResultadoAutenticacao> ProtegeAsync(ChaveDeLogin chave, string? senha, ContextoDeLogin ctx, Func<CancellationToken, Task<ContaParaLogin?>> find, CancellationToken cancellationToken)
    {
        switch (limitador.Avaliar(chave, ctx.Ip))
        {
            case Bloqueada bloqueada:
                return new LoginBloqueado(bloqueada.Ate); // no lookup, no hashing, no audit: a locked attempt must be cheap (and does not count again)
            case ExigeCaptcha when captcha.Configurado && !await captcha.ValidarAsync(ctx.CaptchaResposta, ctx.Ip, cancellationToken):
                return new CaptchaExigido();
        }

        var (result, conta) = await DecideAsync(senha, find, cancellationToken);
        if (result is FalhaDeAutenticacao)
        {
            await RegistrarFalhaAsync(chave, ctx.Ip, conta, cancellationToken);
        }
        else
        {
            limitador.RegistrarSucesso(chave);
        }

        return result;
    }

    /// <summary>Counts the failure and, when it STARTED a lock, leaves the trace: audit + outbox event for a known account, technical log for an address.</summary>
    private async Task RegistrarFalhaAsync(ChaveDeLogin chave, string? ip, ContaParaLogin? conta, CancellationToken cancellationToken)
    {
        var efeito = limitador.RegistrarFalha(chave, ip);
        if (efeito.Ip is { } porIp)
        {
            // An address belongs to no organization: there is no audit chain or outbox to write to. The address itself is not logged, only a short digest to tell two apart.
            log.Write(
                LogSeverity.Warning,
                "login.ip-bloqueado",
                new Dictionary<string, object?> { ["motivo"] = porIp.Motivo.ToString(), ["ateUtc"] = porIp.Ate, ["ip"] = Resumo(ip) });
        }

        if (efeito.Conta is { } bloqueio && conta is not null)
        {
            var contexto = OrganizationContext.WithoutUnit(conta.OrganizacaoId);
            await unitOfWork.ExecuteAsync(
                contexto,
                async ct =>
                {
                    await audit.RecordAsync(
                        contexto,
                        new AuditEntry
                        {
                            Action = IdentidadeAuditActions.AutenticacaoBloquear,
                            Result = AuditResult.Negado,
                            ObjectType = "conta",
                            ObjectId = conta.ContaId.ToString("D"),
                            After = new JsonObject { ["motivo"] = bloqueio.Motivo.ToString(), ["ateUtc"] = bloqueio.Ate.ToString("O", CultureInfo.InvariantCulture) },
                        },
                        ct);
                    await eventos.PublicarAsync(contexto, new ContaBloqueada(EntityId.New().Value, conta.OrganizacaoId, conta.ContaId, bloqueio.Motivo.ToString(), bloqueio.Ate, clock.UtcNow), ct);
                },
                cancellationToken);
        }
    }

    private static string? Resumo(string? ip) => ip is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip)))[..8];

    private async Task<(ResultadoAutenticacao Result, ContaParaLogin? Conta)> DecideAsync(string? senha, Func<CancellationToken, Task<ContaParaLogin?>> find, CancellationToken cancellationToken)
    {
        if (senha is null || senha.Length > PoliticaDeSenha.LimiteDeEntrada)
        {
            return (new FalhaDeAutenticacao(), null); // absurd input: refused before any lookup or hashing, the same for known and unknown accounts
        }

        var conta = await find(cancellationToken);
        var password = PoliticaDeSenha.Normalizar(senha);
        if (conta is null)
        {
            await hasher.VerifyDummyAsync(password, cancellationToken);
            return (new FalhaDeAutenticacao(), null);
        }

        var correct = await hasher.VerifyAsync(password, conta.SenhaHash, cancellationToken);
        // A temporary credential past its deadline is refused like a wrong password (after the same hashing work). It never works again by itself: only a new
        // reset (ticket 09) sets a new deadline. Rows without a deadline (accounts from before migration 0008) do not expire.
        var expired = conta.DeveTrocarSenha && conta.TemporariaExpiraEm is { } deadline && deadline <= clock.UtcNow;
        if (!correct || !conta.Ativa || expired)
        {
            await audit.RecordDetachedAsync(
                OrganizationContext.WithoutUnit(conta.OrganizacaoId),
                new AuditEntry
                {
                    Action = IdentidadeAuditActions.AutenticacaoFalhar,
                    Result = AuditResult.Negado,
                    ObjectType = "conta",
                    ObjectId = conta.ContaId.ToString("D"),
                    After = new JsonObject { ["motivo"] = !correct ? "senha-invalida" : !conta.Ativa ? "conta-desativada" : "credencial-temporaria-vencida" },
                },
                cancellationToken);
            return (new FalhaDeAutenticacao(), conta);
        }

        if (hasher.NeedsRehash(conta.SenhaHash))
        {
            var novoHash = await hasher.HashAsync(password, cancellationToken);
            await unitOfWork.ExecuteAsync(
                OrganizationContext.WithoutUnit(conta.OrganizacaoId),
                ct => contas.RehashAsync(conta.OrganizacaoId, conta.ContaId, conta.SenhaHash, novoHash, ct),
                cancellationToken);
        }

        return (conta.DeveTrocarSenha
            ? new DeveTrocarSenha(conta.ContaId, conta.OrganizacaoId, conta.Tipo, conta.VersaoCredencial)
            : new Autenticado(conta.ContaId, conta.OrganizacaoId, conta.Tipo, conta.VersaoCredencial), conta);
    }
}
