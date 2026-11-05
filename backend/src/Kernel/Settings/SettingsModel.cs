namespace Flow4Tech.Kernel.Settings;

/// <summary>The layers a value can come from, from the broadest to the narrowest: the system (code default plus configuration), the organization, the unit (spec 5.7.1).</summary>
public enum SettingLayer
{
    Sistema = 0,
    Organizacao = 1,
    Unidade = 2,
}

/// <summary>The layers an option may be set on. The system layer always exists (the default). A unit layer needs the organization layer: the lock lives there.</summary>
public enum SettingLayers
{
    /// <summary>Only the system value: read by the modules, changed by configuration and deploy.</summary>
    SomenteSistema = 0,
    Organizacao = 1,
    OrganizacaoEUnidade = 2,
}

public enum SettingType
{
    /// <summary>Whole number (<c>long</c>), invariant culture.</summary>
    Inteiro,

    /// <summary>Decimal number, invariant culture, canonical form without trailing zeros.</summary>
    Numero,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Booleano,

    /// <summary>Text up to 200 characters, kept as typed (trimmed).</summary>
    Texto,

    /// <summary>Time of day <c>HH:mm</c>.</summary>
    Hora,
}

/// <summary>Marks a static class whose <c>public static readonly SettingDefinition</c> fields are the options a module declares (spec 5.7.2), collected like the permission catalog.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SettingsCatalogAttribute : Attribute;

/// <summary>
/// An option declared in code: key, type, default, layers where it applies (spec 5.7.2). <see cref="Key"/> is <c>grupo.nome</c> and is never renamed once published (stored
/// versions point at it). <see cref="ConfigPath"/> is the configuration key (environment variable) that overrides the default at the SYSTEM layer; without it the key
/// <c>Flow4Tech:Configuracoes:{Key}</c> is used. <see cref="RequiresEffectiveInstant"/>: the option changes how the operation reads its own past (the operating day), so a change must
/// name the instant it starts to apply; a change never applies before the moment it is made (spec 5.7.4).
/// </summary>
public sealed record SettingDefinition(string Key, SettingType Type, string Default, SettingLayers Layers, string Description)
{
    public decimal? Minimum { get; init; }

    public decimal? Maximum { get; init; }

    /// <summary>Extra check of a normalized value (an IANA time zone, for instance). Null = none.</summary>
    public Func<string, bool>? Validator { get; init; }

    public string? ConfigPath { get; init; }

    public bool RequiresEffectiveInstant { get; init; }

    public bool AllowsOrganization => Layers != SettingLayers.SomenteSistema;

    public bool AllowsUnit => Layers == SettingLayers.OrganizacaoEUnidade;

    public bool Allows(SettingLayer layer) => layer switch
    {
        SettingLayer.Sistema => true,
        SettingLayer.Organizacao => AllowsOrganization,
        SettingLayer.Unidade => AllowsUnit,
        _ => false,
    };

    public string SystemConfigPath => ConfigPath ?? "Flow4Tech:Configuracoes:" + Key;
}

/// <summary>The value of an option for an organization (and unit) at an instant, and where it came from.</summary>
public sealed record ResolvedSetting(SettingDefinition Definition, string Value, SettingLayer Source, bool LockedByOrganization)
{
    public long AsInteger() => long.Parse(Value, System.Globalization.CultureInfo.InvariantCulture);

    public decimal AsDecimal() => decimal.Parse(Value, System.Globalization.CultureInfo.InvariantCulture);

    public bool AsBoolean() => bool.Parse(Value);

    public TimeOnly AsTime() => TimeOnly.ParseExact(Value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// How the other modules read an option (spec 5.7). <see cref="SystemValue"/> is the system layer alone (code default, overridden by configuration): synchronous, no database,
/// for options that exist only there. <see cref="ResolveAsync"/> resolves the layers for an organization and unit AT AN INSTANT: the instant is explicit so that a past instant
/// keeps reading what applied then (a change never retroacts).
/// </summary>
public interface ISettings
{
    string SystemValue(SettingDefinition definition);

    Task<ResolvedSetting> ResolveAsync(SettingDefinition definition, Guid organizationId, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken);
}
