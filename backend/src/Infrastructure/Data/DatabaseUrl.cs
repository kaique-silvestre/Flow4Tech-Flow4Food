using System.Globalization;
using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// Turns the platform style <c>postgres://user:password@host:port/database</c> (Railway's <c>DATABASE_URL</c>) into an Npgsql
/// connection string. Anything that is not a URL passes through unchanged. Error messages never contain the input: it carries the password.
/// </summary>
public static class DatabaseUrl
{
    private const string Expected = "postgres://user:password@host:port/database (user, password and database are required; percent-encode special characters in the password)";

    /// <summary>The explicit connection string wins; otherwise <paramref name="fallback"/>; otherwise null.</summary>
    public static string? Resolve(string? connectionString, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return Normalize(connectionString);
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : Normalize(fallback);
    }

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"The database URL is empty. Expected {Expected}.");
        }

        var trimmed = value.Trim();
        return IsUrl(trimmed) ? FromUrl(trimmed) : value;
    }

    private static bool IsUrl(string value) =>
        value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    private static string FromUrl(string value)
    {
        // The URL is never put in an exception: UriFormatException and friends are swallowed on purpose.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw Invalid("it could not be parsed");
        }

        if (uri.Fragment.Length > 0)
        {
            throw Invalid("a '#' ended the URL early; percent-encode '#', '?', '/' and '@' in the password");
        }

        var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw Invalid("user and password are required");
        }

        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (string.IsNullOrEmpty(database) || database.Contains('/', StringComparison.Ordinal))
        {
            throw Invalid("a single database name is required in the path");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.DnsSafeHost, // no brackets around IPv6 literals
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(uri.UserInfo[..separator]),
            Password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]),
            Database = database,
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !parts[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("only sslmode is supported as a query parameter");
            }

            if (!Enum.TryParse<SslMode>(Uri.UnescapeDataString(parts[1]).Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var mode))
            {
                throw Invalid("sslmode is not one of disable, allow, prefer, require, verify-ca, verify-full");
            }

            builder.SslMode = mode;
        }

        return builder.ConnectionString;
    }

    private static FormatException Invalid(string reason) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The database URL is invalid ({reason}). Expected {Expected}."));
}
