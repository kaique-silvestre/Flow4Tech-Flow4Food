using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Flow4Tech.Api;

/// <summary>
/// Spec 5.1.3 gate: no route may take the organization or the unit from the client. Inspects every route's pattern parameters, handler
/// parameters bound from route/query/header/form, and the body type (recursively, through collections), for the names in
/// <see cref="Forbidden"/> (ignoring case, underscores and dashes). Used by the architecture test. A field that names a DESTINATION the user
/// wants to move to (<c>unidadeDestinoId</c>) is not a context field and passes.
/// </summary>
internal static class ClientContextInput
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "ORGANIZACAOID", "ORGANIZATIONID", "ORGID", "UNIDADEID", "UNITID", "TENANTID",
    };

    public static IReadOnlyList<string> Check(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var violations = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "<unnamed endpoint>";
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Report(string name, string where)
            {
                if (reported.Add(name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal)))
                {
                    violations.Add($"Route '{route}' takes '{name}' from the client ({where}); organization and unit come from the session only (spec 5.1).");
                }
            }

            foreach (var parameter in endpoint.RoutePattern.Parameters.Where(p => IsForbidden(p.Name)))
            {
                Report(parameter.Name, "route parameter");
            }

            if (endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType is { } body)
            {
                Walk(body, "body", Report, []);
            }

            foreach (var parameter in endpoint.Metadata.GetMetadata<MethodInfo>()?.GetParameters() ?? [])
            {
                CheckParameter(parameter, Report);
            }
        }

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    private static void CheckParameter(ParameterInfo parameter, Action<string, string> report)
    {
        var type = Unwrap(parameter.ParameterType);
        if (parameter.GetCustomAttribute<FromServicesAttribute>() is not null || type.IsInterface || type == typeof(CancellationToken) || type == typeof(HttpContext))
        {
            return;
        }

        var explicitName = parameter.GetCustomAttributes().OfType<IFromHeaderMetadata>().Select(a => a.Name)
            .Concat(parameter.GetCustomAttributes().OfType<IFromQueryMetadata>().Select(a => a.Name))
            .FirstOrDefault(name => name is not null);

        if (IsSimple(type))
        {
            // A simple parameter is bound from the route, the query string or (with an attribute) a header: its name and its explicit name count.
            if (IsForbidden(parameter.Name))
            {
                report(parameter.Name!, "route/query parameter");
            }

            if (IsForbidden(explicitName))
            {
                report(explicitName!, "query/header");
            }

            return;
        }

        if (parameter.GetCustomAttribute<AsParametersAttribute>() is not null
            || parameter.GetCustomAttributes().Any(a => a is IFromQueryMetadata or IFromHeaderMetadata or IFromFormMetadata))
        {
            Walk(type, parameter.Name ?? "parameter", report, []);
        }
    }

    private static void Walk(Type type, string where, Action<string, string> report, HashSet<Type> seen)
    {
        type = Unwrap(type);
        if (IsSimple(type) || !seen.Add(type))
        {
            return;
        }

        if (type.IsArray)
        {
            Walk(type.GetElementType()!, where, report, seen);
            return;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                Walk(argument, where, report, seen);
            }

            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var wire = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (IsForbidden(property.Name))
            {
                report(property.Name, where);
            }

            if (IsForbidden(wire))
            {
                report(wire!, where);
            }

            Walk(property.PropertyType, where + "." + property.Name, report, seen);
        }
    }

    private static bool IsForbidden(string? name) =>
        name is not null && Forbidden.Contains(name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant());

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsSimple(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid) || type == typeof(decimal)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan)
        || type == typeof(Uri) || type == typeof(object);
}
