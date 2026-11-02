using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Permissoes.Application;

/// <summary>
/// Port: which OPERATING day is it now for this unit (the key of a cumulative cap, ADR-006). The future opening-hours feature
/// (roadmap 2.10b) implements this to feed the real opening/closing times.
/// </summary>
public interface IOperatingDayResolver
{
    Task<DateOnly> CurrentAsync(Guid organizationId, Guid? unitId, CancellationToken cancellationToken);
}

/// <summary>
/// Default resolver: a per-unit <see cref="OperatingDayRule"/> from configuration, else <see cref="OperatingDayRule.Default"/> (also for a
/// request with no unit). Time comes from the injected <see cref="IClock"/>. Persisting per-unit rules is a follow-up (roadmap 2.10b).
/// </summary>
public sealed class ConfiguredOperatingDayResolver(IClock clock, IReadOnlyDictionary<Guid, OperatingDayRule>? perUnit = null) : IOperatingDayResolver
{
    public Task<DateOnly> CurrentAsync(Guid organizationId, Guid? unitId, CancellationToken cancellationToken)
    {
        var rule = unitId is { } id && perUnit is not null && perUnit.TryGetValue(id, out var own) ? own : OperatingDayRule.Default;
        return Task.FromResult(rule.DayOf(clock.UtcNow));
    }
}
