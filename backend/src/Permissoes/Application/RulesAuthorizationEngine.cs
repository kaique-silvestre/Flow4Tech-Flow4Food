using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Permissoes.Application;

/// <summary>
/// Port: atomically adds <paramref name="cents"/> to the (user, permission, day) counter only if the total stays within
/// <paramref name="capCents"/>, in one step (check and update cannot be separated). True when the amount was reserved.
/// </summary>
public interface ICumulativeLimitStore
{
    Task<bool> TryConsumeAsync(Guid organizationId, Guid? unitId, Guid userId, string permission, DateOnly day, long cents, long capCents, CancellationToken cancellationToken);
}

/// <summary>The rules-as-data engine: pure evaluation (Domain) plus atomic consumption of cumulative limits through <see cref="ICumulativeLimitStore"/>.</summary>
public sealed class RulesAuthorizationEngine(PolicySet policies, ICumulativeLimitStore limits, IOperatingDayResolver days) : IAuthorizationEngine
{
    public async Task<Decision> DecidirAsync(Subject who, string permission, Scope where, DecisionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(context);
        var evaluation = PolicyEvaluator.Evaluate(policies, who, permission, where, context);
        if (evaluation.Effect == Effect.Deny)
        {
            return Decision.Deny(evaluation.Reason);
        }

        if (evaluation.Limited is not { DailyCapCents: { } cap })
        {
            return Decision.Allow(evaluation.Reason);
        }

        // The operating day depends on the unit's rule: without a unit there is no honest day to count in, so fail closed (nothing consumed).
        if (where.UnitId is null)
        {
            return Decision.Deny("unit-required");
        }

        // The evaluator guarantees Value is present and within the cap; the day is the unit's operating day from the server clock, never caller data.
        var day = await days.CurrentAsync(where.OrganizationId, where.UnitId, cancellationToken);
        var reserved = await limits.TryConsumeAsync(where.OrganizationId, where.UnitId, who.UserId, permission, day, context.Value!.Value.Cents, cap, cancellationToken);
        return reserved ? Decision.Allow("allow") : Decision.Deny("daily-cap-exceeded");
    }
}
