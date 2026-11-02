using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Flow4Tech.Kernel.Audit;

/// <summary>
/// THE rule for secrets (spec 5.6.3 and 5.6.6), shared by the audit trail and the technical log.
/// <list type="bullet">
/// <item><b>Keys.</b> A key is sensitive when, lower-cased and stripped of everything that is not a letter or digit, it CONTAINS one of
/// <see cref="ForbiddenKeyParts"/> (senha, password, token, secret, hash, ...): <c>novaSenha</c>, <c>refresh_token</c>, <c>PASSWORD-HASH</c> all match.</item>
/// <item><b>Values.</b> A string value is a secret when it looks like one regardless of its key: JWT, bcrypt/argon2/PBKDF2/ASP.NET Identity password hash,
/// <c>Bearer</c>/<c>Basic</c> credentials.</item>
/// <item><b>Audit REJECTS</b>: <see cref="Scan"/> returns the JSON paths (never the values); any finding makes the write fail with
/// <see cref="AuditSecretException"/> and nothing is stored. <b>The technical log MASKS</b>: <see cref="MaskText"/> / <see cref="IsSensitiveKey"/>
/// replace the value by <see cref="Mask"/>, because a log line must never be the thing that fails.</item>
/// </list>
/// Deliberately over-strict (a legitimate field named "hash" is also refused): a false positive costs a rename, a false negative leaks a secret.
/// </summary>
public static partial class SensitiveData
{
    public const string Mask = "***";

    private static readonly string[] ForbiddenKeyParts =
    [
        "senha", "password", "passwd", "pwd", "token", "secret", "segredo", "credencial", "credential",
        "apikey", "authorization", "cookie", "hash", "bearer", "privatekey", "chaveprivada",
    ];

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*|\$2[abxy]?\$\d{2}\$[./A-Za-z0-9]{20,}|\$argon2(?:id|i|d)\$\S+|\$pbkdf2\S+|AQAAAA[A-Za-z0-9+/=]{40,}|\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex SecretValue();

    /// <summary>True when the key names a secret (see the rule above).</summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var normalized = new StringBuilder(key.Length);
        foreach (var c in key.Where(char.IsLetterOrDigit))
        {
            normalized.Append(char.ToLowerInvariant(c));
        }

        var text = normalized.ToString();
        return ForbiddenKeyParts.Any(part => text.Contains(part, StringComparison.Ordinal));
    }

    /// <summary>True when the text contains something that looks like a secret on its own: JWT, password hash, bearer credential.</summary>
    public static bool LooksLikeSecret(string? value) => !string.IsNullOrEmpty(value) && SecretValue().IsMatch(value);

    /// <summary>Replaces secret-looking fragments of free text by <see cref="Mask"/>.</summary>
    public static string MaskText(string? text) => string.IsNullOrEmpty(text) ? text ?? string.Empty : SecretValue().Replace(text, Mask);

    /// <summary>JSON paths (never values) that break the rule.</summary>
    public static IReadOnlyList<string> Scan(JsonNode? node)
    {
        var findings = new List<string>();
        Walk(node, "$", findings);
        return findings;
    }

    private static void Walk(JsonNode? node, string path, List<string> findings)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    var childPath = path + "." + key;
                    if (IsSensitiveKey(key))
                    {
                        findings.Add(childPath);
                    }
                    else
                    {
                        Walk(child, childPath, findings);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", findings);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && LooksLikeSecret(text):
                findings.Add(path);
                break;
            default:
                break;
        }
    }
}

public sealed class AuditSecretException : InvalidOperationException
{
    public AuditSecretException()
        : base("Audit data contains a secret.")
    {
    }

    public AuditSecretException(string message)
        : base(message)
    {
    }

    public AuditSecretException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
