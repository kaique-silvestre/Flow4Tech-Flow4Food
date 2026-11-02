using Flow4Tech.Kernel;
using Flow4Tech.Organizacao.Application;

namespace Flow4Tech.Api;

/// <summary>Endpoint metadata: the route needs an authenticated session with a resolved organization/unit context (see <see cref="RequestContextMiddleware"/>).</summary>
internal sealed class SessionRequiredMetadata(bool allowWithoutUnit, bool allowPasswordChangePending)
{
    /// <summary>The route also works for a session that has no active unit yet (the selection routes, the password change).</summary>
    public bool AllowWithoutUnit { get; } = allowWithoutUnit;

    /// <summary>The route also works for a session in the "must change the password" state (only the password change itself).</summary>
    public bool AllowPasswordChangePending { get; } = allowPasswordChangePending;
}

internal static class RequestContextExtensions
{
    /// <summary>
    /// Marks a route as authenticated: the request context is resolved from the session before the handler runs (401 without session, 403 without a link to the unit).
    /// By default a route is for a NORMAL session only: one that must still change its temporary password answers 403, and one with no active unit yet answers 403.
    /// <paramref name="allowWithoutUnit"/> and <paramref name="allowPasswordChangePending"/> open the few routes that those states may reach (ticket 04).
    /// </summary>
    public static TBuilder RequireSession<TBuilder>(this TBuilder builder, bool allowWithoutUnit = false, bool allowPasswordChangePending = false)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new SessionRequiredMetadata(allowWithoutUnit, allowPasswordChangePending)));
        return builder;
    }
}

/// <summary>Per-request holder filled by <see cref="RequestContextMiddleware"/>. Reading before it is filled throws: there is no ambient default organization.</summary>
internal sealed class RequestContextAccessor : IRequestContext
{
    private AuthenticatedSession? _session;
    private OrganizationContext? _organization;

    public Guid UserId => (_session ?? throw NotResolved()).UserId;

    public Guid SessionId => (_session ?? throw NotResolved()).SessionId;

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
/// The single place that resolves <see cref="OrganizationContext"/> (spec 5.1). For a route marked <c>RequireSession()</c>: no valid session -> 401; a session that must still
/// change its temporary password reaches only the routes that allow it -> 403; a session with no active unit reaches only the routes that allow it -> 403; a user with no link to
/// the active unit (or to a unit of that organization at all) -> 403, with no reason in the body. The organization and unit come from the session only; nothing in the route,
/// query, body or headers is read.
/// </summary>
internal sealed class RequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetEndpoint()?.Metadata.GetMetadata<SessionRequiredMetadata>() is not { } required)
        {
            await next(context);
            return;
        }

        // Resolved only for routes that need a session: public routes (health, version) must work with no database configured.
        var sessions = context.RequestServices.GetRequiredService<ISessionSource>();
        var session = await sessions.ReadAsync(context, context.RequestAborted);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (session.PasswordChangePending && !required.AllowPasswordChangePending)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        OrganizationContext organization;
        if (session.ActiveUnitId is { } unitId)
        {
            // The only place the context is assembled; the link is checked under RLS of the session's own organization.
            organization = new OrganizationContext(session.OrganizationId, unitId);
            var unidades = context.RequestServices.GetRequiredService<UnidadesDoUsuario>();
            if (!await unidades.TemVinculoAsync(session.UserId, organization, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else if (required.AllowWithoutUnit)
        {
            // No unit yet: the context carries the organization only (the routes that allow this list or choose a unit; none touches unit data).
            organization = new OrganizationContext(session.OrganizationId, Guid.Empty);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.RequestServices.GetRequiredService<RequestContextAccessor>().Set(session, organization);
        await next(context);
    }
}
