namespace Flow4Tech.Kernel.Settings;

/// <summary>
/// The system layer: the declared default, overridden by a configuration value when there is one (<see cref="SettingDefinition.SystemConfigPath"/>). A configured value that does not
/// validate stops the host at the first read (the same rule the old direct configuration reads had), it is never silently replaced by the default.
/// <see cref="ResolveAsync"/> here knows no database: it answers the system value. The organization module wraps this class and adds the stored layers.
/// </summary>
public class SystemSettings(Func<SettingDefinition, string?> configured) : ISettings
{
    public string SystemValue(SettingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var raw = configured(definition);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SettingValueRules.Normalize(definition, definition.Default)!;
        }

        return SettingValueRules.Normalize(definition, raw)
            ?? throw new InvalidOperationException($"The configured value of '{definition.Key}' ({definition.SystemConfigPath}) is not valid.");
    }

    public virtual Task<ResolvedSetting> ResolveAsync(SettingDefinition definition, Guid organizationId, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedSetting(definition, SystemValue(definition), SettingLayer.Sistema, false));
}
