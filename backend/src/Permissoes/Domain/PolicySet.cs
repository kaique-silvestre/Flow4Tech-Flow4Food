namespace Flow4Tech.Permissoes.Domain;

public enum RuleEffect
{
    Allow,
    Deny,
}

/// <summary>
/// One rule of the rules-as-data policy: profile P may (or may not) do permission X, optionally only on unit U, optionally bounded by a
/// per-operation percent / value and a cumulative daily cap (all values in cents). Limits only make sense on Allow rules.
/// </summary>
public sealed record Rule(
    RuleEffect Effect,
    string Profile,
    string Permission,
    Guid? UnitId = null,
    decimal? MaxPercent = null,
    long? MaxValueCents = null,
    long? DailyCapCents = null);

/// <summary>The catalog of known <c>resource:action</c> permissions plus the rules. Rules over an unknown permission are a configuration error.</summary>
public sealed class PolicySet
{
    public PolicySet(IEnumerable<string> knownPermissions, IEnumerable<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(knownPermissions);
        ArgumentNullException.ThrowIfNull(rules);
        KnownPermissions = knownPermissions.ToHashSet(StringComparer.Ordinal);
        Rules = [.. rules];
        foreach (var rule in Rules)
        {
            if (!KnownPermissions.Contains(rule.Permission))
            {
                throw new ArgumentException($"Rule for profile '{rule.Profile}' names permission '{rule.Permission}' that is not in the catalog.", nameof(rules));
            }

            var limited = rule.MaxPercent is not null || rule.MaxValueCents is not null || rule.DailyCapCents is not null;
            if (rule.Effect == RuleEffect.Deny && limited)
            {
                throw new ArgumentException($"Deny rule for '{rule.Profile}' on '{rule.Permission}' carries a limit; limits belong on Allow rules.", nameof(rules));
            }
        }
    }

    public IReadOnlySet<string> KnownPermissions { get; }

    public IReadOnlyList<Rule> Rules { get; }
}
