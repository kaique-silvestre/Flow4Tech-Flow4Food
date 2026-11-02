using System.Diagnostics;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Api;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Correlation of a request (spec 5.6.6): the W3C trace id of the request's <see cref="Activity"/> (ASP.NET Core starts one and honors an incoming
/// <c>traceparent</c>; if none exists it is started here from that header) is THE correlation id. The same id goes (1) in the <c>X-Correlation-Id</c> response
/// header, (2) in the technical log line written when the request ends, (3) in every audit record written during it (<c>Correlation.Current</c>).
/// The technical line carries method, route PATTERN and status only: never the query string, headers, cookies or body.
/// </summary>
internal sealed class CorrelationMiddleware(RequestDelegate next, ITechnicalLog log)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var own = Activity.Current is null ? new Activity("Flow4Tech.Request") : null;
        if (own is not null)
        {
            if (context.Request.Headers.TryGetValue("traceparent", out var parent) && !string.IsNullOrEmpty(parent))
            {
                own.SetParentId(parent.ToString());
            }

            own.Start();
        }

        var correlationId = Correlation.Current;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        var status = StatusCodes.Status500InternalServerError;
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        finally
        {
            log.Write(
                status >= StatusCodes.Status500InternalServerError ? LogSeverity.Error : LogSeverity.Information,
                "http.requisicao",
                new Dictionary<string, object?>
                {
                    ["metodo"] = context.Request.Method,
                    ["rota"] = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(sem rota)",
                    ["status"] = status,
                });
        }
    }
}
