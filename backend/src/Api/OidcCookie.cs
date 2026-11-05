namespace Flow4Tech.Api;

/// <summary>
/// The signed state of ONE Google round trip (<c>ISeloDeTransacao</c>). Unlike the refresh cookie it must be <c>SameSite=Lax</c>: the browser comes back from google.com with a top-level cross-site GET, and a Strict
/// cookie would not be sent on it. Lax is enough because the callback only acts together with the matching <c>state</c> in the URL, which an attacker cannot know. <c>HttpOnly</c>, <c>Secure</c>, <c>__Host-</c> prefix
/// (<c>Path=/</c>, no <c>Domain</c>), ten minutes, and deleted as soon as the callback has been served.
/// </summary>
internal static class OidcCookie
{
    public const string Name = "__Host-f4t_oidc";

    public static void Set(HttpResponse response, string value)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(Name, value, Options(TimeSpan.FromMinutes(10)));
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
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = lifetime,
        IsEssential = true,
    };
}
