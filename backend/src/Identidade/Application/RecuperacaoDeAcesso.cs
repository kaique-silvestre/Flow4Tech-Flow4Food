using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// "I forgot my password" for accounts with e-mail (spec 5.2.5, 5.2.8). Properties:
/// <list type="bullet">
/// <item><b>No enumeration.</b> <see cref="SolicitarAsync"/> returns nothing and every outcome (account known, unknown, inactive, operational, malformed address, throttled, mail failed) waits the same response-time floor
/// as the login; the route answers the same 202 for all of them. Known limit: the floor hides the database and mail latency only while that latency stays under the floor.</item>
/// <item><b>Throttled by the login limiter</b> (<see cref="ILimitadorDeLogin"/>): every request counts as an attempt against the typed address (its own key space, so it does not lock the login) and against the client address,
/// with the same progressive lock. A throttled request is silently dropped, so the mailbox of a victim cannot be flooded and the answer is still the same.</item>
/// <item><b>Token</b>: only the SHA-256 is stored, single use, <see cref="OpcoesDeAcesso.ValidadeDaRecuperacao"/> (default 1 hour); a new request supersedes the previous token.</item>
/// <item><b>Redeeming</b> replaces the password, ends EVERY session of the account (credential version + explicit revocation), clears the account's login lock (this is the way out of a lock a stranger caused, ticket 08),
/// audits and raises <c>seguranca.acesso-recuperado</c>, which the alert consumer turns into an e-mail to the person.</item>
/// </list>
/// </summary>
public sealed class RecuperacaoDeAcesso(
    IUnitOfWork unitOfWork,
    IPreAuthLookup lookup,
    IContaStore contas,
    IRecuperacoesStore recuperacoes,
    IRevogacaoDeSessoes sessoes,
    IPasswordHasher hasher,
    ICompromisedPasswordCheck compromised,
    IAuditLog audit,
    IEventosDeSeguranca eventos,
    IEnviadorDeEmail email,
    ILimitadorDeLogin limitador,
    IResponseTimeFloor floor,
    ITechnicalLog log,
    IClock clock,
    OpcoesDeAcesso opcoes)
{
    private const string MotivoDeRevogacao = "recuperacao-de-acesso";

    public async Task SolicitarAsync(string? emailDigitado, ContextoDeLogin? contexto, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await ProcessarAsync(emailDigitado, contexto ?? ContextoDeLogin.Nenhum, cancellationToken);
        await floor.AguardarAsync(started, cancellationToken);
    }

    private async Task ProcessarAsync(string? emailDigitado, ContextoDeLogin contexto, CancellationToken cancellationToken)
    {
        var valido = EmailDeConta.TryNormalize(emailDigitado, out var normalizado);
        var chave = new ChaveDeLogin(TipoConta.Email, "recuperacao:" + (valido ? normalizado : Bruto(emailDigitado)));
        if (limitador.Avaliar(chave, contexto.Ip) is Bloqueada)
        {
            return; // dropped: same outward answer, no lookup, no mail
        }

        // Every request is an "attempt": asking again and again is how a mailbox is flooded, and it is how addresses are probed.
        var efeito = limitador.RegistrarFalha(chave, contexto.Ip);
        if (efeito.Ip is { } porIp)
        {
            log.Write(LogSeverity.Warning, "recuperacao.ip-bloqueado", new Dictionary<string, object?> { ["motivo"] = porIp.Motivo.ToString(), ["ateUtc"] = porIp.Ate, ["ip"] = Resumo(contexto.Ip) });
        }

        if (!valido)
        {
            return;
        }

        var conta = await lookup.LocalizarPorEmailAsync(normalizado, cancellationToken);
        if (conta is not { Ativa: true, Tipo: TipoConta.Email })
        {
            _ = TokenDeUso.Criar(TokenDeUso.Recuperacao, Guid.Empty); // the same cheap work as the known path; the floor does the rest
            return;
        }

        var organizationId = conta.OrganizacaoId;
        var (token, hash) = TokenDeUso.Criar(TokenDeUso.Recuperacao, organizationId);
        var context = OrganizationContext.WithoutUnit(organizationId);
        var agora = clock.UtcNow;
        var id = EntityId.New().Value;
        await unitOfWork.ExecuteAsync(
            context,
            async ct =>
            {
                await recuperacoes.InvalidarPendentesAsync(organizationId, conta.ContaId, agora, ct);
                await recuperacoes.InserirAsync(organizationId, id, conta.ContaId, hash, agora, agora + opcoes.ValidadeDaRecuperacao, ct);
                await audit.RecordAsync(
                    context,
                    new AuditEntry
                    {
                        Action = IdentidadeAuditActions.ContaSolicitarRecuperacao,
                        UserId = conta.ContaId, // the person (or whoever typed their address) asked for themselves
                        ObjectType = "conta",
                        ObjectId = conta.ContaId.ToString("D"),
                    },
                    ct);
            },
            cancellationToken);

        try
        {
            await email.EnviarAsync(
                new MensagemDeEmail(
                    normalizado,
                    "Recuperação de acesso ao Flow4Tech",
                    $"Recebemos um pedido para redefinir a senha da sua conta Flow4Tech. Para escolher uma nova senha, abra o link:\n{opcoes.LinkDeRecuperacao(token)}\n\nO link vale por {Math.Max(1, (int)opcoes.ValidadeDaRecuperacao.TotalMinutes)} minutos e só pode ser usado uma vez. Se não foi você, ignore esta mensagem: sua senha continua a mesma."),
                cancellationToken);
        }
        catch (FalhaNoEnvioDeEmailException)
        {
            log.Write(LogSeverity.Warning, "recuperacao.email-falhou", new Dictionary<string, object?> { ["referencia"] = id });
        }
    }

    /// <summary>Redeems the token with the new password. The token is judged first, the password second (a weak password does not burn the token), and the consumption is one atomic step.</summary>
    public async Task<ResultadoRecuperacao> RedefinirAsync(string? token, string novaSenha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(novaSenha);
        if (!TokenDeUso.TentarLer(token, TokenDeUso.Recuperacao, out var organizationId, out var tokenHash))
        {
            return Falha(StatusRecuperacao.TokenInvalido);
        }

        var context = OrganizationContext.WithoutUnit(organizationId);
        var (recuperacao, conta) = await unitOfWork.ExecuteAsync<(RecuperacaoEncontrada? Recuperacao, Conta? Conta)>(
            context,
            async ct =>
            {
                var r = await recuperacoes.ObterPorHashAsync(organizationId, tokenHash, ct);
                if (r is not { UsadoEm: null } || r.ExpiraEm <= clock.UtcNow)
                {
                    return (null, null);
                }

                var c = await contas.ObterAsync(organizationId, r.ContaId, ct);
                if (c is not { Ativa: true, Tipo: TipoConta.Email, Email: not null })
                {
                    return (null, null);
                }

                return (r, c);
            },
            cancellationToken);
        if (recuperacao is null || conta is null)
        {
            return Falha(StatusRecuperacao.TokenInvalido);
        }

        if (novaSenha.Length > PoliticaDeSenha.LimiteDeEntrada)
        {
            return new ResultadoRecuperacao(StatusRecuperacao.SenhaInvalida, [ProblemaDeSenha.Longa]); // before NFKC and hashing
        }

        var problemas = PoliticaDeSenha.Avaliar(novaSenha, conta.Email);
        if (problemas.Count > 0)
        {
            return new ResultadoRecuperacao(StatusRecuperacao.SenhaInvalida, problemas);
        }

        var normalizada = PoliticaDeSenha.Normalizar(novaSenha);
        try
        {
            if (await compromised.IsCompromisedAsync(normalizada, cancellationToken))
            {
                return Falha(StatusRecuperacao.SenhaVazada);
            }
        }
        catch (VerificacaoDeSenhaIndisponivelException)
        {
            return Falha(StatusRecuperacao.VerificacaoIndisponivel);
        }

        var novoHash = await hasher.HashAsync(normalizada, cancellationToken);
        var feito = await unitOfWork.ExecuteAsync(
            context,
            async ct =>
            {
                var agora = clock.UtcNow;
                // Atomic gate first: of two simultaneous redemptions exactly one passes, and a failed gate wrote nothing. Then the account is checked again (it may have been deactivated meanwhile).
                if (!await recuperacoes.ConsumirAsync(organizationId, recuperacao.Id, agora, ct) || (await contas.ObterAsync(organizationId, conta.Id, ct)) is not { Ativa: true })
                {
                    return false;
                }

                await recuperacoes.InvalidarPendentesAsync(organizationId, conta.Id, agora, ct);
                await contas.TrocarSenhaAsync(organizationId, conta.Id, novoHash, ct); // clears any temporary credential and bumps the credential version
                await sessoes.RevogarTodasDaContaAsync(organizationId, conta.Id, MotivoDeRevogacao, agora, ct);
                await audit.RecordAsync(
                    context,
                    new AuditEntry
                    {
                        Action = IdentidadeAuditActions.ContaRecuperarAcesso,
                        UserId = conta.Id,
                        ObjectType = "conta",
                        ObjectId = conta.Id.ToString("D"),
                        After = new JsonObject { ["via"] = "email" },
                    },
                    ct);
                await eventos.PublicarAsync(context, new AcessoRecuperado(EntityId.New().Value, organizationId, conta.Id, agora), ct);
                return true;
            },
            cancellationToken);
        if (!feito)
        {
            return Falha(StatusRecuperacao.TokenInvalido);
        }

        // The person proved the mailbox: a lock that a stranger's guesses put on the login of this address ends here (ticket 08).
        limitador.RegistrarSucesso(new ChaveDeLogin(TipoConta.Email, conta.Email!));
        return new ResultadoRecuperacao(StatusRecuperacao.Redefinida, []);
    }

    private static ResultadoRecuperacao Falha(StatusRecuperacao status) => new(status, []);

    private static string Bruto(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim().ToUpperInvariant();
        return "?" + (trimmed.Length <= 80 ? trimmed : trimmed[..80]);
    }

    private static string? Resumo(string? ip) => ip is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip)))[..8];
}
