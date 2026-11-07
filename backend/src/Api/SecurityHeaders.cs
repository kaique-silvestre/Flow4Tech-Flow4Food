namespace Flow4Tech.Api;

/// <summary>
/// Security headers on EVERY response of the API (F4-0094). The API only answers JSON, empty bodies and redirects-free responses, so the policy is the
/// strictest one: nothing may be loaded or framed from an API response, no sniffing, no referrer, no sensor access, and nothing is cached unless a route
/// sets its own <c>Cache-Control</c> (responses carry tokens and tenant data). HSTS is sent only when the request is HTTPS as seen by the host: behind the
/// platform proxy that means <c>Flow4Tech__Rede__ProxyConfiavel=true</c> so <c>X-Forwarded-Proto</c> is honored; plain HTTP never gets it (browsers ignore it there).
/// No <c>includeSubDomains</c> or <c>preload</c>: those commit the whole domain and are a decision for the domain owner (see docs/runbooks/sast.md).
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    internal const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    internal const string StrictTransportSecurity = "max-age=31536000";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers.TryAdd("Cache-Control", "no-store");
            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = StrictTransportSecurity;
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
