using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Kernel.Audit;

/// <summary>
/// Marks a static class whose <c>public const string</c> fields are audit action names (<c>recurso:acao</c>), like <c>PermissionCatalog</c> for permissions.
/// The names are exported to <c>backend/audit.catalog.txt</c> and an architecture test fails when a published name disappears (never rename: 5.6.7).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditCatalogAttribute : Attribute;

/// <summary>Audit actions of the platform itself. Each module declares its own in a class marked <see cref="AuditCatalogAttribute"/>.</summary>
[AuditCatalog]
public static class PlatformAuditActions
{
    /// <summary>Switch of the active unit (spec 5.1.4). Result <c>sucesso</c> or, when the target is not linked to the user, <c>negado</c>.</summary>
    public const string SessaoTrocarUnidade = "sessao:trocar-unidade";
}

public static partial class AuditActionName
{
    [GeneratedRegex("^[a-z][a-z0-9-]*:[a-z][a-z0-9_-]*$")]
    private static partial Regex Format();

    public static bool IsValid(string? action) => action is { Length: <= 120 } && Format().IsMatch(action);
}

/// <summary>
/// Security event raised (outbox, type <see cref="Type"/>) when the verification of an organization's audit chain finds a record that was
/// altered, removed or reordered. No consumer yet. The type name is stable: never rename it once published.
/// </summary>
public sealed record AuditIntegrityViolated(Guid EventId, Guid OrganizationId, long FirstBrokenSequence, string Kind, DateTimeOffset DetectedAt) : IDomainEvent
{
    public const string Type = "auditoria.integridade-violada";

    public string EventType => Type;
}

public static class AuditEntryRules
{
    /// <summary>
    /// The rules every <see cref="IAuditLog"/> applies before storing anything: the action is <c>recurso:acao</c> (<see cref="ArgumentException"/>),
    /// and neither the object reference nor <c>antes</c>/<c>depois</c> hold a secret (<see cref="AuditSecretException"/>, which names paths only).
    /// </summary>
    public static void EnsureValid(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!AuditActionName.IsValid(entry.Action))
        {
            throw new ArgumentException("The audit action must be 'recurso:acao' (lower case, from an [AuditCatalog] class).", nameof(entry));
        }

        var findings = new List<string>();
        if (SensitiveData.LooksLikeSecret(entry.ObjectType))
        {
            findings.Add("objectType");
        }

        if (SensitiveData.LooksLikeSecret(entry.ObjectId))
        {
            findings.Add("objectId");
        }

        findings.AddRange(SensitiveData.Scan(entry.Before).Select(p => "before" + p[1..]));
        findings.AddRange(SensitiveData.Scan(entry.After).Select(p => "after" + p[1..]));
        if (findings.Count > 0)
        {
            throw new AuditSecretException("Audit data refused, secret at: " + string.Join(", ", findings));
        }

        var numbers = new List<string>();
        FindUnrepresentableNumbers(entry.Before, "before", numbers);
        FindUnrepresentableNumbers(entry.After, "after", numbers);
        if (numbers.Count > 0)
        {
            throw new AuditValueException("Audit data refused, number outside the supported range at: " + string.Join(", ", numbers));
        }
    }

    /// <summary>Numbers must fit a <c>decimal</c> (the canonical form of the hash, see <c>AuditHash</c>). Paths only, never values.</summary>
    private static void FindUnrepresentableNumbers(JsonNode? node, string path, List<string> findings)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    FindUnrepresentableNumbers(child, path + "." + (SensitiveData.LooksLikeSecret(key) ? "[key]" : key), findings);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    FindUnrepresentableNumbers(array[i], $"{path}[{i}]", findings);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                using (var document = JsonDocument.Parse(value.ToJsonString()))
                {
                    if (!document.RootElement.TryGetDecimal(out _))
                    {
                        findings.Add(path);
                    }
                }

                break;
            default:
                break;
        }
    }
}
