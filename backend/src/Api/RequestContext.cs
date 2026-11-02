using Flow4Tech.Kernel;
using Flow4Tech.Organizacao.Application;

namespace Flow4Tech.Api;

/// <summary>Endpoint metadata: the route needs an authenticated session with a resolved organization/unit context (see <see cref="RequestContextMiddleware"/>).</summary>
internal sealed class SessionRequiredMetadata;

internal static class RequestContextExtensions
{
    /// <summary>Marks a route as authenticated: the request context is resolved from the session before the handler runs (401 without session, 403 without a link to the unit).</summary>
    public static TBuilder RequireSession<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new SessionRequiredMetadata()));
        return builder;
    }
}

/// <summary>Per-request holder filled by <see cref="RequestContextMiddleware"/>. Reading before it is filled throws: there is no ambient default organization.</summary>
internal sealed class RequestContextAccessor : IRequestContext
{
    private AuthenticatedSession? _session;
    private OrganizationContext? _organization;

    public Guid UserId => (_session ?? throw NotResolved()).UserId;

    public OrganizationContext Organization => _organization ?? throw NotResolved();

    public AuthenticatedSession Session => _session ?? throw NotResolved();

    public void Set(AuthenticatedSession session, OrganizationContext organization)
    {
        _session = session;
        _organization = organization;
    }

    private static InvalidOperationException NotResolved() =>
        new("The request context was not resolved: the route must be marked .RequireSession() and run after the request context middleware.");
}

/// <summary>
/// The single place that resolves <see cref="OrganizationContext"/> (spec 5.1). For a route marked <c>RequireSession()</c>: no session or no
/// active unit in it -> 401; a user with no link to that unit (or to a unit of that organization at all) -> 403, with no reason in the body.
/// The organization and unit come from the session only; nothing in the route, query, body or headers is read.
/// </summary>
internal sealed class RequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetEndpoint()?.Metadata.GetMetadata<SessionRequiredMetadata>() is null)
        {
            await next(context);
            return;
        }

        // Resolved only for routes that need a session: public routes (health, version) must work with no database configured.
        var sessions = context.RequestServices.GetRequiredService<ISessionSource>();
        var session = await sessions.ReadAsync(context, context.RequestAborted);
        if (session is not { ActiveUnitId: { } unitId })
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // The only place the context is assembled; the link is checked under RLS of the session's own organization.
        var organization = new OrganizationContext(session.OrganizationId, unitId);
        var unidades = context.RequestServices.GetRequiredService<UnidadesDoUsuario>();
        if (!await unidades.TemVinculoAsync(session.UserId, organization, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.RequestServices.GetRequiredService<RequestContextAccessor>().Set(session, organization);
        await next(context);
    }
}
