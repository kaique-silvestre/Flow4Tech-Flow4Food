namespace Flow4Tech.Kernel.Authorization;

/// <summary>
/// The single authorization decision point (ADR-006). Every route/handler asks this and nothing else; the engine behind it
/// (rules-as-data today, Cedar/Casbin/other tomorrow) is swappable. Order of precedence: explicit deny, then allow, then default deny.
/// </summary>
public interface IAuthorizationEngine
{
    /// <summary>
    /// Decides whether <paramref name="who"/> may do <paramref name="permission"/> (<c>resource:action</c>) <paramref name="where"/>, given
    /// <paramref name="context"/>. Never throws for a denial: a denial is a <see cref="Decision"/>. When an allow is subject to a cumulative
    /// limit, the engine consumes that budget atomically as part of deciding (an Allow means "go ahead, the budget is already reserved").
    /// </summary>
    Task<Decision> DecidirAsync(Subject who, string permission, Scope where, DecisionContext context, CancellationToken cancellationToken = default);
}

/// <summary>Who asks: the authenticated user and the profiles they hold.</summary>
public sealed record Subject(Guid UserId, IReadOnlyList<string> Profiles);

/// <summary>Where: organization (tenant), optionally the unit and a specific object.</summary>
public sealed record Scope(Guid OrganizationId, Guid? UnitId = null, string? ObjectId = null);

/// <summary>
/// Facts about the operation. The server fills these from the REAL operation (computed value, percent from the order), never from what
/// the client claims. There is deliberately no time here: windows (the daily cap) use the engine's own <see cref="IClock"/>.
/// </summary>
public sealed record DecisionContext(Money? Value = null, decimal? Percent = null, string? Device = null, string? SessionType = null);

public enum Effect
{
    Deny,
    Allow,
}

/// <summary>The outcome plus a stable machine-readable reason (for audit and tests).</summary>
public sealed record Decision(Effect Effect, string Reason)
{
    public bool IsAllowed => Effect == Effect.Allow;

    public static Decision Allow(string reason) => new(Effect.Allow, reason);

    public static Decision Deny(string reason) => new(Effect.Deny, reason);
}
