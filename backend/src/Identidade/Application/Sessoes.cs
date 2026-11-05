using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// Sessions and tokens (spec 5.3, ADR-010). The session is a ROW; the access token only names it. On every request the server reads the row and the account it belongs to,
/// so deactivating the account, changing the password or the owner flag, revoking, expiring or removing the unit link takes effect on the NEXT request, with no list of
/// revoked tokens to keep. A refresh token is an opaque random secret stored only as SHA-256, rotated on every use; presenting a used one again revokes the session.
/// <para>Nothing here reads the HTTP request: the host passes strings and gets strings. Every method opens (or joins) a unit of work of the organization it is about; the
/// organization comes from a SIGNED token or from the refresh token's locator, and RLS refuses anything that does not match a real row.</para>
/// </summary>
public sealed class Sessoes(
    IUnitOfWork unitOfWork,
    ISessaoStore store,
    IContaStore contas,
    IUnidadesDaConta unidades,
    IEmissorDeTokens tokens,
    IAuditLog audit,
    IEventosDeSeguranca eventos,
    IClock clock,
    RelogiosDaSessao relogios,
    IAcessosConhecidos acessos)
{
    private const string RefreshPrefix = "r1";

    /// <summary>Rows of already used refresh tokens are kept this long (reuse detection window), then pruned at the next rotation of the session.</summary>
    private static readonly TimeSpan RefreshUsadoRetencao = TimeSpan.FromHours(24);

    // ---- start ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a session for an account that just authenticated (<paramref name="versaoCredencial"/> and <paramref name="deveTrocarSenha"/> come from <see cref="Autenticado"/> /
    /// <see cref="DeveTrocarSenha"/>). With exactly one linked unit it becomes the active one; with several or none the session starts WITHOUT an active unit.
    /// </summary>
    public Task<SessaoEmitida> IniciarAsync(Guid contaId, Guid organizacaoId, int versaoCredencial, bool deveTrocarSenha, ClienteDaSessao cliente, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cliente);
        return unitOfWork.ExecuteAsync(
            OrganizationContext.WithoutUnit(organizacaoId),
            ct => CriarAsync(contaId, organizacaoId, versaoCredencial, deveTrocarSenha, cliente, true, ct),
            cancellationToken);
    }

    /// <summary>
    /// After the person changed THEIR OWN password: a new normal session on the new credential version, and every other session of the account is revoked (they were already
    /// invalid by the version; this makes the list say so). Null when the account is gone or inactive.
    /// </summary>
    public Task<SessaoEmitida?> IniciarAposTrocaDeSenhaAsync(Guid organizacaoId, Guid contaId, ClienteDaSessao cliente, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cliente);
        return unitOfWork.ExecuteAsync(
            OrganizationContext.WithoutUnit(organizacaoId),
            async ct =>
            {
                var conta = await contas.ObterAsync(organizacaoId, contaId, ct);
                if (conta is not { Ativa: true })
                {
                    return (SessaoEmitida?)null;
                }

                var emitida = await CriarAsync(contaId, organizacaoId, conta.VersaoCredencial, false, cliente, false, ct);
                await store.RevogarOutrasDaContaAsync(organizacaoId, contaId, emitida.SessaoId, MotivoSenhaTrocada, clock.UtcNow, ct);
                return emitida;
            },
            cancellationToken);
    }

    private const string MotivoSenhaTrocada = "senha-trocada";

    /// <param name="alertarAcessoNovo">True for a login. The new session after the person's own password change is the same person on the same device: it is neither judged nor learned.</param>
    private async Task<SessaoEmitida> CriarAsync(Guid contaId, Guid organizacaoId, int versaoCredencial, bool deveTrocarSenha, ClienteDaSessao cliente, bool alertarAcessoNovo, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var linked = await unidades.ListarAsync(organizacaoId, contaId, ct);
        Guid? active = linked.Count == 1 ? linked[0] : null;
        var sessao = new NovaSessao(
            EntityId.New().Value,
            contaId,
            active,
            EntityId.New().Value,
            deveTrocarSenha,
            versaoCredencial,
            now,
            now + relogios.Inatividade,
            now + relogios.Absoluta,
            Truncate(cliente.Ip, 45),
            Truncate(cliente.Agente, 200));
        await store.InserirAsync(organizacaoId, sessao, ct);
        var (refresh, hash) = NewRefreshToken(organizacaoId);
        await store.InserirRefreshAsync(organizacaoId, EntityId.New().Value, sessao.Id, hash, now, ct);

        // New device or place (spec 5.5.5): learned and judged in the SAME unit of work as the session, so the event and the audit exist exactly when the session does.
        var aparelho = ImpressaoDeAcesso.Aparelho(cliente.Agente);
        var rede = ImpressaoDeAcesso.Rede(cliente.Ip);
        var visita = alertarAcessoNovo ? await acessos.RegistrarAsync(organizacaoId, contaId, aparelho, rede, now, ct) : null;
        var contexto = OrganizationContext.WithoutUnit(organizacaoId);
        await audit.RecordAsync(
            contexto,
            new AuditEntry
            {
                Action = IdentidadeAuditActions.SessaoIniciar,
                Result = AuditResult.Sucesso,
                UserId = contaId,
                SessionId = sessao.Id,
                UnitId = active,
                ObjectType = "sessao",
                ObjectId = sessao.Id.ToString("D"),
                After = new JsonObject
                {
                    ["restrita"] = deveTrocarSenha, // never a key with "senha": the audit refuses it
                    ["aparelhoNovo"] = visita is { DeveAlertar: true, AparelhoNovo: true },
                    ["redeNova"] = visita is { DeveAlertar: true, RedeNova: true },
                },
            },
            ct);
        if (visita is { DeveAlertar: true })
        {
            await eventos.PublicarAsync(
                contexto,
                new LoginNovo(EntityId.New().Value, organizacaoId, contaId, sessao.Id, visita.AparelhoNovo, visita.RedeNova, rede, aparelho, now),
                ct);
        }

        return Emitida(sessao.Id, contaId, organizacaoId, active, deveTrocarSenha, versaoCredencial, sessao.ExpiraEm, refresh, now);
    }

    private SessaoEmitida Emitida(Guid sessaoId, Guid contaId, Guid organizacaoId, Guid? unidade, bool deveTrocarSenha, int versaoCredencial, DateTimeOffset expiraEm, string refresh, DateTimeOffset now) =>
        new(
            sessaoId,
            contaId,
            organizacaoId,
            unidade,
            deveTrocarSenha,
            tokens.EmitirAcesso(RealmDeToken.App, new ClaimsDeAcesso(contaId, organizacaoId, sessaoId, versaoCredencial)),
            now + tokens.ValidadeDoAcesso,
            refresh,
            expiraEm);

    // ---- every request --------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Authenticates an access token: signature, audience and lifetime, then the SESSION ROW and its account. Null for any reason (the caller answers 401 and does not say why).
    /// Activity slides the inactivity window; the write happens at most once per <see cref="RelogiosDaSessao.IntervaloDeAtividade"/> so a busy session is not a write per request.
    /// </summary>
    public async Task<SessaoAutenticada?> AutenticarAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var claims = tokens.ValidarAcesso(RealmDeToken.App, accessToken);
        if (claims is null)
        {
            return null;
        }

        return await unitOfWork.ExecuteAsync(
            OrganizationContext.WithoutUnit(claims.OrganizacaoId),
            async ct =>
            {
                var sessao = await store.ObterParaRequisicaoAsync(claims.OrganizacaoId, claims.SessaoId, ct);
                var now = clock.UtcNow;
                if (sessao is null || sessao.ContaId != claims.ContaId || sessao.VersaoCredencialDaSessao != claims.VersaoCredencial || !sessao.EstaViva(now))
                {
                    return null;
                }

                if (now - sessao.UltimaAtividadeEm >= relogios.IntervaloDeAtividade)
                {
                    await store.TocarAtividadeAsync(claims.OrganizacaoId, sessao.Id, now, Min(now + relogios.Inatividade, sessao.ExpiraEm), ct);
                }

                return new SessaoAutenticada(sessao.Id, sessao.ContaId, claims.OrganizacaoId, sessao.UnidadeAtivaId, sessao.DeveTrocarSenha);
            },
            cancellationToken);
    }

    // ---- refresh -------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Exchanges a refresh token for the next one (rotation) and a new access token. The row of the presented token is locked while this runs, so two simultaneous requests with
    /// the SAME token are serialized: the first rotates, the second finds the token already used and is treated as a REUSE (the strictest safe reading: it is indistinguishable from
    /// a stolen token being replayed), which revokes the whole session. A client must therefore refresh from a single place at a time (ADR-010).
    /// </summary>
    public async Task<ResultadoRenovacao> RenovarAsync(string? refreshToken, ClienteDaSessao cliente, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cliente);
        if (!TryParseRefresh(refreshToken, out var organizacaoId, out var hash))
        {
            return new ResultadoRenovacao(StatusRenovacao.Invalida, null);
        }

        var context = OrganizationContext.WithoutUnit(organizacaoId);
        return await unitOfWork.ExecuteAsync(
            context,
            async ct =>
            {
                var now = clock.UtcNow;
                var found = await store.ObterRefreshTravadoAsync(organizacaoId, hash, ct);
                if (found is null)
                {
                    return new ResultadoRenovacao(StatusRenovacao.Invalida, null);
                }

                var sessao = await store.ObterParaRequisicaoAsync(organizacaoId, found.SessaoId, ct);
                if (sessao is null)
                {
                    return new ResultadoRenovacao(StatusRenovacao.Invalida, null);
                }

                if (found.UsadoEm is not null)
                {
                    await RegistrarReusoAsync(context, organizacaoId, sessao, now, ct);
                    return new ResultadoRenovacao(StatusRenovacao.Reutilizada, null);
                }

                if (!sessao.EstaViva(now))
                {
                    return new ResultadoRenovacao(StatusRenovacao.Invalida, null);
                }

                var (next, nextHash) = NewRefreshToken(organizacaoId);
                await store.MarcarRefreshUsadoAsync(organizacaoId, found.Id, now, ct);
                await store.PodarRefreshUsadosAsync(organizacaoId, sessao.Id, now - RefreshUsadoRetencao, ct);
                await store.InserirRefreshAsync(organizacaoId, EntityId.New().Value, sessao.Id, nextHash, now, ct);
                await store.TocarAtividadeAsync(organizacaoId, sessao.Id, now, Min(now + relogios.Inatividade, sessao.ExpiraEm), ct);
                var emitida = Emitida(sessao.Id, sessao.ContaId, organizacaoId, sessao.UnidadeAtivaId, sessao.DeveTrocarSenha, sessao.VersaoCredencialDaSessao, sessao.ExpiraEm, next, now);
                return new ResultadoRenovacao(StatusRenovacao.Renovada, emitida);
            },
            cancellationToken);
    }

    /// <summary>Revokes the session of a reused token ONCE (a second replay finds it already revoked and records nothing more), audits it and raises the security event, all in the unit of work of the refresh.</summary>
    private async Task RegistrarReusoAsync(OrganizationContext context, Guid organizacaoId, SessaoParaRequisicao sessao, DateTimeOffset now, CancellationToken ct)
    {
        if (!await store.RevogarAsync(organizacaoId, sessao.Id, MotivoRefreshReutilizado, now, ct))
        {
            return;
        }

        await audit.RecordAsync(
            context,
            new AuditEntry
            {
                Action = IdentidadeAuditActions.SessaoRefreshReutilizado,
                Result = AuditResult.Negado,
                UserId = sessao.ContaId,
                SessionId = sessao.Id,
                UnitId = sessao.UnidadeAtivaId,
                ObjectType = "sessao",
                ObjectId = sessao.Id.ToString("D"),
                After = new JsonObject { ["motivo"] = MotivoRefreshReutilizado },
            },
            ct);
        await eventos.PublicarAsync(context, new RefreshReutilizado(EntityId.New().Value, organizacaoId, sessao.Id, sessao.ContaId, now), ct);
    }

    private const string MotivoRefreshReutilizado = "refresh-reutilizado";

    // ---- logout and revocation ------------------------------------------------------------------------------------------------------------------

    /// <summary>Logout by the refresh token (the cookie). Idempotent: an unknown or already ended session is not an error.</summary>
    public async Task EncerrarPorRefreshAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        if (!TryParseRefresh(refreshToken, out var organizacaoId, out var hash))
        {
            return;
        }

        var context = OrganizationContext.WithoutUnit(organizacaoId);
        await unitOfWork.ExecuteAsync(
            context,
            async ct =>
            {
                var found = await store.ObterRefreshTravadoAsync(organizacaoId, hash, ct);
                if (found is null)
                {
                    return;
                }

                var sessao = await store.ObterParaRequisicaoAsync(organizacaoId, found.SessaoId, ct);
                if (sessao is null || !await store.RevogarAsync(organizacaoId, sessao.Id, "encerrada-pelo-usuario", clock.UtcNow, ct))
                {
                    return;
                }

                await audit.RecordAsync(
                    context,
                    new AuditEntry
                    {
                        Action = IdentidadeAuditActions.SessaoEncerrar,
                        Result = AuditResult.Sucesso,
                        UserId = sessao.ContaId,
                        SessionId = sessao.Id,
                        UnitId = sessao.UnidadeAtivaId,
                        ObjectType = "sessao",
                        ObjectId = sessao.Id.ToString("D"),
                    },
                    ct);
            },
            cancellationToken);
    }

    /// <summary>
    /// Sets the active unit of a session. Runs INSIDE the unit of work of the switch (<c>UnidadesDoUsuario.TrocarAsync</c>): the session row, the audit record and the link check
    /// commit or roll back together. Throws outside a unit of work.
    /// </summary>
    public Task DefinirUnidadeAtivaAsync(Guid organizacaoId, Guid sessaoId, Guid unidadeId, CancellationToken cancellationToken) =>
        store.DefinirUnidadeAtivaAsync(organizacaoId, sessaoId, unidadeId, cancellationToken);

    /// <summary>The live sessions of the signed-in account.</summary>
    public Task<IReadOnlyList<SessaoResumo>> ListarPropriasAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct => Resumos(await store.ListarVivasAsync(request.Organization.OrganizationId, request.UserId, clock.UtcNow, ct), request.SessionId),
            cancellationToken);
    }

    /// <summary>
    /// Every live session of the organization. For now only an OWNER may (ADR-010; ticket 05 replaces this with <c>sessao:ver-todas</c> decided by the permission engine). Null when not allowed.
    /// </summary>
    public Task<IReadOnlyList<SessaoResumo>?> ListarTodasAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await EhDonoAsync(request, ct))
                {
                    return (IReadOnlyList<SessaoResumo>?)null;
                }

                return Resumos(await store.ListarVivasAsync(request.Organization.OrganizationId, null, clock.UtcNow, ct), request.SessionId);
            },
            cancellationToken);
    }

    /// <summary>
    /// Ends a session. One's OWN sessions can always be ended; someone else's only by an owner (for now, see <see cref="ListarTodasAsync"/>). A session that does not exist, is not live,
    /// belongs to another organization (RLS) or is not the caller's to touch answers <see cref="ResultadoRevogacao.NaoEncontrada"/>.
    /// </summary>
    public Task<ResultadoRevogacao> RevogarAsync(IRequestContext request, Guid sessaoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var organizacaoId = request.Organization.OrganizationId;
                var alvo = await store.ObterVivaAsync(organizacaoId, sessaoId, clock.UtcNow, ct);
                if (alvo is null)
                {
                    return ResultadoRevogacao.NaoEncontrada;
                }

                var propria = alvo.ContaId == request.UserId;
                if (!propria && !await EhDonoAsync(request, ct))
                {
                    return ResultadoRevogacao.NaoEncontrada;
                }

                if (!await store.RevogarAsync(organizacaoId, sessaoId, propria ? "encerrada-pelo-usuario" : "encerrada-por-administrador", clock.UtcNow, ct))
                {
                    return ResultadoRevogacao.NaoEncontrada;
                }

                await audit.RecordAsync(
                    request,
                    new AuditEntry
                    {
                        Action = IdentidadeAuditActions.SessaoRevogar,
                        Result = AuditResult.Sucesso,
                        ObjectType = "sessao",
                        ObjectId = sessaoId.ToString("D"),
                        After = new JsonObject { ["motivo"] = propria ? "propria" : "administrador", ["contaId"] = alvo.ContaId.ToString("D") },
                    },
                    ct);
                return ResultadoRevogacao.Revogada;
            },
            cancellationToken);
    }

    private async Task<bool> EhDonoAsync(IRequestContext request, CancellationToken ct) =>
        await contas.ObterAsync(request.Organization.OrganizationId, request.UserId, ct) is { Dono: true, Ativa: true };

    private static IReadOnlyList<SessaoResumo> Resumos(IReadOnlyList<SessaoParaListar> rows, Guid atual) =>
        [.. rows.Select(r => new SessaoResumo(r.Id, r.ContaId, r.UnidadeAtivaId, r.CriadaEm, r.UltimaAtividadeEm, r.ExpiraEm, r.Ip, r.Agente, r.Id == atual))];

    // ---- refresh token format ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>r1.&lt;organization id, 32 hex&gt;.&lt;256 random bits, base64url&gt;</c>. The organization is a LOCATOR (the lookup runs under RLS and needs it; it is not a secret and a wrong
    /// one finds nothing); the random part is the secret. Only the SHA-256 of the whole string is stored.
    /// </summary>
    private static (string Token, byte[] Hash) NewRefreshToken(Guid organizacaoId)
    {
        var token = $"{RefreshPrefix}.{organizacaoId:N}.{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        return (token, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static bool TryParseRefresh(string? token, out Guid organizacaoId, out byte[] hash)
    {
        organizacaoId = Guid.Empty;
        hash = [];
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != RefreshPrefix)
        {
            return false;
        }

        var valid = parts[1].Length == 32 && Guid.TryParseExact(parts[1], "N", out organizacaoId) && organizacaoId != Guid.Empty && parts[2].Length == 43 && parts[2].All(IsBase64UrlChar);
        if (!valid)
        {
            organizacaoId = Guid.Empty;
            return false;
        }

        hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return true;
    }

    private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
