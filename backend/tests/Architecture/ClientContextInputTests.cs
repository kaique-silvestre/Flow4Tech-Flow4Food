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

    [GeneratedRegex(@"new\s+OrganizationContext\s*\(")]
    private static partial Regex BuildsContext();

    [Fact]
    public void The_detector_flags_code_that_builds_the_organization_context_by_hand()
    {
        Assert.Matches(BuildsContext(), "var ctx = new OrganizationContext(orgId, unitId);");
        Assert.DoesNotMatch(BuildsContext(), "await unitOfWork.ExecuteAsync(requestContext, work);");
    }

    [Fact]
    public void Module_application_code_never_builds_the_organization_context_it_gets_it_from_the_request()
    {
        var src = Path.Combine(ModuleGraph.BackendRoot(), "src");
        var offenders = Directory.EnumerateDirectories(src)
            .Where(d => Path.GetFileName(d) is not ("Kernel" or "Infrastructure" or "Api"))
            .SelectMany(module => Directory.EnumerateDirectories(module).Where(l => Path.GetFileName(l) is "Application" or "Domain" or "Interface"))
            .SelectMany(layer => Directory.EnumerateFiles(layer, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => BuildsContext().IsMatch(File.ReadAllText(f)))
            .ToList();

        Assert.Empty(offenders);
    }
}
