using System.Globalization;
using Microsoft.AspNetCore.Http.Features;

namespace Flow4Tech.Api;

/// <summary>Endpoint metadata: the request body may not exceed this many bytes (checked by <see cref="RouteGuardsMiddleware"/> before the body is read or the session looked up).</summary>
internal sealed class BodyLimitMetadata(long maxBytes)
{
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>Endpoint metadata: the route is reached with a COOKIE credential (refresh) or creates one (login), so it needs the CSRF guard.</summary>
internal sealed class CsrfGuardMetadata;

internal static class RouteGuardExtensions
{
    public static TBuilder WithBodyLimit<TBuilder>(this TBuilder builder, long maxBytes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new BodyLimitMetadata(maxBytes)));
        return builder;
    }

    /// <summary>Marks a route as cookie-authenticated: it needs the custom header and a same-origin (or configured) Origin, see <see cref="RouteGuardsMiddleware"/>.</summary>
    public static TBuilder RequireCsrfGuard<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new CsrfGuardMetadata()));
        return builder;
    }
}

/// <summary>Origins (scheme://host[:port]) other than the API's own that may call the cookie routes, from <c>Flow4Tech:Sessoes:AllowedOrigins</c>. Empty by default: same origin only.</summary>
internal sealed record AllowedOrigins(IReadOnlySet<string> Values);

/// <summary>
/// Request guards declared as endpoint metadata, run BEFORE the body is read and before any database call:
/// <list type="bullet">
/// <item><b>Body limit</b> (<see cref="BodyLimitMetadata"/>): a declared <c>Content-Length</c> above the limit answers 413; a chunked body is cut off at the limit while it is read.</item>
/// <item><b>CSRF</b> (<see cref="CsrfGuardMetadata"/>): the cookie routes are cookie-authenticated, so a browser attached to another site could send them. The guard needs (1) the custom header
/// <c>X-F4T-Csrf: 1</c>, which a cross-site form or plain request cannot add (a cross-site <c>fetch</c> with it needs a CORS preflight, which this API does not grant); (2) if
/// <c>Origin</c> is present, it must be this API's own authority (host and port; the scheme is not compared because a TLS-terminating proxy hides it) or a configured origin;
/// (3) if <c>Sec-Fetch-Site</c> is present it must be <c>same-origin</c> or <c>none</c>, or the request must come from a configured origin. Together with <c>SameSite=Strict</c> on the
/// cookie that is defence in depth; a non-browser client sends the header and neither of the other two.</item>
/// </list>
/// </summary>
internal sealed class RouteGuardsMiddleware(RequestDelegate next, AllowedOrigins allowedOrigins)
{
    public const string CsrfHeader = "X-F4T-Csrf";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<CsrfGuardMetadata>() is not null && !CsrfAllowed(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (endpoint?.Metadata.GetMetadata<BodyLimitMetadata>() is not { } limit)
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength > limit.MaxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit.MaxBytes;
        }

        context.Request.Body = new LimitedReadStream(context.Request.Body, limit.MaxBytes);
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
    }

    private bool CsrfAllowed(HttpRequest request)
    {
        if (request.Headers[CsrfHeader] != "1")
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        var originAllowed = false;
        if (origin.Length > 0)
        {
            originAllowed = OriginIsOwnOrConfigured(origin, request.Host.Value ?? string.Empty);
            if (!originAllowed)
            {
                return false;
            }
        }

        if (site.Length == 0 || site is "same-origin" or "none")
        {
            return true;
        }

        return originAllowed && allowedOrigins.Values.Contains(origin.TrimEnd('/'));
    }

    private bool OriginIsOwnOrConfigured(string origin, string host)
    {
        if (allowedOrigins.Values.Contains(origin.TrimEnd('/')))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read-only stream that fails once more than <c>limit</c> bytes were read (a chunked body has no declared length to check up front).</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > limit
                ? throw new BadHttpRequestException(string.Create(CultureInfo.InvariantCulture, $"The request body is larger than {limit} bytes."), StatusCodes.Status413PayloadTooLarge)
                : read;
        }
    }
}
