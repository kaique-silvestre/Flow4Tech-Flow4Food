using System.Text.RegularExpressions;
using Flow4Tech.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Architecture.Tests;

/// <summary>
/// Ticket 01 (spec 5.1.3): organization and unit come from the session, never from the client. No route may take an
/// <c>organizacaoId</c>/<c>unidadeId</c> (or a synonym) from the route, query, header, form or body; and business code never builds the context.
/// </summary>
public sealed partial class ClientContextInputTests
{
    private static List<Endpoint> EndpointsOf(Action<WebApplication> map)
    {
        var app = WebApplication.CreateSlimBuilder().Build();
        map(app);
        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints)];
    }

    internal sealed record BodyWithOrganization(string Nome, Guid OrganizacaoId);

    internal sealed record BodyWithUnit(string Nome, Guid UnidadeId);

    internal sealed record BodyWithSnakeCase(string Nome, [property: System.Text.Json.Serialization.JsonPropertyName("organizacao_id")] Guid Outro);

    internal sealed record Wrapper(string Nome, BodyWithOrganization Dentro);

    internal sealed record WrapperList(string Nome, List<BodyWithUnit> Itens);

    internal sealed record BodyOk(string Nome, Guid UnidadeDestinoId);

    internal sealed record QueryFilter
    {
        public Guid? TenantId { get; init; }
    }

    internal sealed record Envelope<T>(T Data, Guid OrganizacaoId);

    internal sealed record Page<T>(List<T> Items, int Total);

    internal sealed class Node
    {
        public Node? Next { get; init; }

        public List<Node> Children { get; init; } = [];

        public string Nome { get; init; } = "";
    }

    internal sealed class BodyWithField
    {
#pragma warning disable CA1051, CS0649
        public Guid OrganizacaoId;
#pragma warning restore CA1051, CS0649
    }

    internal sealed record BodyWithSynonyms(Guid Org, Guid Tenant, Guid Unidade);

    [Fact]
    public void A_generic_body_is_inspected_for_its_own_properties_not_only_its_type_arguments()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapPost("/env", (Envelope<BodyOk> body) => body.OrganizacaoId);
            app.MapPost("/page", (Page<BodyWithUnit> body) => body.Total);
        });

        var violations = ClientContextInput.Check(endpoints);

        Assert.Contains(violations, v => v.Contains("/env", StringComparison.Ordinal) && v.Contains("OrganizacaoId", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("/page", StringComparison.Ordinal) && v.Contains("UnidadeId", StringComparison.Ordinal));
    }

    [Fact]
    public void A_self_referencing_body_terminates_and_a_clean_generic_passes()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapPost("/node", (Node body) => body.Nome);
            app.MapPost("/page-ok", (Page<BodyOk> body) => body.Total);
        });

        Assert.Empty(ClientContextInput.Check(endpoints));
    }

    [Fact]
    public void A_public_field_is_inspected_like_a_property()
    {
        var endpoints = EndpointsOf(app => app.MapPost("/field", (BodyWithField body) => body.GetHashCode()));

        Assert.Contains(ClientContextInput.Check(endpoints), v => v.Contains("OrganizacaoId", StringComparison.Ordinal));
    }

    [Fact]
    public void An_explicit_binding_name_on_a_simple_parameter_is_flagged_for_every_source()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapPost("/form", ([FromForm(Name = "organizacaoId")] Guid a) => a);
            app.MapGet("/route/{unidadeId}", ([FromRoute(Name = "unidadeId")] Guid a) => a);
            app.MapGet("/query", ([FromQuery(Name = "tenant_id")] Guid a) => a);
        });

        var violations = ClientContextInput.Check(endpoints);

        Assert.Contains(violations, v => v.Contains("'/form'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("'/route/{unidadeId}'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("'/query'", StringComparison.Ordinal));
    }

    [Fact]
    public void The_synonyms_org_tenant_and_unidade_are_flagged_but_unidadeDestinoId_still_passes()
    {
        var bad = EndpointsOf(app => app.MapPost("/syn", (BodyWithSynonyms body) => body.Org));
        var ok = EndpointsOf(app => app.MapPost("/dest", (BodyOk body) => body.UnidadeDestinoId));

        Assert.Equal(3, ClientContextInput.Check(bad).Count);
        Assert.Empty(ClientContextInput.Check(ok));
    }

    [Fact]
    public void A_body_with_an_organizacaoId_or_unidadeId_field_is_flagged()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapPost("/a", (BodyWithOrganization body) => body.Nome);
            app.MapPost("/b", (BodyWithUnit body) => body.Nome);
            app.MapPost("/c", (BodyWithSnakeCase body) => body.Nome);
        });

        var violations = ClientContextInput.Check(endpoints);

        Assert.Equal(3, violations.Count);
        Assert.Contains(violations, v => v.Contains("/a", StringComparison.Ordinal) && v.Contains("OrganizacaoId", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("/b", StringComparison.Ordinal) && v.Contains("UnidadeId", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("/c", StringComparison.Ordinal));
    }

    [Fact]
    public void Nested_and_collection_bodies_are_inspected()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapPost("/nested", (Wrapper body) => body.Nome);
            app.MapPost("/list", (WrapperList body) => body.Nome);
            app.MapPost("/array", (BodyWithUnit[] body) => body.Length);
        });

        Assert.Equal(3, ClientContextInput.Check(endpoints).Count);
    }

    [Fact]
    public void Route_query_and_header_parameters_with_those_names_are_flagged()
    {
        var endpoints = EndpointsOf(app =>
        {
            app.MapGet("/r/{organizacaoId}", (Guid organizacaoId) => organizacaoId);
            app.MapGet("/q", (Guid unidadeId) => unidadeId);
            app.MapGet("/h", ([FromHeader(Name = "X-Org")] Guid organizationId) => organizationId);
            app.MapGet("/f", ([AsParameters] QueryFilter filter) => filter.TenantId);
            app.MapGet("/ok/{id}", (Guid id) => id);
        });

        var violations = ClientContextInput.Check(endpoints);

        Assert.Equal(4, violations.Count);
        Assert.DoesNotContain(violations, v => v.Contains("/ok", StringComparison.Ordinal));
    }

    [Fact]
    public void An_explicit_destination_field_is_not_the_request_context()
    {
        var endpoints = EndpointsOf(app => app.MapPost("/trocar", (BodyOk body) => body.Nome));

        Assert.Empty(ClientContextInput.Check(endpoints));
    }

    [Fact]
    public void No_route_of_the_real_api_takes_organization_or_unit_from_the_client()
    {
        using var api = new WebApplicationFactory<Program>();
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.Empty(ClientContextInput.Check(endpoints));

        // Not vacuous: the session routes of ticket 01 exist and one of them has a body that was inspected.
        var patterns = endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        Assert.Contains("/unidades", patterns);
        Assert.Contains("/sessao/unidade-ativa", patterns);
    }

    [GeneratedRegex(@"new\s+(?:[\w:.]+\.)?OrganizationContext\s*\(")]
    private static partial Regex BuildsContext();

    private static string Sep(string name) => Path.DirectorySeparatorChar + name + Path.DirectorySeparatorChar;

    /// <summary>Source files of every module's Application/Domain/Interface layer under <paramref name="srcRoot"/> that build the context by hand.</summary>
    private static List<string> FilesBuildingTheContext(string srcRoot, out int scanned)
    {
        var files = Directory.EnumerateDirectories(srcRoot)
            .Where(d => Path.GetFileName(d) is not ("Kernel" or "Infrastructure" or "Api"))
            .SelectMany(module => Directory.EnumerateDirectories(module).Where(l => Path.GetFileName(l) is "Application" or "Domain" or "Interface"))
            .SelectMany(layer => Directory.EnumerateFiles(layer, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Sep("obj"), StringComparison.Ordinal) && !f.Contains(Sep("bin"), StringComparison.Ordinal))
            .ToList();
        scanned = files.Count;
        return [.. files.Where(f => BuildsContext().IsMatch(File.ReadAllText(f)))];
    }

    [Fact]
    public void The_detector_also_flags_the_namespace_qualified_spelling()
    {
        Assert.Matches(BuildsContext(), "return new Flow4Tech.Kernel.OrganizationContext(a, b);");
        Assert.Matches(BuildsContext(), "return new global::Flow4Tech.Kernel.OrganizationContext (a, b);");
    }

    [Fact]
    public void The_source_scan_finds_a_violating_fixture_layer_and_ignores_infrastructure()
    {
        var root = Path.Combine(Path.GetTempPath(), "f4t-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var layer in new[] { "Application", "Domain", "Interface", "Infrastructure" })
            {
                Directory.CreateDirectory(Path.Combine(root, "Fixture", layer));
                File.WriteAllText(Path.Combine(root, "Fixture", layer, "X.cs"), "class X { object o = new OrganizationContext(a, b); }");
            }

            var offenders = FilesBuildingTheContext(root, out var scanned);

            Assert.Equal(3, scanned);
            Assert.Equal(3, offenders.Count);
            Assert.DoesNotContain(offenders, f => f.Contains("Infrastructure", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_detector_flags_code_that_builds_the_organization_context_by_hand()
    {
        Assert.Matches(BuildsContext(), "var ctx = new OrganizationContext(orgId, unitId);");
        Assert.DoesNotMatch(BuildsContext(), "await unitOfWork.ExecuteAsync(requestContext, work);");
    }

    [Fact]
    public void Module_application_code_never_builds_the_organization_context_it_gets_it_from_the_request()
    {
        var offenders = FilesBuildingTheContext(Path.Combine(ModuleGraph.BackendRoot(), "src"), out var scanned);

        Assert.True(scanned > 0, "the scan found no Application/Domain/Interface source files: it would pass vacuously");
        Assert.Empty(offenders);
    }
}
