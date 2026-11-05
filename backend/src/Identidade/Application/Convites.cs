using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// Invitations to accounts with e-mail (spec 5.2.1, 5.2.6). A person with the <c>usuario:convidar</c> permission (decided by the route) invites an e-mail address; the person receives a link with a
/// single-use token, chooses their OWN password and the account is created then. Properties:
/// <list type="bullet">
/// <item>Only the SHA-256 of the token is stored; the token exists in the e-mail and nowhere else (not in the audit, the log or the outbox).</item>
/// <item>Single use and expiry (<see cref="OpcoesDeAcesso.ValidadeDoConvite"/>): an expired, revoked, used, unknown or malformed token gets the SAME answer.</item>
/// <item>The address is reserved on the platform only when the invitation is ACCEPTED: the link reached that mailbox, which is the proof of possession ticket 03 asked for.</item>
/// <item>A new invitation to the same address revokes the old one. The inviter learns only whether the address already has an account in THEIR organization; another organization's accounts are invisible (RLS).</item>
/// <item>The profile and the initial unit are stored and travel in the outbox event <c>identidade.convite-aceito</c>; applying them is the job of whoever owns profiles and units, with the inviter's rights (the anti-escalation guard lives there).</item>
/// </list>
/// </summary>
public sealed class Convites(
    IUnitOfWork unitOfWork,
    IConvitesStore convites,
    IContaStore contas,
    IPasswordHasher hasher,
    ICompromisedPasswordCheck compromised,
    IAuditLog audit,
    IEventosDeSeguranca eventos,
    IEnviadorDeEmail email,
    ITechnicalLog log,
    IClock clock,
    OpcoesDeAcesso opcoes)
{
    private const int MaximoNome = 200;

    public async Task<ResultadoConvite> ConvidarAsync(IRequestContext request, string emailDoConvidado, string nome, Guid? perfilId, Guid? unidadeInicialId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EmailDeConta.TryNormalize(emailDoConvidado, out var normalizado))
        {
            return new ResultadoConvite(StatusConvite.EmailInvalido, null, null, false);
        }

        if (string.IsNullOrWhiteSpace(nome) || nome.Trim().Length > MaximoNome)
        {
            return new ResultadoConvite(StatusConvite.NomeInvalido, null, null, false);
        }

        var organizationId = request.Organization.OrganizationId;
        var (token, hash) = TokenDeUso.Criar(TokenDeUso.Convite, organizationId);
        var id = EntityId.New().Value;
        var agora = clock.UtcNow;
        var expiraEm = agora + opcoes.ValidadeDoConvite;
        StatusConvite status;
        try
        {
            status = await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    // An actor who must still change a temporary password, or who was deactivated meanwhile, invites nobody, whatever the session or the route let through.
                    if ((await contas.ObterComCredencialAsync(organizationId, request.UserId, ct)) is not { Ativa: true, DeveTrocarSenha: false })
                    {
                        return StatusConvite.Negado;
                    }

                    if (await convites.EmailJaEhMembroAsync(organizationId, normalizado, ct))
                    {
                        return StatusConvite.JaEhMembro;
                    }

                    await convites.RevogarPendentesDoEmailAsync(organizationId, normalizado, agora, ct);
                    await convites.InserirAsync(organizationId, new NovoConvite(id, normalizado, nome.Trim(), perfilId, unidadeInicialId, hash, request.UserId, agora, expiraEm), ct);
                    await audit.RecordAsync(
                        request,
                        new AuditEntry
                        {
                            Action = IdentidadeAuditActions.ConviteCriar,
                            ObjectType = "convite",
                            ObjectId = id.ToString("D"),
                            After = new JsonObject { ["email"] = normalizado, ["comPerfil"] = perfilId is not null, ["comUnidade"] = unidadeInicialId is not null },
                        },
                        ct);
                    return StatusConvite.Criado;
                },
                cancellationToken);
        }
        catch (ReferenciaInvalidaException)
        {
            return new ResultadoConvite(StatusConvite.PerfilOuUnidadeInvalidos, null, null, false);
        }

        if (status == StatusConvite.Negado)
        {
            await audit.RecordDetachedAsync(request, new AuditEntry { Action = IdentidadeAuditActions.ConviteCriar, Result = AuditResult.Negado, ObjectType = "convite", After = new JsonObject { ["motivo"] = "ator-nao-pode-agir" } }, cancellationToken);
        }

        if (status != StatusConvite.Criado)
        {
            return new ResultadoConvite(status, null, null, false);
        }

        // Sent AFTER the commit and outside the database: the token cannot go through the outbox (it would sit in a table in clear). A failure leaves the invitation in place; the inviter invites again.
        var enviado = await EnviarAsync(
            new MensagemDeEmail(
                normalizado,
                "Você foi convidado para o Flow4Tech",
                $"Você foi convidado(a) para usar o Flow4Tech. Para criar sua senha e entrar, abra o link:\n{opcoes.LinkDeConvite(token)}\n\nO convite vale até {expiraEm.UtcDateTime:dd/MM/yyyy HH:mm} UTC e só pode ser usado uma vez. Se você não esperava este convite, ignore esta mensagem."),
            "convite.email-falhou",
            id,
            cancellationToken);
        return new ResultadoConvite(StatusConvite.Criado, id, expiraEm, enviado);
    }

    /// <summary>Revokes a pending invitation of the caller's organization. False when it is not pending.</summary>
    public async Task<bool> RevogarAsync(IRequestContext request, Guid conviteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organizationId = request.Organization.OrganizationId;
        return await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if ((await contas.ObterComCredencialAsync(organizationId, request.UserId, ct)) is not { Ativa: true, DeveTrocarSenha: false } || !await convites.RevogarAsync(organizationId, conviteId, clock.UtcNow, ct))
                {
                    return false;
                }

                await audit.RecordAsync(request, new AuditEntry { Action = IdentidadeAuditActions.ConviteRevogar, ObjectType = "convite", ObjectId = conviteId.ToString("D") }, ct);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// The invited person accepts: the account is created with the password THEY chose. The token is checked, the password is judged, and only then the token is consumed and the account written in ONE unit of
    /// work (so a weak password does not burn the token, and of two simultaneous acceptances exactly one wins).
    /// </summary>
    public async Task<ResultadoAceite> AceitarAsync(string? token, string senha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(senha);
        if (!TokenDeUso.TentarLer(token, TokenDeUso.Convite, out var organizationId, out var tokenHash))
        {
            return Invalido(StatusAceite.TokenInvalido);
        }

        var context = OrganizationContext.WithoutUnit(organizationId);
        var convite = await unitOfWork.ExecuteAsync(context, ct => convites.ObterPorHashAsync(organizationId, tokenHash, ct), cancellationToken);
        if (!Vivo(convite))
        {
            return Invalido(StatusAceite.TokenInvalido);
        }

        if (senha.Length > PoliticaDeSenha.LimiteDeEntrada)
        {
            return new ResultadoAceite(StatusAceite.SenhaInvalida, null, [ProblemaDeSenha.Longa]); // before NFKC and hashing
        }

        var problemas = PoliticaDeSenha.Avaliar(senha, convite!.Email);
        if (problemas.Count > 0)
        {
            return new ResultadoAceite(StatusAceite.SenhaInvalida, null, problemas);
        }

        var normalizada = PoliticaDeSenha.Normalizar(senha);
        try
        {
            if (await compromised.IsCompromisedAsync(normalizada, cancellationToken))
            {
                return Invalido(StatusAceite.SenhaVazada);
            }
        }
        catch (VerificacaoDeSenhaIndisponivelException)
        {
            return Invalido(StatusAceite.VerificacaoIndisponivel);
        }

        var senhaHash = await hasher.HashAsync(normalizada, cancellationToken);
        var contaId = EntityId.New().Value;
        try
        {
            await unitOfWork.ExecuteAsync(
                context,
                async ct =>
                {
                    var agora = clock.UtcNow;
                    if (!await contas.InserirAsync(organizationId, new NovaConta(contaId, TipoConta.Email, convite.Nome, convite.Email, null, false, agora), senhaHash, false, null, ct))
                    {
                        // Either the winner of a simultaneous acceptance (the token is then spent) or an address that became a member meanwhile.
                        if ((await convites.ObterPorHashAsync(organizationId, tokenHash, ct)) is { UsadoEm: not null })
                        {
                            throw new TokenNaoUtilizavelException();
                        }

                        throw new ContaIndisponivelException();
                    }

                    if (!await contas.RegistrarEmailAsync(organizationId, contaId, convite.Email, ct))
                    {
                        throw new ContaIndisponivelException(); // taken by another organization: a return value would not roll back the rows already written
                    }

                    if (!await convites.MarcarUsadoAsync(organizationId, convite.Id, contaId, agora, ct))
                    {
                        throw new TokenNaoUtilizavelException();
                    }

                    await audit.RecordAsync(
                        context,
                        new AuditEntry
                        {
                            Action = IdentidadeAuditActions.ConviteAceitar,
                            UserId = contaId, // the new person acts for themselves
                            ObjectType = "convite",
                            ObjectId = convite.Id.ToString("D"),
                            After = new JsonObject { ["conta"] = contaId.ToString("D"), ["email"] = convite.Email },
                        },
                        ct);
                    await eventos.PublicarAsync(context, new ConviteAceito(EntityId.New().Value, organizationId, contaId, convite.Id, convite.PerfilId, convite.UnidadeId, convite.CriadoPor, agora), ct);
                },
                cancellationToken);
        }
        catch (TokenNaoUtilizavelException)
        {
            return Invalido(StatusAceite.TokenInvalido);
        }
        catch (ContaIndisponivelException)
        {
            return Invalido(StatusAceite.EmailIndisponivel);
        }

        return new ResultadoAceite(StatusAceite.Aceito, contaId, []);
    }

    private bool Vivo(ConviteEncontrado? convite) => convite is { UsadoEm: null, RevogadoEm: null } && convite.ExpiraEm > clock.UtcNow;

    private static ResultadoAceite Invalido(StatusAceite status) => new(status, null, []);

    private async Task<bool> EnviarAsync(MensagemDeEmail mensagem, string evento, Guid referencia, CancellationToken cancellationToken)
    {
        try
        {
            await email.EnviarAsync(mensagem, cancellationToken);
            return true;
        }
        catch (FalhaNoEnvioDeEmailException)
        {
            // Neither the address nor the body (it holds the token) goes to the log.
            log.Write(LogSeverity.Warning, evento, new Dictionary<string, object?> { ["referencia"] = referencia });
            return false;
        }
    }
}
