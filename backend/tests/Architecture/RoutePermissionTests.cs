using Flow4Tech.Api;
using Flow4Tech.Kernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;

namespace Flow4Tech.Architecture.Tests;

[PermissionCatalog]
internal static class FixtureCatalogA
{
    public const string Zeta = "zeta:ler";
    public const string Alpha = "alpha:gravar";
}

[PermissionCatalog]
internal static class FixtureCatalogB
{
    public const string Beta = "beta:ver";
}

[PermissionCatalog]
internal static class FixtureBadName
{
    public const string Bad = "NotAPermission";
}

[PermissionCatalog]
internal static class FixtureDuplicate
{
    public const string Again = "alpha:gravar";
}

internal static class NotACatalog
{
    public const string Ignored = "ignored:thing";
}

/// <summary>Ticket 10: every route declares a catalog permission; the catalog is collected from module code.</summary>
public sealed class RoutePermissionTests
{
    private static readonly HashSet<string> Catalog = [SistemaPermissionsName, "alpha:gravar"];
    private const string SistemaPermissionsName = "sistema:ver-versao";

    private static List<Endpoint> EndpointsOf(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();
        map(app);
        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints)];
    }

    // ---- session requirement (ticket 01 follow-up): every route outside the allowlists needs .RequireSession() ----

    private static string KeyOf(RouteEndpoint endpoint) =>
        $"{(endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { HttpMethods: [var method, ..] } ? method : "ANY")} {endpoint.RoutePattern.RawText}";

    private static List<string> RoutesWithoutSession(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.OfType<RouteEndpoint>()
            .Select(e => (Route: e.RoutePattern.RawText ?? e.DisplayName ?? "<unnamed endpoint>", Endpoint: e))
            .Where(x => !RoutePermissions.Allowlist.Contains(x.Route) && !RoutePermissions.PublicWithoutSession.Contains(KeyOf(x.Endpoint)))
            .Where(x => x.Endpoint.Metadata.GetMetadata<SessionRequiredMetadata>() is null)
            .Select(x => x.Route)
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_route_of_the_real_api_outside_the_allowlists_requires_a_session()
    {
        using var api = new WebApplicationFactory<Program>();
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.Empty(RoutesWithoutSession(endpoints));

        // Not vacuous: the session routes exist and carry the marker; the public allowlist entries are real routes.
        var withSession = endpoints.OfType<RouteEndpoint>().Where(e => e.Metadata.GetMetadata<SessionRequiredMetadata>() is not null)
            .Select(e => e.RoutePattern.RawText).ToList();
        Assert.Contains("/unidades", withSession);
        Assert.Contains("/sessao/unidade-ativa", withSession);
        var all = endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        Assert.Contains("/version", all);
        foreach (var route in new[] { "/login", "/refresh", "/logout", "/sessoes", "/sessoes/todas", "/sessoes/{id:guid}/revogar", "/sessao/trocar-senha" })
        {
            Assert.Contains(route, all);
        }

        // The public routes are exactly these (ticket 04: login, refresh, logout; ticket 09: invitation, recovery and the two redirects of the Google round trip).
        var publicOnes = endpoints.OfType<RouteEndpoint>().Where(e => e.Metadata.GetMetadata<SessionRequiredMetadata>() is null && !RoutePermissions.Allowlist.Contains(e.RoutePattern.RawText ?? ""))
            .Select(KeyOf).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["GET /login/google", "GET /login/google/retorno", "GET /version", "POST /convites/aceitar", "POST /login", "POST /logout", "POST /recuperacao/redefinir", "POST /recuperacao/solicitar", "POST /refresh"],
            publicOnes);
    }

    [Fact]
    public void A_business_route_without_require_session_is_reported()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapGet("/negocio-sem-sessao", () => "x");
            app.MapGet("/negocio-com-sessao", () => "x").RequireSession();
            app.MapGet("/version", () => "x");
            app.MapGet("/health/live", () => "x");
            app.MapPost("/login", () => "x");
            app.MapGet("/login", () => "x"); // same pattern, another verb: not the public one
        });

        Assert.Equal(["/login", "/negocio-sem-sessao"], RoutesWithoutSession(endpoints));
        Assert.Contains("/negocio-sem-sessao", RoutesWithoutSession(endpoints));
    }

    // ---- route enumeration ----

    [Fact]
    public void Every_route_of_the_real_api_declares_a_permission_or_is_allowlisted()
    {
        using var api = new WebApplicationFactory<Program>();
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var catalog = ModuleCatalog.Load(AppContext.BaseDirectory).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(RoutePermissions.Check(endpoints, catalog));

        // Not vacuous: the routes we know exist were enumerated, and /version really carries its declaration.
        var patterns = endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        Assert.Contains("/version", patterns);
        Assert.Contains("/health/live", patterns);
        Assert.Contains("/health/ready", patterns);
        var version = endpoints.OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/version");
        Assert.Equal("sistema:ver-versao", version.Metadata.GetMetadata<PermissionDeclaration>()?.Name);
    }

    [Fact]
    public void A_route_without_a_declared_permission_is_reported_by_its_pattern()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapGet("/violating-fixture", () => "x");
            app.MapGet("/declared-fixture", () => "x").RequirePermission("alpha:gravar");
        });

        var violations = RoutePermissions.Check(endpoints, Catalog);

        var violation = Assert.Single(violations);
        Assert.Contains("/violating-fixture", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_permission_unknown_to_the_catalog_is_reported_and_rejected_at_startup()
    {
        var endpoints = EndpointsOf(app => app.MapGet("/typo-fixture", () => "x").RequirePermission("alpha:gravr"));

        var violation = Assert.Single(RoutePermissions.Check(endpoints, Catalog));
        Assert.Contains("alpha:gravr", violation, StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidOperationException>(() => RoutePermissions.EnsureValid(endpoints, Catalog));
        Assert.Contains("/typo-fixture", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_health_and_openapi_routes_are_allowlisted_by_exact_pattern()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapGet("/health/live", () => "x");
            app.MapGet("/health/ready", () => "x");
            app.MapGet("/openapi/{documentName}.json", () => "x");
            app.MapGet("/health/other", () => "x");
            app.MapGet("/health", () => "x");
        });

        var violations = RoutePermissions.Check(endpoints, Catalog);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains("/health/other", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("'/health'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("POST", "/health/live")]
    [InlineData("DELETE", "/health/live")]
    [InlineData("POST", "/health/ready")]
    [InlineData("DELETE", "/health/ready")]
    [InlineData("POST", "/openapi/{documentName}.json")]
    [InlineData("DELETE", "/openapi/{documentName}.json")]
    [InlineData("PUT", "/health/live")]
    public void The_allowlist_is_GET_only_so_a_write_on_an_allowlisted_pattern_is_reported(string method, string pattern)
    {
        var endpoints = EndpointsOf(app => app.MapMethods(pattern, [method], () => "x"));

        var violation = Assert.Single(RoutePermissions.Check(endpoints, Catalog));
        Assert.Contains(pattern, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_route_that_accepts_any_method_does_not_pass_the_allowlist()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.Map("/health/live", () => "x"); // no HTTP method metadata: answers every verb
            app.MapMethods("/health/ready", ["GET", "POST"], () => "x");
        });

        Assert.Equal(2, RoutePermissions.Check(endpoints, Catalog).Count);
    }

    [Fact]
    public void An_allowlisted_pattern_with_a_declared_permission_is_still_fine_for_any_method()
    {
        var endpoints = EndpointsOf(app => app.MapPost("/health/live", () => "x").RequirePermission("alpha:gravar"));

        Assert.Empty(RoutePermissions.Check(endpoints, Catalog));
    }

    [Fact]
    public void An_invalid_permission_name_on_a_route_is_reported()
    {
        var endpoints = EndpointsOf(app => app.MapGet("/bad-name", () => "x").RequirePermission("NoColon"));

        Assert.Single(RoutePermissions.Check(endpoints, new HashSet<string> { "NoColon" }));
    }

    // ---- catalog collection ----

    [Fact]
    public void Catalog_from_catalog_classes_is_sorted_deduplicated_and_ignores_unmarked_classes()
    {
        var list = PermissionCatalog.FromTypes([typeof(FixtureCatalogB), typeof(NotACatalog), typeof(FixtureCatalogA)]);

        Assert.Equal(["alpha:gravar", "beta:ver", "zeta:ler"], list);
        Assert.Equal(list, PermissionCatalog.FromTypes([typeof(FixtureCatalogA), typeof(FixtureCatalogB)]));
    }

    [Fact]
    public void A_malformed_or_duplicated_permission_in_a_catalog_class_fails_collection()
    {
        Assert.Throws<InvalidOperationException>(() => PermissionCatalog.FromTypes([typeof(FixtureBadName)]));
        Assert.Throws<InvalidOperationException>(() => PermissionCatalog.FromTypes([typeof(FixtureCatalogA), typeof(FixtureDuplicate)]));
    }

    [Theory]
    [InlineData("desconto:conceder", true)]
    [InlineData("sistema:ver-versao", true)]
    [InlineData("Desconto:conceder", false)]
    [InlineData("desconto", false)]
    [InlineData("a:b:c", false)]
    [InlineData(":x", false)]
    [InlineData("", false)]
    public void Permission_names_are_resource_colon_action(string name, bool valid) =>
        Assert.Equal(valid, PermissionCatalog.IsValidName(name));

    [Fact]
    public void The_module_catalog_is_collected_from_module_assemblies_and_matches_the_committed_export()
    {
        var collected = ModuleCatalog.Load(AppContext.BaseDirectory);

        Assert.Contains("sistema:ver-versao", collected);
        Assert.Equal(collected.OrderBy(x => x, StringComparer.Ordinal), collected);

        var exportPath = Path.Combine(ModuleGraph.BackendRoot(), "permissions.catalog.txt");
        var rendered = PermissionCatalog.Render(collected);
        if (Environment.GetEnvironmentVariable("UPDATE_PERMISSION_CATALOG") == "1")
        {
            File.WriteAllText(exportPath, rendered);
        }

        Assert.True(File.Exists(exportPath), "backend/permissions.catalog.txt is missing: run with UPDATE_PERMISSION_CATALOG=1");
        Assert.Equal(rendered, File.ReadAllText(exportPath));
    }

    // ---- ticket 05: a declared permission is decided, so the route must be authenticated ----

    [Fact]
    public void A_route_with_a_permission_but_no_session_is_reported_unless_it_is_one_of_the_public_ones()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapGet("/sem-sessao", () => "x").RequirePermission("alpha:gravar");
            app.MapGet("/com-sessao", () => "x").RequirePermission("alpha:gravar").RequireSession();
            app.MapPost("/login", () => "x").RequirePermission("alpha:gravar");
            app.MapGet("/health/live", () => "x");
        });

        var violation = Assert.Single(RoutePermissions.CheckSessions(endpoints));
        Assert.Contains("/sem-sessao", violation, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => RoutePermissions.EnsureValid(endpoints, Catalog));
    }

    [Fact]
    public void The_real_api_has_no_route_with_a_permission_and_no_session_outside_the_public_ones()
    {
        using var api = new WebApplicationFactory<Program>();
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.Empty(RoutePermissions.CheckSessions(endpoints));
    }

    // ---- ticket 05: the contract exposes the permission of every operation (x-permission) ----

    [Fact]
    public void The_committed_openapi_document_exposes_x_permission_for_every_operation_that_declares_one()
    {
        using var api = new WebApplicationFactory<Program>();
        var declared = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => (Name: e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName, Permission: e.Metadata.GetMetadata<PermissionDeclaration>()?.Name))
            .Where(x => x.Name is not null && x.Permission is not null)
            .ToDictionary(x => x.Name!, x => x.Permission!, StringComparer.Ordinal);
        Assert.NotEmpty(declared);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "modules.dependencies.json")))
        {
            dir = dir.Parent;
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "Api", "openapi.json")));
        var found = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var id = operation.Value.GetProperty("operationId").GetString()!;
                found[id] = operation.Value.TryGetProperty("x-permission", out var permission) ? permission.GetString() : null;
            }
        }

        foreach (var (name, permission) in declared)
        {
            Assert.True(found.ContainsKey(name), $"operation '{name}' is missing from openapi.json (regenerate the contract)");
            Assert.Equal(permission, found[name]);
        }

        // Operations without a declaration (the health probes) carry no x-permission.
        Assert.All(found.Where(f => !declared.ContainsKey(f.Key)), f => Assert.Null(f.Value));
    }
}
