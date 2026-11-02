using System.Reflection;
using System.Text;

namespace Flow4Tech.Kernel.Authorization;

/// <summary>Marks a static class whose public <c>const string</c> fields are the <c>resource:action</c> permissions its module defines.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PermissionCatalogAttribute : Attribute;

/// <summary>Endpoint metadata: the permission a route declares (<c>resource:action</c>).</summary>
public sealed record PermissionDeclaration(string Name);

/// <summary>
/// The permission catalog (ADR-006, S8): not a hand-kept list but whatever the modules declare in <see cref="PermissionCatalogAttribute"/>
/// classes. The result is sorted ordinally and free of duplicates, so it is deterministic and can be exported and diffed.
/// </summary>
public static class PermissionCatalog
{
    /// <summary><c>resource:action</c>: two non-empty parts of lowercase letters, digits and hyphens, each starting with a letter.</summary>
    public static bool IsValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var parts = name.Split(':');
        return parts.Length == 2 && parts.All(IsValidPart);
    }

    public static IReadOnlyList<string> FromTypes(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var owners = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in types.Where(t => t.IsDefined(typeof(PermissionCatalogAttribute), inherit: false)))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            {
                var name = (string)field.GetRawConstantValue()!;
                if (!IsValidName(name))
                {
                    throw new InvalidOperationException($"{type.FullName}.{field.Name} = '{name}' is not a valid permission name (resource:action).");
                }

                if (!owners.TryAdd(name, type))
                {
                    throw new InvalidOperationException($"Permission '{name}' is declared twice ({owners[name].FullName} and {type.FullName}).");
                }
            }
        }

        return [.. owners.Keys.Order(StringComparer.Ordinal)];
    }

    public static IReadOnlyList<string> Collect(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return FromTypes(assemblies.SelectMany(a => a.GetTypes()));
    }

    /// <summary>The exported form: one name per line, LF endings, trailing newline.</summary>
    public static string Render(IReadOnlyList<string> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var text = new StringBuilder();
        foreach (var name in catalog)
        {
            text.Append(name).Append('\n');
        }

        return text.ToString();
    }

    private static bool IsValidPart(string part) =>
        part.Length > 0
        && part[0] is >= 'a' and <= 'z'
        && part.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
