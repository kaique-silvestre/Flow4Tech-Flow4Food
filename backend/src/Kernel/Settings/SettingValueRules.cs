using System.Globalization;

namespace Flow4Tech.Kernel.Settings;

/// <summary>The rules every stored or configured value goes through: parse by type, range, the option's own check, one canonical text form.</summary>
public static class SettingValueRules
{
    public const int MaxTextLength = 200;

    /// <summary>Validates <paramref name="raw"/> for <paramref name="definition"/> and returns its canonical text; null (and no exception) when it is not acceptable.</summary>
    public static string? Normalize(SettingDefinition definition, string? raw)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (raw is null)
        {
            return null;
        }

        var text = raw.Trim();
        string? canonical = definition.Type switch
        {
            SettingType.Inteiro => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? whole.ToString(CultureInfo.InvariantCulture) : null,
            SettingType.Numero => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                ? (number / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)
                : null,
            SettingType.Booleano => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ? "true" : string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ? "false" : null,
            SettingType.Texto => text.Length is > 0 and <= MaxTextLength ? text : null,
            SettingType.Hora => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : null,
            _ => null,
        };

        if (canonical is null)
        {
            return null;
        }

        if ((definition.Minimum is not null || definition.Maximum is not null) && (definition.Type is SettingType.Inteiro or SettingType.Numero))
        {
            var value = decimal.Parse(canonical, CultureInfo.InvariantCulture);
            if (value < definition.Minimum || value > definition.Maximum)
            {
                return null;
            }
        }

        return definition.Validator is { } check && !check(canonical) ? null : canonical;
    }
}
