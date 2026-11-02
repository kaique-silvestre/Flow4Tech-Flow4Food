using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Infrastructure.Audit;

/// <summary>Everything that is hashed for one record, in the exact form it has in the database (see <see cref="AuditHash"/>).</summary>
internal sealed record AuditRecordData(
    Guid Id,
    Guid OrganizationId,
    Guid? UnitId,
    Guid? UserId,
    Guid? SessionId,
    string Action,
    string? ObjectType,
    string? ObjectId,
    string? Before,
    string? After,
    string Result,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    long Sequence,
    byte[]? PreviousHash);

/// <summary>
/// Hash of one audit record (spec 5.6.1). DESIGN (no ADR: the spec already decides "hash chained per organization"):
/// <list type="bullet">
/// <item><b>Algorithm</b>: SHA-256 (32 bytes) over the UTF-8 bytes of the canonical form below. The chain link is the hash of the previous record of the same organization.</item>
/// <item><b>Canonical form</b>: one JSON object, no spaces, properties in the FIXED order <c>v, id, org, unit, user, session, action, objectType, objectId, before, after, result,
/// correlation, at, seq, prev</c>. <c>v</c> is the canonicalization version (1): any change to this form must bump it and keep verifying old records by their own version.
/// Absent values are <c>null</c>. Guids are lower-case 'D' text; <c>at</c> is UTC with exactly 6 fractional digits (the precision of <c>timestamptz</c>, so the database round trip
/// cannot change it); <c>prev</c> is lower-case hex or null.</item>
/// <item><b>before/after</b> are embedded in canonical JSON: object keys sorted ordinally at every depth, no whitespace, numbers normalized through <c>decimal</c> (so <c>1e2</c>,
/// <c>100</c> and <c>100.0</c> are one value, which is what <c>jsonb</c> may hand back), strings escaped by the default JSON encoder. PostgreSQL <c>jsonb</c> reorders keys and
/// drops spaces; canonicalizing again on verification makes the hash independent of that.</item>
/// </list>
/// The hash covers every column except itself, so changing, moving (sequence) or re-linking any record changes it.
/// </summary>
internal static class AuditHash
{
    private const int Version = 1;

    public static string Canonical(AuditRecordData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("id", data.Id.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("org", data.OrganizationId.ToString("D", CultureInfo.InvariantCulture));
            WriteGuid(writer, "unit", data.UnitId);
            WriteGuid(writer, "user", data.UserId);
            WriteGuid(writer, "session", data.SessionId);
            writer.WriteString("action", data.Action);
            WriteText(writer, "objectType", data.ObjectType);
            WriteText(writer, "objectId", data.ObjectId);
            WriteJson(writer, "before", data.Before);
            WriteJson(writer, "after", data.After);
            writer.WriteString("result", data.Result);
            writer.WriteString("correlation", data.CorrelationId);
            writer.WriteString("at", ToMicroseconds(data.OccurredAt).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteNumber("seq", data.Sequence);
            WriteText(writer, "prev", data.PreviousHash is null ? null : Convert.ToHexStringLower(data.PreviousHash));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static byte[] Compute(AuditRecordData data) => SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(data)));

    /// <summary>Canonical JSON text (sorted keys, no whitespace, normalized numbers); null stays null.</summary>
    public static string? CanonicalJson(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement, "$");
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The instant as <c>timestamptz</c> will store it: UTC, truncated to microseconds.</summary>
    public static DateTimeOffset ToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    private static void WriteGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } guid)
        {
            writer.WriteString(name, guid.ToString("D", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteJson(Utf8JsonWriter writer, string name, string? json)
    {
        if (json is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WritePropertyName(name);
            writer.WriteRawValue(CanonicalJson(json)!, skipInputValidation: true);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, path + "." + (SensitiveData.LooksLikeSecret(property.Name) ? "[key]" : property.Name));
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item, $"{path}[{index++}]");
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                if (!element.TryGetDecimal(out var number))
                {
                    throw new AuditValueException("Audit data refused, number outside the supported range at: " + path);
                }

                writer.WriteRawValue(number.ToString("0.############################", CultureInfo.InvariantCulture), skipInputValidation: true);
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
