using Casbin;
using Casbin.Model;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using KDecision = Flow4Tech.Kernel.Authorization.Decision;
using RuleEffect = Flow4Tech.Permissoes.Domain.RuleEffect;
using KPolicySet = Flow4Tech.Permissoes.Domain.PolicySet;

namespace Authz.Spike;

/// <summary>
/// Casbin.NET behind the same interface (ABAC model with an expression matcher; one enforce per profile because Casbin has no multi-subject
/// request). Per-operation limits live in the matcher; the cumulative cap is again the hybrid part (the matched policy row names the cap).
/// </summary>
public sealed class CasbinAuthorizationEngine : IAuthorizationEngine
{
    private const string ModelText = """
        [request_definition]
        r = sub, act, unit, pct, val

        [policy_definition]
        p = sub, act, unit, maxpct, maxval, cap, eft

        [policy_effect]
        e = some(where (p.eft == allow)) && !some(where (p.eft == deny))

        [matchers]
        m = r.sub == p.sub && r.act == p.act && (p.unit == "*" || r.unit == p.unit || (p.eft == "deny" && r.unit == "none")) && (p.eft == "deny" || ((p.maxpct == "-" || (r.pct >= 0 && r.pct <= Convert.ToInt64(p.maxpct))) && (p.maxval == "-" || (r.val >= 0 && r.val <= Convert.ToInt64(p.maxval))) && (p.cap == "-" || (r.val >= 0 && r.val <= Convert.ToInt64(p.cap)))))
        """;

    private readonly KPolicySet _policies;
    private readonly ICumulativeLimitStore _limits;
    private readonly IOperatingDayResolver _days;
    private readonly Enforcer _enforcer;

    public CasbinAuthorizationEngine(KPolicySet policies, ICumulativeLimitStore limits, IOperatingDayResolver days)
    {
        _policies = policies;
        _limits = limits;
        _days = days;
        _enforcer = new Enforcer(DefaultModel.CreateFromText(ModelText));
        foreach (var r in policies.Rules)
        {
            _enforcer.AddPolicy(
                r.Profile, r.Permission, r.UnitId?.ToString() ?? "*",
                r.MaxPercent is { } p ? ((long)(p * 100)).ToString() : "-",
                r.MaxValueCents?.ToString() ?? "-",
                r.DailyCapCents?.ToString() ?? "-",
                r.Effect == RuleEffect.Allow ? "allow" : "deny");
        }
    }

    public async Task<KDecision> DecidirAsync(Subject who, string permission, Scope where, DecisionContext context, CancellationToken cancellationToken = default)
    {
        if (!_policies.KnownPermissions.Contains(permission ?? string.Empty))
        {
            return KDecision.Deny("unknown-permission");
        }

        if (where.OrganizationId == Guid.Empty)
        {
            return KDecision.Deny("invalid-scope");
        }

        if (context.Value is { Currency: not PolicyEvaluator.LimitCurrency }
            && _policies.Rules.Any(r => r.Effect == RuleEffect.Allow && r.Permission == permission && who.Profiles.Contains(r.Profile) && (r.MaxValueCents is not null || r.DailyCapCents is not null)))
        {
            return KDecision.Deny("currency-not-supported");
        }

        var pct = context.Percent is { } percent ? (long)(percent * 100) : -1L;
        var val = context.Value is { Currency: PolicyEvaluator.LimitCurrency } brl ? brl.Cents : -1L; // other currency = no usable value (fails closed)
        var unit = where.UnitId?.ToString() ?? "none";
        var allowedRows = new List<IEnumerable<string>>();
        foreach (var profile in who.Profiles)
        {
            var (result, explains) = _enforcer.EnforceEx(profile, permission!, unit, pct, val);
            if (explains.Any(row => row.Last() == "deny"))
            {
                return KDecision.Deny("explicit-deny");
            }

            if (result)
            {
                allowedRows.AddRange(explains);
            }
        }

        if (allowedRows.Count == 0)
        {
            return KDecision.Deny("default-deny");
        }

        // Row layout: sub, act, unit, maxpct, maxval, cap, eft.
        var uncapped = allowedRows.Exists(row => row.ElementAt(5) == "-");
        if (uncapped)
        {
            return KDecision.Allow("allow");
        }

        var cap = long.Parse(allowedRows[0].ElementAt(5), System.Globalization.CultureInfo.InvariantCulture);
        if (where.UnitId is null)
        {
            return KDecision.Deny("unit-required");
        }

        var day = await _days.CurrentAsync(where.OrganizationId, where.UnitId, cancellationToken);
        var reserved = await _limits.TryConsumeAsync(where.OrganizationId, where.UnitId, who.UserId, permission!, day, context.Value!.Value.Cents, cap, cancellationToken);
        return reserved ? KDecision.Allow("allow") : KDecision.Deny("daily-cap-exceeded");
    }
}
