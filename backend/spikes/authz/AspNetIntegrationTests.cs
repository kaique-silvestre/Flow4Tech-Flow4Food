using System.Security.Claims;
using Flow4Tech.Authorization.Tests;
using Flow4Tech.Kernel.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using static Flow4Tech.Authorization.Tests.Fixtures;

namespace Authz.Spike;

/// <summary>"Permission:resource:action" requirement; the facts of the operation travel as the resource of resource-based authorization.</summary>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>What a handler knows about the real operation (value, percent, unit). Built by the handler from server-side data, never from the request body.</summary>
public sealed record OperationFacts(Scope Where, DecisionContext Context);

/// <summary>The whole ASP.NET integration: one handler that maps the authenticated user to a Subject and asks the single interface.</summary>
public sealed class PermissionHandler(IAuthorizationEngine engine) : AuthorizationHandler<PermissionRequirement, OperationFacts>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement, OperationFacts facts)
    {
        var userId = context.User.FindFirstValue("sub");
        if (!Guid.TryParse(userId, out var id))
        {
            return; // not authenticated: no Succeed => denied
        }

        var profiles = context.User.FindAll("profile").Select(c => c.Value).ToList();
        var decision = await engine.DecidirAsync(new Subject(id, profiles), requirement.Permission, facts.Where, facts.Context);
        if (decision.IsAllowed)
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class AspNetIntegrationTests
{
    private static ServiceProvider Services(IAuthorizationEngine engine) =>
        new ServiceCollection()
            .AddLogging()
            .AddAuthorization(o => o.AddPolicy("desconto", p => p.AddRequirements(new PermissionRequirement(Desconto))))
            .AddSingleton(engine)
            .AddSingleton<IAuthorizationHandler, PermissionHandler>()
            .BuildServiceProvider();

    private static ClaimsPrincipal User(params string[] profiles) =>
        new(new ClaimsIdentity([new Claim("sub", Fixtures.User.ToString()), .. profiles.Select(p => new Claim("profile", p))], "test"));

    private static OperationFacts Facts(decimal percent) => new(At(Unit1), Ctx(percent: percent));

    [Fact]
    public async Task Resource_based_authorization_carries_the_real_operation_facts_to_the_engine()
    {
        var engine = new CedarAuthorizationEngine(Standard(), new InMemoryCumulativeLimitStore(), new Flow4Tech.Permissoes.Application.ConfiguredOperatingDayResolver(new FixedClock(Now)));
        using var services = Services(engine);
        var authz = services.GetRequiredService<IAuthorizationService>();

        Assert.True((await authz.AuthorizeAsync(User(Garcom), Facts(10m), new PermissionRequirement(Desconto))).Succeeded);
        Assert.False((await authz.AuthorizeAsync(User(Garcom), Facts(12m), new PermissionRequirement(Desconto))).Succeeded);
    }

    [Fact]
    public async Task A_name_only_policy_cannot_see_the_values_so_a_limited_permission_fails_closed()
    {
        var engine = new CedarAuthorizationEngine(Standard(), new InMemoryCumulativeLimitStore(), new Flow4Tech.Permissoes.Application.ConfiguredOperatingDayResolver(new FixedClock(Now)));
        using var services = Services(engine);
        var authz = services.GetRequiredService<IAuthorizationService>();

        // [Authorize(Policy = "desconto")] style: no resource, so the handler (AuthorizationHandler<TReq, TResource>) is never invoked.
        Assert.False((await authz.AuthorizeAsync(User(Garcom), "desconto")).Succeeded);
    }

    [Fact]
    public async Task An_unauthenticated_user_is_denied()
    {
        var engine = new CedarAuthorizationEngine(Standard(), new InMemoryCumulativeLimitStore(), new Flow4Tech.Permissoes.Application.ConfiguredOperatingDayResolver(new FixedClock(Now)));
        using var services = Services(engine);
        var authz = services.GetRequiredService<IAuthorizationService>();

        Assert.False((await authz.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), Facts(1m), new PermissionRequirement(Desconto))).Succeeded);
    }
}
