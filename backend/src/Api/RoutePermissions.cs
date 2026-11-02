using System.Reflection;
using System.Runtime.Loader;
using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Api;

internal static class RoutePermissionExtensions
{
    /// <summary>Declares the <c>resource:action</c> permission of a route as endpoint metadata. Every route needs it (see <see cref="RoutePermissions"/>).</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new PermissionDeclaration(permission)));
        return builder;
    }
}

/// <summary>
/// Route permission gate (ticket 10). Every route must declare a catalog permission through <c>RequirePermission</c>; the only exceptions are
/// the explicit <see cref="Allowlist"/> (GET on an exact pattern). Used at startup (<see cref="EnsureValid"/>, the host refuses to start) and by the architecture test.
/// </summary>
internal static class RoutePermissions
{
    /// <summary>
    /// Routes that may exist without a permission, matched by exact route pattern: the two health probes (they must answer before anyone is
    /// signed in) and the OpenAPI document (mapped in Development only; it describes the contract, not business data).
    /// </summary>
    public static readonly IReadOnlySet<string> Allowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "/health/live",
        "/health/ready",
        "/openapi/{documentName}.json",
    };

    public static IReadOnlyList<string> Check(IEnumerable<Endpoint> endpoints, IReadOnlySet<string> catalog)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(catalog);
        var violations = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName ?? "<unnamed endpoint>";
            if (Allowlist.Contains(route) && IsGetOnly(endpoint))
            {
                continue;
            }

            var name = endpoint.Metadata.GetMetadata<PermissionDeclaration>()?.Name;
            if (name is null)
            {
                violations.Add($"Route '{route}' declares no permission: add .RequirePermission(\"resource:action\").");
            }
            else if (!PermissionCatalog.IsValidName(name))
            {
                violations.Add($"Route '{route}' declares '{name}', which is not a valid permission name (resource:action).");
            }
            else if (!catalog.Contains(name))
            {
                violations.Add($"Route '{route}' declares permission '{name}', which is unknown to the catalog.");
            }
        }

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    /// <summary>The allowlist is (GET, exact pattern): a route with no method metadata (any verb) or any other verb never qualifies.</summary>
    private static bool IsGetOnly(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { HttpMethods: { } methods }
        && methods.Count == 1
        && string.Equals(methods.Single(), HttpMethods.Get, StringComparison.OrdinalIgnoreCase);

    public static void EnsureValid(IEnumerable<Endpoint> endpoints, IReadOnlySet<string> catalog)
    {
        var violations = Check(endpoints, catalog);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("Route permission check failed:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }
}

/// <summary>Collects the permission catalog from the module assemblies (<c>Flow4Tech.&lt;Module&gt;.&lt;Layer&gt;.dll</c>) next to the host.</summary>
internal static class ModuleCatalog
{
    private static readonly string[] Layers = ["Domain", "Application", "Infrastructure", "Interface"];

    public static IReadOnlyList<string> Load(string directory)
    {
        var assemblies = new List<Assembly>();
        foreach (var path in Directory.EnumerateFiles(directory, "Flow4Tech.*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var parts = name.Split('.');
            if (parts.Length != 3 || !Layers.Contains(parts[2], StringComparer.Ordinal))
            {
                continue;
            }

            assemblies.Add(AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name)
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path));
        }

        return PermissionCatalog.Collect(assemblies);
    }
}
