namespace Flow4Tech.Api;

/// <summary>
/// The refresh cookie (spec 5.3.3). <c>HttpOnly</c> (script cannot read it), <c>Secure</c> (ALWAYS, whatever the request scheme: behind a TLS-terminating proxy the scheme is plain
/// HTTP, and a Secure flag that depended on it would silently drop), <c>SameSite=Strict</c> (the browser never attaches it to a request started from another site, which is also
/// why a cross-site front needs a same-site proxy; Lax would send it on top-level cross-site GETs for no benefit, since only POST routes read it), <c>Path=/</c> and no
/// <c>Domain</c>, which is what the <c>__Host-</c> prefix requires (it stops a sibling subdomain or a plain-HTTP response from planting or overwriting the cookie). A path
/// restricted to the refresh routes would be tighter, but the prefix and a custom path are mutually exclusive, and the prefix defends against the attack that matters more;
/// only <c>/refresh</c>, <c>/logout</c> and <c>/login</c> ever read it (ADR-010).
/// </summary>
internal static class RefreshCookie
{
    public const string Name = "__Host-f4t_refresh";

    public static void Set(HttpResponse response, string token, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(Name, token, Options(lifetime));
    }

    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var options = Options(TimeSpan.Zero);
        options.Expires = DateTimeOffset.UnixEpoch;
        response.Cookies.Append(Name, string.Empty, options);
    }

    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    private static CookieOptions Options(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = lifetime < TimeSpan.Zero ? TimeSpan.Zero : lifetime,
        IsEssential = true,
    };
}
