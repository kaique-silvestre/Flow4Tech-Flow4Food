using System.Security.Claims;
using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Microsoft.AspNetCore.Authorization;

namespace Flow4Tech.Api;

/// <summary>The permission a route needs (<c>resource:action</c>, from the catalog).</summary>
internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>
/// What the handler is asked about: the resolved request (user, session, organization, unit) and where the answer goes. The handler writes the engine's reason here so the
/// middleware can log it; the reason is NEVER sent to the client (spec 5.4.1).
/// </summary>
internal sealed class OperationFacts(IRequestContext request, CancellationToken cancellationToken)
{
    public IRequestContext Request { get; } = request;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public string? Reason { get; set; }
}

/// <summary>
/// Turns the session into a <see cref="Subject"/> and asks the one engine (<c>IAuthorizationEngine.DecidirAsync</c>, ADR-006). It evaluates nothing itself and never reads a profile
/// name: the profiles come from <see cref="IPerfisDoUsuario"/> and only the engine interprets them. Route-level decisions carry no operation values, so a permission with a
/// limit is denied here (fails closed): those are asked in the Application handler, after loading the real operation.
/// </summary>
internal sealed class PermissionHandler(IAuthorizationEngine engine, IPerfisDoUsuario perfis) : AuthorizationHandler<PermissionRequirement, OperationFacts>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement, OperationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(facts);
        var organization = facts.Request.Organization;
        var profiles = await perfis.ListarAsync(facts.Request, facts.CancellationToken);
        var unit = organization.UnitId == Guid.Empty ? (Guid?)null : organization.UnitId;
        var decision = await engine.DecidirAsync(
            new Subject(facts.Request.UserId, profiles),
            requirement.Permission,
            new Scope(organization.OrganizationId, unit),
            new DecisionContext(),
            facts.CancellationToken);
        facts.Reason = decision.Reason;
        if (decision.IsAllowed)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, decision.Reason));
        }
    }
}

/// <summary>Endpoint metadata: a denial on this route is a security fact worth an audit record (spec 5.4.8). Set by <c>RequirePermission(..., sensitive: true)</c>.</summary>
internal sealed class SensitiveRouteMetadata;

/// <summary>
/// Runs after <see cref="RequestContextMiddleware"/> (which already answered 401 for a missing session): for a route that declares a permission, asks the permission handler.
/// Denied: 403 with an EMPTY body (no reason, no permission name); the reason goes to the technical log (<c>acesso.negado</c>) and, on a sensitive route, to the audit trail
/// (<c>acesso:negar</c>, result <c>negado</c>) in its own transaction so it persists although the request failed. If the audit store is down the request fails (500): a denial
/// that cannot be recorded is not silently swallowed.
/// </summary>
internal sealed class PermissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<SessionRequiredMetadata>() is null || endpoint.Metadata.GetMetadata<PermissionDeclaration>() is not { } declaration)
        {
            await next(context);
            return;
        }

        var services = context.RequestServices;
        var request = services.GetRequiredService<RequestContextAccessor>();
        var facts = new OperationFacts(request, context.RequestAborted);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, request.UserId.ToString("D"))], "session"));
        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, facts, new PermissionRequirement(declaration.Name));
        if (result.Succeeded)
        {
            await next(context);
            return;
        }

        services.GetRequiredService<ITechnicalLog>().Write(
            LogSeverity.Warning,
            "acesso.negado",
            new Dictionary<string, object?> { ["permissao"] = declaration.Name, ["motivo"] = facts.Reason ?? "unknown", ["usuarioId"] = request.UserId, ["sessaoId"] = request.SessionId });
        if (endpoint.Metadata.GetMetadata<SensitiveRouteMetadata>() is not null)
        {
            var organization = request.Organization;
            await services.GetRequiredService<IAuditLog>().RecordDetachedAsync(
                organization,
                new AuditEntry
                {
                    Action = PlatformAuditActions.AcessoNegar,
                    Result = AuditResult.Negado,
                    UserId = request.UserId,
                    SessionId = request.SessionId,
                    UnitId = organization.UnitId == Guid.Empty ? null : organization.UnitId,
                    ObjectType = "permissao",
                    ObjectId = declaration.Name,
                    After = new JsonObject { ["permissao"] = declaration.Name },
                },
                context.RequestAborted);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }
}
