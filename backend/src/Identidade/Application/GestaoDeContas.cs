using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// Creating, deactivating and (re)naming the owner of accounts of the current organization (spec 5.2.1, 5.2.7). Every state change runs in ONE unit of work with
/// its audit record. NO method receives the password of another person: the operational account gets a generated temporary credential that is returned once; the
/// founding owner types their own password. Who may call these (the <c>usuario:*</c> permissions, ticket 05/06) is decided by the route BEFORE it gets here; the
/// protection rules below hold whoever asks. Not called by any route yet.
/// </summary>
public sealed class GestaoDeContas(
    IUnitOfWork unitOfWork,
    IContaStore contas,
    IPasswordHasher hasher,
    ICompromisedPasswordCheck compromised,
    ITemporaryCredentialGenerator temporary,
    IAuditLog audit,
    IClock clock,
    ValidadeDaCredencialTemporaria validade)
{
    private const int MaximoNome = 200;

    /// <summary>
    /// The founding owner of an organization, who chooses their OWN password (signup; the invite flow of ticket 09 is the same shape for the other accounts with e-mail).
    /// The caller is the onboarding flow of a new organization (a trusted flow such as the Control Tower), never a public route and never a user acting on someone else.
    /// Allowed ONLY in an organization with no accounts at all (active or not): the check and the insert happen under a lock on the organization row, so of two
    /// simultaneous calls exactly one wins and the other gets <see cref="StatusCriacao.OrganizacaoJaIniciada"/>.
    /// </summary>
    public async Task<ResultadoCriacaoDono> CriarDonoAsync(Guid organizationId, string email, string nome, string senha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(senha);
        if (!EmailDeConta.TryNormalize(email, out var emailNormalizado) || !NomeValido(nome))
        {
            return new ResultadoCriacaoDono(StatusCriacao.IdentificadorInvalido, null, []);
        }

        if (senha.Length > PoliticaDeSenha.LimiteDeEntrada)
        {
            return new ResultadoCriacaoDono(StatusCriacao.SenhaInvalida, null, [ProblemaDeSenha.Longa]); // before NFKC and hashing
        }

        var problemas = PoliticaDeSenha.Avaliar(senha, emailNormalizado);
        if (problemas.Count > 0)
        {
            return new ResultadoCriacaoDono(StatusCriacao.SenhaInvalida, null, problemas);
        }

        var normalizada = PoliticaDeSenha.Normalizar(senha);
        try
        {
            if (await compromised.IsCompromisedAsync(normalizada, cancellationToken))
            {
                return new ResultadoCriacaoDono(StatusCriacao.SenhaVazada, null, []);
            }
        }
        catch (VerificacaoDeSenhaIndisponivelException)
        {
            return new ResultadoCriacaoDono(StatusCriacao.VerificacaoIndisponivel, null, []);
        }

        var hash = await hasher.HashAsync(normalizada, cancellationToken);
        var id = EntityId.New().Value;
        var context = OrganizationContext.WithoutUnit(organizationId);
        var created = await CriarAsync(
            context,
            actorId: null,
            new NovaConta(id, TipoConta.Email, nome.Trim(), emailNormalizado, null, true, clock.UtcNow),
            hash,
            deveTrocarSenha: false,
            temporariaExpiraEm: null,
            fundador: true,
            new AuditEntry
            {
                Action = IdentidadeAuditActions.ContaCriar,
                UserId = id, // the founder acts for themselves
                ObjectType = "conta",
                ObjectId = id.ToString("D"),
                After = new JsonObject { ["tipo"] = "email", ["email"] = emailNormalizado, ["dono"] = true },
            },
            cancellationToken);

        return created switch
        {
            CriarResultado.Criada => new ResultadoCriacaoDono(StatusCriacao.Criada, id, []),
            CriarResultado.OrganizacaoJaIniciada => new ResultadoCriacaoDono(StatusCriacao.OrganizacaoJaIniciada, null, []),
            _ => new ResultadoCriacaoDono(StatusCriacao.Indisponivel, null, []),
        };
    }

    /// <summary>
    /// An operational account (waiter, cashier): user name + generated temporary credential, returned ONCE in the result and never stored or recoverable. The first
    /// login can only change the password (<see cref="Autenticacao"/>).
    /// </summary>
    public async Task<ResultadoCriacaoOperacional> CriarOperacionalAsync(IRequestContext request, string usuario, string nome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NomeDeUsuario.TryNormalize(usuario, out var usuarioNormalizado) || !NomeValido(nome))
        {
            return new ResultadoCriacaoOperacional(StatusCriacao.IdentificadorInvalido, null, null);
        }

        var credencial = temporary.Generate();
        var hash = await hasher.HashAsync(PoliticaDeSenha.Normalizar(credencial), cancellationToken);
        var id = EntityId.New().Value;
        var created = await CriarAsync(
            request.Organization,
            request.UserId,
            new NovaConta(id, TipoConta.Operacional, nome.Trim(), null, usuarioNormalizado, false, clock.UtcNow),
            hash,
            deveTrocarSenha: true,
            temporariaExpiraEm: clock.UtcNow + validade.Valor,
            fundador: false,
            new AuditEntry
            {
                Action = IdentidadeAuditActions.ContaCriar,
                UserId = request.UserId,
                SessionId = request.SessionId,
                UnitId = request.Organization.UnitId,
                ObjectType = "conta",
                ObjectId = id.ToString("D"),
                After = new JsonObject { ["tipo"] = "operacional", ["usuario"] = usuarioNormalizado, ["dono"] = false },
            },
            cancellationToken);

        if (created == CriarResultado.Negada)
        {
            await audit.RecordDetachedAsync(
                request,
                Entry(IdentidadeAuditActions.ContaCriar, AuditResult.Negado, id, null, new JsonObject { ["motivo"] = "ator-nao-pode-agir" }),
                cancellationToken);
            return new ResultadoCriacaoOperacional(StatusCriacao.Negada, null, null);
        }

        return created == CriarResultado.Criada
            ? new ResultadoCriacaoOperacional(StatusCriacao.Criada, id, new CredencialTemporaria(credencial))
            : new ResultadoCriacaoOperacional(StatusCriacao.Indisponivel, null, null);
    }

    /// <summary>
    /// Spec 5.2.5: whoever holds <c>usuario:redefinir-acesso</c> (decided by the route) generates a NEW temporary credential for an OPERATIONAL account whose person lost the access: returned ONCE, never stored or
    /// recoverable, expires, forces the change at the next login (<see cref="Autenticacao"/>), and ends every session of the old credential. Nobody types the password for the person. It is also the only way back
    /// for an account whose temporary credential expired. Accounts with e-mail recover by e-mail (<see cref="RecuperacaoDeAcesso"/>); nobody resets their own access this way.
    /// </summary>
    public async Task<ResultadoRedefinicao> RedefinirAcessoAsync(IRequestContext request, Guid contaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organizationId = request.Organization.OrganizationId;
        var credencial = temporary.Generate();
        var hash = await hasher.HashAsync(PoliticaDeSenha.Normalizar(credencial), cancellationToken);
        var expiraEm = clock.UtcNow + validade.Valor;
        var status = await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (contaId == request.UserId || (await contas.ObterComCredencialAsync(organizationId, request.UserId, ct)) is not { Ativa: true, DeveTrocarSenha: false })
                {
                    return StatusRedefinicao.Negada;
                }

                var alvo = await contas.ObterAsync(organizationId, contaId, ct);
                if (alvo is not { Ativa: true })
                {
                    return StatusRedefinicao.NaoEncontrada;
                }

                if (alvo.Tipo != TipoConta.Operacional)
                {
                    return StatusRedefinicao.ContaComEmail;
                }

                if (!await contas.RedefinirCredencialTemporariaAsync(organizationId, contaId, hash, expiraEm, ct))
                {
                    return StatusRedefinicao.NaoEncontrada;
                }

                await audit.RecordAsync(request, Entry(IdentidadeAuditActions.ContaRedefinirAcesso, AuditResult.Sucesso, contaId, null, new JsonObject { ["tipo"] = "operacional" }), ct);
                return StatusRedefinicao.Redefinida;
            },
            cancellationToken);

        if (status == StatusRedefinicao.Negada)
        {
            await audit.RecordDetachedAsync(request, Entry(IdentidadeAuditActions.ContaRedefinirAcesso, AuditResult.Negado, contaId, null, new JsonObject { ["motivo"] = "ator-nao-pode-agir" }), cancellationToken);
        }

        return status == StatusRedefinicao.Redefinida
            ? new ResultadoRedefinicao(status, new CredencialTemporaria(credencial), expiraEm)
            : new ResultadoRedefinicao(status, null, null);
    }

    /// <summary>Deactivates an account (marks it, ends its sessions through the credential version, keeps all history). The protection rules of spec 5.2.7 apply.</summary>
    public async Task<ResultadoProtecao> DesativarAsync(IRequestContext request, Guid contaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organizationId = request.Organization.OrganizationId;
        var refusal = await unitOfWork.ExecuteAsync<RecusaDeProtecao?>(
            request,
            async ct =>
            {
                // Lock the active owners first: two owners leaving at the same time serialize here and the second sees the first (always one owner).
                var owners = await contas.TravarDonosAtivosAsync(organizationId, ct);
                var actor = await contas.ObterAsync(organizationId, request.UserId, ct);
                if (await MustChangePasswordAsync(organizationId, request.UserId, ct))
                {
                    return RecusaDeProtecao.AtorDeveTrocarSenha;
                }

                var target = await contas.ObterAsync(organizationId, contaId, ct);
                var denied = ProtecaoDeConta.PodeDesativar(actor, target, owners);
                if (denied is not null)
                {
                    return denied;
                }

                if (!await contas.DesativarAsync(organizationId, contaId, clock.UtcNow, ct))
                {
                    return RecusaDeProtecao.ContaDesativada;
                }

                await audit.RecordAsync(request, Entry(IdentidadeAuditActions.ContaDesativar, AuditResult.Sucesso, contaId, new JsonObject { ["ativa"] = true }, new JsonObject { ["ativa"] = false }), ct);
                return null;
            },
            cancellationToken);

        return await FinishAsync(request, IdentidadeAuditActions.ContaDesativar, contaId, refusal, new JsonObject { ["motivo"] = refusal?.ToString() }, cancellationToken);
    }

    /// <summary>
    /// Names (or tries to un-name) an owner. Only an owner names another owner, nobody changes their own permissions and an owner is never demoted by anyone else; the
    /// credential version grows so the promoted account's sessions are re-evaluated (spec 5.3).
    /// </summary>
    public async Task<ResultadoProtecao> DefinirDonoAsync(IRequestContext request, Guid contaId, bool dono, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organizationId = request.Organization.OrganizationId;
        var refusal = await unitOfWork.ExecuteAsync<RecusaDeProtecao?>(
            request,
            async ct =>
            {
                var owners = await contas.TravarDonosAtivosAsync(organizationId, ct);
                var actor = await contas.ObterAsync(organizationId, request.UserId, ct);
                if (await MustChangePasswordAsync(organizationId, request.UserId, ct))
                {
                    return RecusaDeProtecao.AtorDeveTrocarSenha;
                }

                var target = await contas.ObterAsync(organizationId, contaId, ct);
                var denied = ProtecaoDeConta.PodeDefinirDono(actor, target, dono, owners);
                if (denied is not null)
                {
                    return denied;
                }

                if (!await contas.DefinirDonoAsync(organizationId, contaId, dono, ct))
                {
                    return RecusaDeProtecao.ContaDesativada; // deactivated by another unit after it was read: nothing was written
                }

                await audit.RecordAsync(request, Entry(IdentidadeAuditActions.ContaDefinirDono, AuditResult.Sucesso, contaId, new JsonObject { ["dono"] = !dono }, new JsonObject { ["dono"] = dono }), ct);
                return null;
            },
            cancellationToken);

        return await FinishAsync(request, IdentidadeAuditActions.ContaDefinirDono, contaId, refusal, new JsonObject { ["dono"] = dono, ["motivo"] = refusal?.ToString() }, cancellationToken);
    }

    private static bool NomeValido(string? nome) => !string.IsNullOrWhiteSpace(nome) && nome.Trim().Length <= MaximoNome;

    private static AuditEntry Entry(string action, AuditResult result, Guid contaId, JsonObject? before, JsonObject? after) => new()
    {
        Action = action,
        Result = result,
        ObjectType = "conta",
        ObjectId = contaId.ToString("D"),
        Before = before,
        After = after,
    };

    /// <summary>A refusal is audited as <c>negado</c> on its own transaction AFTER the unit of work ended (it persists though the operation did not happen).</summary>
    private async Task<ResultadoProtecao> FinishAsync(IRequestContext request, string action, Guid contaId, RecusaDeProtecao? refusal, JsonObject details, CancellationToken cancellationToken)
    {
        if (refusal is null)
        {
            return new ResultadoProtecao(true, null);
        }

        await audit.RecordDetachedAsync(request, Entry(action, AuditResult.Negado, contaId, null, details), cancellationToken);
        return new ResultadoProtecao(false, refusal);
    }

    /// <summary>While the credential is a temporary one nothing is allowed but changing the password (spec 5.2.1), whatever the session or the route let through.</summary>
    private async Task<bool> MustChangePasswordAsync(Guid organizationId, Guid actorId, CancellationToken cancellationToken) =>
        (await contas.ObterComCredencialAsync(organizationId, actorId, cancellationToken)) is { DeveTrocarSenha: true };

    private async Task<CriarResultado> CriarAsync(OrganizationContext context, Guid? actorId, NovaConta conta, string hash, bool deveTrocarSenha, DateTimeOffset? temporariaExpiraEm, bool fundador, AuditEntry entry, CancellationToken cancellationToken)
    {
        var organizationId = context.OrganizationId;
        try
        {
            return await unitOfWork.ExecuteAsync(
                context,
                async ct =>
                {
                    if (fundador && await contas.TravarOrganizacaoEContarContasAsync(organizationId, ct) > 0)
                    {
                        return CriarResultado.OrganizacaoJaIniciada;
                    }

                    if (actorId is { } actor && (await contas.ObterComCredencialAsync(organizationId, actor, ct)) is not { Ativa: true, DeveTrocarSenha: false })
                    {
                        return CriarResultado.Negada;
                    }

                    if (!await contas.InserirAsync(organizationId, conta, hash, deveTrocarSenha, temporariaExpiraEm, ct))
                    {
                        return CriarResultado.Indisponivel;
                    }

                    if (conta.Email is not null && !await contas.RegistrarEmailAsync(organizationId, conta.Id, conta.Email, ct))
                    {
                        throw new ContaIndisponivelException(); // a return value would not roll back the rows already written
                    }

                    await audit.RecordAsync(context, entry, ct);
                    return CriarResultado.Criada;
                },
                cancellationToken);
        }
        catch (ContaIndisponivelException)
        {
            return CriarResultado.Indisponivel;
        }
    }

    private enum CriarResultado
    {
        Criada,
        Indisponivel,
        Negada,
        OrganizacaoJaIniciada,
    }
}

/// <summary>Signal: the e-mail already belongs to another account, so the unit of work must roll back what it wrote (a return value would not).</summary>
public sealed class ContaIndisponivelException : Exception
{
    public ContaIndisponivelException()
        : base("Account identifier unavailable.")
    {
    }

    public ContaIndisponivelException(string message)
        : base(message)
    {
    }

    public ContaIndisponivelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
