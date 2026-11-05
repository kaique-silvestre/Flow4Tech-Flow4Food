using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Permissoes.Domain;

/// <summary>Result of the pure (no I/O) evaluation. <see cref="Limited"/> is the allow rule whose cumulative cap still has to be consumed.</summary>
public sealed record Evaluation(Effect Effect, string Reason, Rule? Limited = null);

/// <summary>
/// Precedence: unknown permission denies; any matching Deny rule (across all the subject's profiles) denies; otherwise an Allow rule whose
/// scope and per-operation limits hold allows; otherwise default deny. Anything missing for a limit (value, percent) fails closed.
/// </summary>
public static class PolicyEvaluator
{
    /// <summary>Limits are expressed in cents of this currency only; a value in any other currency fails closed (ADR-006).</summary>
    public const string LimitCurrency = "BRL";

    /// <summary>The rules of <paramref name="permission"/> that the subject holds through any of its profiles (the only other reader of <c>Subject.Profiles</c>, for the anti-escalation check).</summary>
    public static IReadOnlyList<Rule> RulesHeldBy(PolicySet policies, Subject who, string permission)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(who);
        return [.. policies.Rules.Where(r => r.Permission == permission && who.Profiles.Contains(r.Profile))];
    }

    /// <summary>The rules that belong to one profile key (to copy a factory or custom profile).</summary>
    public static IReadOnlyList<Rule> RulesOfProfile(PolicySet policies, string key)
    {
        ArgumentNullException.ThrowIfNull(policies);
        return [.. policies.Rules.Where(r => string.Equals(r.Profile, key, StringComparison.Ordinal))];
    }

    public static Evaluation Evaluate(PolicySet policies, Subject who, string permission, Scope where, DecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(context);

        if (!policies.KnownPermissions.Contains(permission ?? string.Empty))
        {
            return new Evaluation(Effect.Deny, "unknown-permission");
        }

        if (where.OrganizationId == Guid.Empty)
        {
            return new Evaluation(Effect.Deny, "invalid-scope");
        }

        var applicable = policies.Rules
            .Where(r => r.Permission == permission && who.Profiles.Contains(r.Profile) && InUnit(r, where))
            .ToList();

        if (applicable.Exists(r => r.Effect == RuleEffect.Deny))
        {
            return new Evaluation(Effect.Deny, "explicit-deny");
        }

        var allowing = applicable.Where(r => r.Effect == RuleEffect.Allow).ToList();
        if (allowing.Count == 0)
        {
            return new Evaluation(Effect.Deny, "default-deny");
        }

        var withinLimits = allowing.Where(r => WithinPerOperationLimits(r, context)).ToList();
        if (withinLimits.Count == 0)
        {
            var unsupportedCurrency = context.Value is { Currency: not LimitCurrency } && allowing.Exists(NeedsValue);
            return new Evaluation(Effect.Deny, unsupportedCurrency ? "currency-not-supported" : "limit-exceeded");
        }

        // An uncapped allow wins over a capped one (nothing to consume); otherwise the first capped rule.
        var uncapped = withinLimits.Find(r => r.DailyCapCents is null);
        return uncapped is not null
            ? new Evaluation(Effect.Allow, "allow")
            : new Evaluation(Effect.Allow, "allow", withinLimits[0]);
    }

    // Fail closed both ways: an Allow scoped to a unit needs that exact unit; a Deny scoped to a unit also applies when the request names no unit.
    private static bool InUnit(Rule rule, Scope where) =>
        rule.UnitId is null || rule.UnitId == where.UnitId || (rule.Effect == RuleEffect.Deny && where.UnitId is null);

    private static bool NeedsValue(Rule rule) => rule.MaxValueCents is not null || rule.DailyCapCents is not null;

    private static bool WithinPerOperationLimits(Rule rule, DecisionContext context)
    {
        if (rule.MaxPercent is { } maxPercent && !(context.Percent is { } percent && percent >= 0 && percent <= maxPercent))
        {
            return false;
        }

        // Value must be present, non-negative and in BRL for any value-based limit (per-operation or cumulative).
        if (NeedsValue(rule) && context.Value is not { Cents: >= 0, Currency: LimitCurrency })
        {
            return false;
        }

        if (rule.MaxValueCents is { } maxValue && context.Value!.Value.Cents > maxValue)
        {
            return false;
        }

        return rule.DailyCapCents is not { } cap || context.Value!.Value.Cents <= cap;
    }
}
