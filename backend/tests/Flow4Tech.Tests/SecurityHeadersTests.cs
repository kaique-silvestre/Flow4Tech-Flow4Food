using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Flow4Tech.Tests;

/// <summary>F4-0094: the API sends the security headers on every response, success and refusal alike.</summary>
public sealed class SecurityHeadersTests
{
    [Theory]
    [InlineData("/version", HttpStatusCode.OK)]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/does-not-exist", HttpStatusCode.NotFound)]
    public async Task Every_response_carries_the_security_headers(string path, HttpStatusCode expected)
    {
        await using var api = new WebApplicationFactory<Program>();
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", Single(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));
        Assert.Contains("camera=()", Single(response, "Permissions-Policy"), StringComparison.Ordinal);
        Assert.Equal("no-store", Single(response, "Cache-Control"));
    }

    [Fact]
    public async Task Hsts_is_sent_only_over_https()
    {
        await using var api = new WebApplicationFactory<Program>();
        using var plain = api.CreateClient();
        using var secure = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var overHttp = await plain.GetAsync(new Uri("/version", UriKind.Relative));
        using var overHttps = await secure.GetAsync(new Uri("/version", UriKind.Relative));

        Assert.False(overHttp.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("max-age=31536000", Single(overHttps, "Strict-Transport-Security"));
    }

    private static string Single(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));
}
