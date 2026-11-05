using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Flow4Tech.Kernel.Settings;

/// <summary>
/// The catalog of options (spec 5.7.2): whatever the modules declare in <see cref="SettingsCatalogAttribute"/> classes, collected from the assemblies like the permission catalog.
/// Sorted by key, free of duplicates, every default valid for its own definition.
/// </summary>
public static partial class SettingsCatalog
{
    [GeneratedRegex("^[a-z][a-z0-9-]*\\.[a-z][a-z0-9-]*$")]
    private static partial Regex KeyFormat();

    public static bool IsValidKey(string? key) => key is { Length: <= 80 } && KeyFormat().IsMatch(key);

    public static IReadOnlyList<SettingDefinition> FromTypes(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var owners = new Dictionary<string, (SettingDefinition Definition, Type Owner)>(StringComparer.Ordinal);
        foreach (var type in types.Where(t => t.IsDefined(typeof(SettingsCatalogAttribute), inherit: false)))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(f => f.FieldType == typeof(SettingDefinition)))
            {
                var definition = (SettingDefinition)field.GetValue(null)!;
                Validate(definition, $"{type.FullName}.{field.Name}");
                if (!owners.TryAdd(definition.Key, (definition, type)))
                {
                    throw new InvalidOperationException($"Setting '{definition.Key}' is declared twice ({owners[definition.Key].Owner.FullName} and {type.FullName}).");
                }
            }
        }

        return [.. owners.Values.Select(o => o.Definition).OrderBy(d => d.Key, StringComparer.Ordinal)];
    }

    public static IReadOnlyList<SettingDefinition> Collect(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return FromTypes(assemblies.SelectMany(a => a.GetTypes()));
    }

    /// <summary>A malformed declaration stops the host and the architecture test: key format, a valid default, layers that make sense, a range that is a range.</summary>
    public static void Validate(SettingDefinition definition, string where)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsValidKey(definition.Key))
        {
            throw new InvalidOperationException($"{where}: '{definition.Key}' is not a valid setting key (grupo.nome, lower case).");
        }

        if (!Enum.IsDefined(definition.Layers))
        {
            throw new InvalidOperationException($"{where}: unknown layers.");
        }

        if (string.IsNullOrWhiteSpace(definition.Description))
        {
            throw new InvalidOperationException($"{where}: describe the option.");
        }

        if (definition.Minimum > definition.Maximum)
        {
            throw new InvalidOperationException($"{where}: the minimum is above the maximum.");
        }

        if (SettingValueRules.Normalize(definition, definition.Default) is null)
        {
            throw new InvalidOperationException($"{where}: the default '{definition.Default}' is not valid for the option itself.");
        }

        if (definition.RequiresEffectiveInstant && !definition.AllowsOrganization)
        {
            throw new InvalidOperationException($"{where}: an option only the system sets cannot require an effective instant.");
        }
    }

    /// <summary>The exported form, one option per line: <c>key|type|layers|default</c> (LF endings, trailing newline). A key is never renamed once published.</summary>
    public static string Render(IEnumerable<SettingDefinition> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var text = new StringBuilder();
        foreach (var d in catalog.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            text.Append(d.Key).Append('|').Append(d.Type).Append('|').Append(d.Layers).Append('|').Append(d.Default).Append('\n');
        }

        return text.ToString();
    }
}
