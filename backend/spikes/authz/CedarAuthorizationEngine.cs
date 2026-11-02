using System.Globalization;
using System.Text;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using MonoCloud.Cedar;
using MonoCloud.Cedar.Model;
using MonoCloud.Cedar.Model.Entity;
using MonoCloud.Cedar.Model.Policy;
using MonoCloud.Cedar.Value;
using KDecision = Flow4Tech.Kernel.Authorization.Decision;
using KEffect = Flow4Tech.Kernel.Authorization.Effect;
using RuleEffect = Flow4Tech.Permissoes.Domain.RuleEffect;
using KPolicySet = Flow4Tech.Permissoes.Domain.PolicySet;

namespace Authz.Spike;

/// <summary>
/// Cedar (native Rust engine through MonoCloud.Cedar) behind the same interface. Rules-as-data are TRANSLATED to Cedar text:
/// profiles are parent entities of the user (<c>principal in Profile::"x"</c>), the unit is the resource, per-operation limits are
/// <c>when</c> conditions over the context (Cedar has no decimals worth using here: percent travels as basis points).
/// Cedar cannot keep state, so the cumulative cap is the hybrid part: Cedar decides, the shared store consumes.
/// </summary>
public sealed class CedarAuthorizationEngine : IAuthorizationEngine
{
    private readonly KPolicySet _policies;
    private readonly ICumulativeLimitStore _limits;
    private readonly Flow4Tech.Kernel.IClock _clock;
    private readonly MonoCloud.Cedar.Model.Policy.PolicySet _cedar;
    private readonly BasicAuthorizationEngine _engine = new();

    public CedarAuthorizationEngine(KPolicySet policies, ICumulativeLimitStore limits, Flow4Tech.Kernel.IClock clock)
    {
        _policies = policies;
        _limits = limits;
        _clock = clock;
        _cedar = MonoCloud.Cedar.Model.Policy.PolicySet.ParsePolicies(Translate(policies));
    }

    /// <summary>The Cedar source generated from the rules (ids are policy0..N in rule order).</summary>
    public static string Translate(KPolicySet policies)
    {
        var text = new StringBuilder();
        foreach (var r in policies.Rules)
        {
            var effect = r.Effect == RuleEffect.Allow ? "permit" : "forbid";
            // Allow needs the exact unit; Deny also matches a request with no unit (Unit::"none"): fail closed.
            var resource = r.UnitId is { } u && r.Effect == RuleEffect.Allow ? $"resource == Unit::\"{u}\"" : "resource";
            text.Append(CultureInfo.InvariantCulture, $"{effect}(principal in Profile::\"{r.Profile}\", action == Action::\"{r.Permission}\", {resource})");
            var conditions = new List<string>();
            if (r.UnitId is { } du && r.Effect == RuleEffect.Deny)
            {
                conditions.Add($"(resource == Unit::\"{du}\" || resource == Unit::\"none\")");
            }

            if (r.MaxPercent is { } pct)
            {
                conditions.Add($"context has percentBp && context.percentBp >= 0 && context.percentBp <= {(long)(pct * 100)}");
            }

            if (r.MaxValueCents is { } max)
            {
                conditions.Add($"context has valueCents && context.valueCents >= 0 && context.valueCents <= {max}");
            }

            if (r.DailyCapCents is { } cap)
            {
                conditions.Add($"context has valueCents && context.valueCents >= 0 && context.valueCents <= {cap}");
            }

            text.AppendLine(conditions.Count == 0 ? ";" : " when { " + string.Join(" && ", conditions) + " };");
        }

        return text.ToString();
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

        var user = new EntityUID(EntityTypeName.Parse("User"), who.UserId.ToString());
        var parents = who.Profiles.Select(p => new EntityUID(EntityTypeName.Parse("Profile"), p)).ToHashSet();
        var entities = new Entities(new HashSet<Entity> { new(user, parents) });
        var ctx = new Dictionary<string, MonoCloud.Cedar.Value.Value>();
        if (context.Percent is { } percent)
        {
            ctx["percentBp"] = new PrimLong((long)(percent * 100));
        }

        if (context.Value is { } value)
        {
            ctx["valueCents"] = new PrimLong(value.Cents);
        }

        var request = new AuthorizationRequest(
            user,
            new EntityUID(EntityTypeName.Parse("Action"), permission!),
            new EntityUID(EntityTypeName.Parse("Unit"), where.UnitId?.ToString() ?? "none"),
            ctx);
        var response = _engine.IsAuthorized(request, _cedar, entities);
        var success = response.Success!;
        var reasons = success.GetReason();
        if (!success.IsAllowed())
        {
            return KDecision.Deny(reasons.Count > 0 ? "explicit-deny" : "default-deny");
        }

        var matched = reasons.Select(id => _policies.Rules[int.Parse(id["policy".Length..], CultureInfo.InvariantCulture)]).ToList();
        if (matched.Exists(r => r.DailyCapCents is null))
        {
            return KDecision.Allow("allow");
        }

        var capped = matched[0];
        var day = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var reserved = await _limits.TryConsumeAsync(where.OrganizationId, where.UnitId, who.UserId, permission!, day, context.Value!.Value.Cents, capped.DailyCapCents!.Value, cancellationToken);
        return reserved ? KDecision.Allow("allow") : KDecision.Deny("daily-cap-exceeded");
    }
}
