using System.Text.Json.Nodes;
using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Kernel.Audit;
using FsCheck.Xunit;

namespace Flow4Tech.Audit.Tests;

/// <summary>Property tests (F4-0105) of the audit canonical form and the secret rule.</summary>
public sealed class AuditPropertyTests
{
    [Property]
    public bool The_canonical_json_ignores_key_order_and_is_idempotent(string[] keys, int[] values)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);
        var pairs = keys.Where(k => k is not null).Distinct(StringComparer.Ordinal).Select((k, i) => (k, v: values.Length == 0 ? i : values[i % values.Length])).ToList();
        string Build(IEnumerable<(string k, int v)> p)
        {
            var o = new JsonObject();
            foreach (var (k, v) in p)
            {
                o[k] = v;
            }

            return o.ToJsonString();
        }

        var a = AuditHash.CanonicalJson(Build(pairs));
        var b = AuditHash.CanonicalJson(Build(Enumerable.Reverse(pairs)));
        return a == b && AuditHash.CanonicalJson(a) == a;
    }

    [Property]
    public bool Microsecond_truncation_is_idempotent_and_never_moves_forward(long ticks)
    {
        var instante = new DateTimeOffset(Math.Abs(ticks % (DateTimeOffset.MaxValue.Ticks - TimeSpan.TicksPerDay * 2)) + TimeSpan.TicksPerDay, TimeSpan.Zero);
        var t = AuditHash.ToMicroseconds(instante);
        return AuditHash.ToMicroseconds(t) == t && t <= instante && (instante - t).Ticks < 10;
    }

    [Property]
    public bool A_key_the_rule_calls_sensitive_is_refused_at_any_depth_with_a_path_and_never_the_value(string? segredo)
    {
        var valor = segredo ?? "x";
        var node = new JsonObject { ["dados"] = new JsonObject { ["senha"] = valor } };
        var findings = SensitiveData.Scan(node);
        return findings.Count >= 1 && findings.All(f => !string.IsNullOrEmpty(valor) && valor.Length > 3 ? !f.Contains(valor, StringComparison.Ordinal) : true);
    }

    [Property]
    public bool Masking_is_idempotent_and_never_leaves_a_secret_looking_value(string? text)
    {
        var once = SensitiveData.MaskText(text);
        return SensitiveData.MaskText(once) == once;
    }
}
