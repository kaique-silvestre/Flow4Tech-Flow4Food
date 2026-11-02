using System.Text.RegularExpressions;

namespace Flow4Tech.Api;

/// <summary>Which commit is serving. Not sensitive; lets the deploy smoke test confirm the NEW version is the one answering.</summary>
internal static partial class BuildInfo
{
    /// <summary>A git SHA (7-64 hex chars) trimmed, or null for anything else (never echoes arbitrary configuration).</summary>
    internal static string? NormalizeCommit(string? raw)
    {
        var value = raw?.Trim();
        return value is not null && ShaRegex().IsMatch(value) ? value : null;
    }

    /// <summary><c>GIT_SHA</c> (set by the deploy workflow) wins over <c>RAILWAY_GIT_COMMIT_SHA</c> (set by Railway's GitHub integration).</summary>
    internal static string? Commit(IConfiguration configuration) =>
        NormalizeCommit(configuration["GIT_SHA"]) ?? NormalizeCommit(configuration["RAILWAY_GIT_COMMIT_SHA"]);

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex ShaRegex();
}
