using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Permissoes.Domain;

/// <summary>
/// A rule as an administrator proposes it, before it belongs to a profile or to a person: effect, permission, optional unit and limits (cents). The profile it ends up in
/// is decided by the caller (<see cref="ToRule"/>).
/// </summary>
public sealed record RegraDeAcesso(
    RuleEffect Effect,
    string Permission,
    Guid? UnitId = null,
    decimal? MaxPercent = null,
    long? MaxValueCents = null,
    long? DailyCapCents = null)
{
    public Rule ToRule(string profile) => new(Effect, profile, Permission, UnitId, MaxPercent, MaxValueCents, DailyCapCents);
}

/// <summary>
/// Anti-escalation (spec 5.4.5): nobody grants what they do not hold. Pure: it reads the ACTOR's own rules from the same <see cref="PolicySet"/> the engine uses, so the answer
/// can never drift from what the actor can really do. An allow can be handed on only when the actor has an allow that covers the unit and is at least as generous in every limit
/// (a limit the actor lacks must not be dropped, a limit the actor has must not be raised), and no deny of the actor on that permission reaches the proposed unit. A deny is
/// always grantable: it only narrows. An unknown permission is never grantable.
/// </summary>
public static class GrantGuard
{
    public static bool CanGrant(PolicySet policies, Subject actor, RegraDeAcesso proposed)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(proposed);
        var permission = proposed.Permission ?? string.Empty;
        if (!policies.KnownPermissions.Contains(permission))
        {
            return false;
        }

        if (proposed.Effect == RuleEffect.Deny)
        {
            return true;
        }

        var theirs = PolicyEvaluator.RulesHeldBy(policies, actor, permission);

        // An actor deny blocks the grant when it reaches the proposed unit; a proposal for every unit is reached by a deny on any unit.
        if (theirs.Any(r => r.Effect == RuleEffect.Deny && (r.UnitId is null || proposed.UnitId is null || r.UnitId == proposed.UnitId)))
        {
            return false;
        }

        return theirs.Any(r => r.Effect == RuleEffect.Allow && CoversUnit(r, proposed) && AtLeastAsGenerous(r, proposed));
    }

    private static bool CoversUnit(Rule held, RegraDeAcesso proposed) => held.UnitId is null || held.UnitId == proposed.UnitId;

    private static bool AtLeastAsGenerous(Rule held, RegraDeAcesso proposed) =>
        Within(held.MaxPercent, proposed.MaxPercent) && Within(held.MaxValueCents, proposed.MaxValueCents) && Within(held.DailyCapCents, proposed.DailyCapCents);

    // The proposal may be as limited as the holder or more; it may not be less limited (a missing limit is unlimited).
    private static bool Within<T>(T? held, T? proposed)
        where T : struct, IComparable<T> =>
        held is null || (proposed is not null && proposed.Value.CompareTo(held.Value) <= 0);
}
