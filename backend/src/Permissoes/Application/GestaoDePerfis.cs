using System.Text.Json.Nodes;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Permissoes.Application;

public enum ResultadoGestao
{
    Ok,

    /// <summary>The caller lacks the permission to manage (403, no reason to the client).</summary>
    Negado,

    NaoEncontrado,

    /// <summary>A factory profile is not editable, only duplicable (spec 5.4.3).</summary>
    PerfilDeFabrica,

    /// <summary>Nobody changes their own rights, directly or through a profile they hold (spec 5.4.5).</summary>
    AutoPromocao,

    /// <summary>The caller tried to hand on a right they do not hold, or hold less generously (spec 5.4.5).</summary>
    EscalacaoNegada,

    /// <summary>A rule names an unknown permission, a limit on a deny, or the like.</summary>
    Invalido,

    NomeEmUso,
}

public sealed record ResultadoCriacao(ResultadoGestao Resultado, Guid? Id = null);

/// <summary>
/// Management of profiles, rules, assignments and individual overrides (spec 5.4.3 to 5.4.5, 5.4.9). Every operation is ONE unit of work: ask the engine whether the caller may
/// manage at all (<c>perfil:gerir</c> / <c>permissao:atribuir</c>), apply the anti-escalation rules, write, audit. Anything refused leaves nothing behind. Refusals that look like an
/// attack (own rights, escalation) are also recorded as <c>negado</c> on their own transaction. The engine is the only judge of what the caller holds; no profile name is read here.
/// </summary>
public sealed class GestaoDePerfis(
    IUnitOfWork unitOfWork,
    IAuthorizationEngine engine,
    IPolicySource policies,
    IPerfisDoUsuario perfisDoUsuario,
    IPerfilStore store,
    IAuditLog audit,
    IClock clock)
{
    private const int MaxRules = 500;

    private sealed record Saida(ResultadoGestao Resultado, string? Motivo = null, Guid? Id = null);

    /// <summary>A brand-new custom profile with its own rules.</summary>
    public Task<ResultadoCriacao> CriarAsync(IRequestContext request, string nome, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken = default) =>
        CriarInternoAsync(request, nome, modelo: null, acompanha: false, regras, cancellationToken);

    /// <summary>
    /// Duplicates a factory or custom profile (<paramref name="origem"/> is its key). "Copy" (<paramref name="acompanhar"/> false) copies the rules once; "follow" keeps the
    /// template as source: its rules stay in force for the new profile and move when the template moves (spec 5.4.3).
    /// </summary>
    public async Task<ResultadoCriacao> DuplicarAsync(IRequestContext request, string origem, string nome, bool acompanhar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(origem);
        var (found, rules) = await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var set = await policies.ObterAsync(request.Organization.OrganizationId, ct);
                var fromTemplate = await RulesOfKeyAsync(request, set, origem, ct);
                return (fromTemplate is not null, fromTemplate ?? []);
            },
            cancellationToken);
        if (!found)
        {
            return new ResultadoCriacao(ResultadoGestao.NaoEncontrado);
        }

        // Following keeps no rules of its own; copying makes them the profile's own.
        return await CriarInternoAsync(request, nome, origem, acompanhar, acompanhar ? [] : rules, cancellationToken);
    }

    /// <summary>Replaces the OWN rules of a custom profile. A factory profile is refused; so is a profile the caller holds (own rights).</summary>
    public async Task<ResultadoGestao> SubstituirRegrasAsync(IRequestContext request, string perfil, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(perfil);
        ArgumentNullException.ThrowIfNull(regras);
        var outcome = await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await MayAsync(request, PerfisPermissions.Gerir, ct))
                {
                    return new Saida(ResultadoGestao.Negado);
                }

                var set = await policies.ObterAsync(request.Organization.OrganizationId, ct);
                if (!Guid.TryParse(perfil, out var id))
                {
                    return new Saida(IsFactoryKey(set, perfil) ? ResultadoGestao.PerfilDeFabrica : ResultadoGestao.NaoEncontrado);
                }

                var current = await store.ObterAsync(request.Organization.OrganizationId, id, ct);
                if (current is null)
                {
                    return new Saida(ResultadoGestao.NaoEncontrado);
                }

                if (!Valid(set, regras) || !await UnitsExistAsync(request.Organization.OrganizationId, regras, ct))
                {
                    return new Saida(ResultadoGestao.Invalido);
                }

                var mine = await perfisDoUsuario.ListarAsync(request.Organization, request.UserId, ct);
                if (mine.Contains(ChavesDePerfil.Personalizado(id)))
                {
                    return new Saida(ResultadoGestao.AutoPromocao, "perfil-proprio");
                }

                if (!CanGrantAll(set, new Subject(request.UserId, mine), regras))
                {
                    return new Saida(ResultadoGestao.EscalacaoNegada, "regra-acima-do-que-possui");
                }

                await store.SubstituirRegrasAsync(request.Organization.OrganizationId, id, regras, ct);
                await audit.RecordAsync(
                    request,
                    new AuditEntry
                    {
                        Action = PerfisAuditActions.PerfilEditarRegras,
                        ObjectType = "perfil",
                        ObjectId = ChavesDePerfil.Personalizado(id),
                        Before = Wrap(current.Regras),
                        After = Wrap(regras),
                    },
                    ct);
                return new Saida(ResultadoGestao.Ok);
            },
            cancellationToken);
        await RecordRefusalAsync(request, outcome, "perfil", perfil, cancellationToken);
        return outcome.Resultado;
    }

    /// <summary>Gives a custom profile to a person. The caller must hold every right the profile carries (including the template's), and cannot change themselves.</summary>
    public Task<ResultadoGestao> AtribuirAsync(IRequestContext request, Guid userId, Guid perfil, CancellationToken cancellationToken = default) =>
        AssignAsync(request, userId, perfil, give: true, cancellationToken);

    /// <summary>Takes a custom profile from a person. Nobody removes a profile from themselves either.</summary>
    public Task<ResultadoGestao> RemoverAtribuicaoAsync(IRequestContext request, Guid userId, Guid perfil, CancellationToken cancellationToken = default) =>
        AssignAsync(request, userId, perfil, give: false, cancellationToken);

    /// <summary>Replaces the individual overrides of a person (rules that exist only for them).</summary>
    public async Task<ResultadoGestao> DefinirOverridesAsync(IRequestContext request, Guid userId, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(regras);
        var outcome = await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await MayAsync(request, PerfisPermissions.Atribuir, ct))
                {
                    return new Saida(ResultadoGestao.Negado);
                }

                if (userId == request.UserId)
                {
                    return new Saida(ResultadoGestao.AutoPromocao, "usuario-proprio");
                }

                var organizationId = request.Organization.OrganizationId;
                if (!await store.ContaAtivaExisteAsync(organizationId, userId, ct))
                {
                    return new Saida(ResultadoGestao.NaoEncontrado);
                }

                var set = await policies.ObterAsync(organizationId, ct);
                if (!Valid(set, regras) || !await UnitsExistAsync(organizationId, regras, ct))
                {
                    return new Saida(ResultadoGestao.Invalido);
                }

                var mine = await perfisDoUsuario.ListarAsync(request.Organization, request.UserId, ct);
                if (!CanGrantAll(set, new Subject(request.UserId, mine), regras))
                {
                    return new Saida(ResultadoGestao.EscalacaoNegada, "regra-acima-do-que-possui");
                }

                var before = await store.OverridesAsync(organizationId, userId, ct);
                await store.SubstituirOverridesAsync(organizationId, userId, regras, ct);
                await audit.RecordAsync(
                    request,
                    new AuditEntry
                    {
                        Action = PerfisAuditActions.PermissaoDefinirOverrides,
                        ObjectType = "usuario",
                        ObjectId = userId.ToString("D"),
                        Before = Wrap(before),
                        After = Wrap(regras),
                    },
                    ct);
                return new Saida(ResultadoGestao.Ok);
            },
            cancellationToken);
        await RecordRefusalAsync(request, outcome, "usuario", userId.ToString("D"), cancellationToken);
        return outcome.Resultado;
    }

    private async Task<ResultadoCriacao> CriarInternoAsync(IRequestContext request, string nome, string? modelo, bool acompanha, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(regras);
        var outcome = await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await MayAsync(request, PerfisPermissions.Gerir, ct))
                {
                    return new Saida(ResultadoGestao.Negado);
                }

                var name = nome?.Trim() ?? string.Empty;
                var set = await policies.ObterAsync(request.Organization.OrganizationId, ct);
                if (name.Length is 0 or > 80 || !Valid(set, regras) || !await UnitsExistAsync(request.Organization.OrganizationId, regras, ct))
                {
                    return new Saida(ResultadoGestao.Invalido);
                }

                var mine = await perfisDoUsuario.ListarAsync(request.Organization, request.UserId, ct);
                var effective = new List<RegraDeAcesso>(regras);
                if (acompanha && modelo is not null)
                {
                    effective.AddRange(await RulesOfKeyAsync(request, set, modelo, ct) ?? []);
                }

                // Creating hands nothing to anyone, but a profile nobody could safely assign is a trap: the same rule applies from the start.
                if (!CanGrantAll(set, new Subject(request.UserId, mine), effective))
                {
                    return new Saida(ResultadoGestao.EscalacaoNegada, "regra-acima-do-que-possui");
                }

                var id = EntityId.New().Value;
                var created = await store.CriarAsync(request.Organization.OrganizationId, new PerfilPersonalizado(id, name, modelo, acompanha, regras), clock.UtcNow, ct);
                if (!created)
                {
                    return new Saida(ResultadoGestao.NomeEmUso);
                }

                await audit.RecordAsync(
                    request,
                    new AuditEntry
                    {
                        Action = PerfisAuditActions.PerfilCriar,
                        ObjectType = "perfil",
                        ObjectId = ChavesDePerfil.Personalizado(id),
                        After = new JsonObject { ["nome"] = name, ["modelo"] = modelo, ["acompanhaModelo"] = acompanha, ["regras"] = Describe(regras) },
                    },
                    ct);
                return new Saida(ResultadoGestao.Ok, Id: id);
            },
            cancellationToken);
        await RecordRefusalAsync(request, outcome, "perfil", nome ?? string.Empty, cancellationToken);
        return new ResultadoCriacao(outcome.Resultado, outcome.Id);
    }

    private async Task<ResultadoGestao> AssignAsync(IRequestContext request, Guid userId, Guid perfil, bool give, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await MayAsync(request, PerfisPermissions.Atribuir, ct))
                {
                    return new Saida(ResultadoGestao.Negado);
                }

                if (userId == request.UserId)
                {
                    return new Saida(ResultadoGestao.AutoPromocao, "usuario-proprio");
                }

                var organizationId = request.Organization.OrganizationId;
                var profile = await store.ObterAsync(organizationId, perfil, ct);
                if (profile is null || !await store.ContaAtivaExisteAsync(organizationId, userId, ct))
                {
                    return new Saida(ResultadoGestao.NaoEncontrado);
                }

                var set = await policies.ObterAsync(organizationId, ct);
                var mine = await perfisDoUsuario.ListarAsync(request.Organization, request.UserId, ct);
                if (give)
                {
                    var effective = new List<RegraDeAcesso>(profile.Regras);
                    if (profile.AcompanhaModelo && profile.Modelo is not null)
                    {
                        effective.AddRange(await RulesOfKeyAsync(request, set, profile.Modelo, ct) ?? []);
                    }

                    if (!CanGrantAll(set, new Subject(request.UserId, mine), effective))
                    {
                        return new Saida(ResultadoGestao.EscalacaoNegada, "perfil-acima-do-que-possui");
                    }
                }

                var changed = give
                    ? await store.AtribuirAsync(organizationId, userId, perfil, ct)
                    : await store.RemoverAtribuicaoAsync(organizationId, userId, perfil, ct);
                if (changed)
                {
                    await audit.RecordAsync(
                        request,
                        new AuditEntry
                        {
                            Action = give ? PerfisAuditActions.PerfilAtribuir : PerfisAuditActions.PerfilRemoverAtribuicao,
                            ObjectType = "usuario",
                            ObjectId = userId.ToString("D"),
                            After = new JsonObject { ["perfilId"] = ChavesDePerfil.Personalizado(perfil), ["perfil"] = profile.Nome },
                        },
                        ct);
                }

                return new Saida(ResultadoGestao.Ok);
            },
            cancellationToken);
        await RecordRefusalAsync(request, outcome, "usuario", userId.ToString("D"), cancellationToken);
        return outcome.Resultado;
    }

    // The caller's own right to manage, asked to THE engine like any other decision (route-level: no operation values).
    private async Task<bool> MayAsync(IRequestContext request, string permission, CancellationToken cancellationToken)
    {
        var mine = await perfisDoUsuario.ListarAsync(request.Organization, request.UserId, cancellationToken);
        var unit = request.Organization.UnitId == Guid.Empty ? (Guid?)null : request.Organization.UnitId;
        var decision = await engine.DecidirAsync(new Subject(request.UserId, mine), permission, new Scope(request.Organization.OrganizationId, unit), new DecisionContext(), cancellationToken);
        return decision.IsAllowed;
    }

    private async Task<IReadOnlyList<RegraDeAcesso>?> RulesOfKeyAsync(IRequestContext request, PolicySet set, string key, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(key, out var id))
        {
            return (await store.ObterAsync(request.Organization.OrganizationId, id, cancellationToken))?.Regras;
        }

        var rules = PolicyEvaluator.RulesOfProfile(set, key);
        return rules.Count == 0
            ? null
            : [.. rules.Select(r => new RegraDeAcesso(r.Effect, r.Permission, r.UnitId, r.MaxPercent, r.MaxValueCents, r.DailyCapCents))];
    }

    /// <summary>A rule scoped to a unit must name a unit of THIS organization (RLS hides the others): without the check the foreign key would surface as a 500.</summary>
    private async Task<bool> UnitsExistAsync(Guid organizationId, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken)
    {
        var units = regras.Where(r => r.UnitId is not null).Select(r => r.UnitId!.Value).Distinct().ToList();
        return units.Count == 0 || await store.UnidadesExistemAsync(organizationId, units, cancellationToken);
    }

    private static bool IsFactoryKey(PolicySet set, string key) => PolicyEvaluator.RulesOfProfile(set, key).Count > 0;

    private static bool Valid(PolicySet set, IReadOnlyList<RegraDeAcesso> regras) =>
        regras.Count <= MaxRules
        && regras.All(r =>
            set.KnownPermissions.Contains(r.Permission ?? string.Empty)
            && (r.Effect == RuleEffect.Allow || (r.MaxPercent is null && r.MaxValueCents is null && r.DailyCapCents is null))
            && (r.MaxPercent is null or (>= 0 and <= 100))
            && (r.MaxValueCents is null or >= 0)
            && (r.DailyCapCents is null or >= 0));

    private static bool CanGrantAll(PolicySet set, Subject actor, IEnumerable<RegraDeAcesso> regras) => regras.All(r => GrantGuard.CanGrant(set, actor, r));

    private static JsonObject Wrap(IEnumerable<RegraDeAcesso> regras) => new() { ["regras"] = Describe(regras) };

    private static JsonArray Describe(IEnumerable<RegraDeAcesso> regras)
    {
        var array = new JsonArray();
        foreach (var r in regras)
        {
            array.Add(new JsonObject
            {
                ["efeito"] = r.Effect == RuleEffect.Allow ? "allow" : "deny",
                ["permissao"] = r.Permission,
                ["unidadeId"] = r.UnitId?.ToString("D"),
                ["maxPercentual"] = r.MaxPercent,
                ["maxValorCentavos"] = r.MaxValueCents,
                ["tetoDiarioCentavos"] = r.DailyCapCents,
            });
        }

        return array;
    }

    // Own rights and escalation are security facts: recorded on their own transaction, AFTER the unit of work ended, so they persist although nothing was changed.
    private Task RecordRefusalAsync(IRequestContext request, Saida outcome, string objectType, string objectId, CancellationToken cancellationToken)
    {
        if (outcome.Resultado is not (ResultadoGestao.AutoPromocao or ResultadoGestao.EscalacaoNegada))
        {
            return Task.CompletedTask;
        }

        return audit.RecordDetachedAsync(
            request,
            new AuditEntry
            {
                Action = PerfisAuditActions.PermissaoNegarEscalada,
                Result = AuditResult.Negado,
                UserId = request.UserId,
                SessionId = request.SessionId,
                ObjectType = objectType,
                ObjectId = objectId.Length > 200 ? objectId[..200] : objectId,
                After = new JsonObject { ["motivo"] = outcome.Motivo },
            },
            cancellationToken);
    }
}
