using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Flow4Tech.Identidade.Domain;

/// <summary>Operational user name: unique inside ONE organization (not globally), compared in its normalized form (trimmed, Unicode NFKC, lower case).</summary>
public static partial class NomeDeUsuario
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,31}$")]
    private static partial Regex Format();

    /// <summary>The canonical case of identifiers is lower (culture-invariant). Not <c>ToLowerInvariant</c>: CA1308 wants the upper form, which would change every stored key.</summary>
    internal static string Lower(string text) => CultureInfo.InvariantCulture.TextInfo.ToLower(text);

    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 64)
        {
            return false;
        }

        var candidate = Lower(raw.Trim().Normalize(NormalizationForm.FormKC));
        if (!Format().IsMatch(candidate))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}

/// <summary>E-mail of an account with e-mail: unique across the platform because one account belongs to one organization (README premise).</summary>
public static partial class EmailDeConta
{
    public const int MaximoCaracteres = 254;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s.]+$")]
    private static partial Regex Format();

    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaximoCaracteres + 16)
        {
            return false;
        }

        var candidate = NomeDeUsuario.Lower(raw.Trim().Normalize(NormalizationForm.FormKC));
        if (candidate.Length > MaximoCaracteres || candidate.Any(char.IsControl) || !Format().IsMatch(candidate))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
