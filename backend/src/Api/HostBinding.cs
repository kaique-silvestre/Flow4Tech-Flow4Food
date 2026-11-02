using System.Globalization;

namespace Flow4Tech.Api;

/// <summary>Where the host listens. Railway (and most platforms) inject <c>PORT</c>; the standard container falls back to 8080.</summary>
internal static class HostBinding
{
    internal const int DefaultPort = 8080;

    /// <summary>
    /// <c>PORT</c> wins. Without it, an explicit binding (ASPNETCORE_URLS / HTTP_PORTS) is left alone (returns null), otherwise 8080.
    /// </summary>
    internal static string? ResolveUrl(string? port, bool otherBindingConfigured)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            return otherBindingConfigured ? null : Url(DefaultPort);
        }

        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 65535)
        {
            throw new FormatException("PORT must be an integer between 1 and 65535.");
        }

        return Url(value);
    }

    private static string Url(int port) => string.Create(CultureInfo.InvariantCulture, $"http://*:{port}");
}
