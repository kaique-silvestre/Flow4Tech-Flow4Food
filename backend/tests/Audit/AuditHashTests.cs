using Flow4Tech.Infrastructure.Audit;

namespace Flow4Tech.Audit.Tests;

/// <summary>Canonicalization of the hashed content: deterministic, independent of JSON key order and spacing, sensitive to every field.</summary>
public sealed class AuditHashTests
{
    private static readonly Guid Org = Guid.Parse("0197a000-0000-7000-8000-00000000000a");

    private static AuditRecordData Sample() => new(
        Guid.Parse("0197a000-0000-7000-8000-000000000001"), Org, Guid.Parse("0197a000-0000-7000-8000-0000000000a1"),
        Guid.Parse("0197a000-0000-7000-8000-0000000000c1"), Guid.Parse("0197a000-0000-7000-8000-0000000000d1"),
        "sessao:trocar-unidade", "unidade", "u1", """{"b":2,"a":1}""", """{"x":"y"}""", "sucesso", "4bf92f3577b34da6a3ce929d0e0e4736",
        new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567), 2, new byte[32]);

    [Fact]
    public void Json_is_canonical_regardless_of_key_order_and_spacing()
    {
        Assert.Equal(AuditHash.CanonicalJson("""{"b":2,"a":{"d":1,"c":[3,{"z":1,"y":2}]}}"""), AuditHash.CanonicalJson("""  { "a" : { "c" : [3, {"y":2,"z":1}], "d":1 }, "b" : 2 }  """));
        Assert.Equal("""{"a":1,"b":2}""", AuditHash.CanonicalJson("""{"b":2,"a":1}"""));
        Assert.Null(AuditHash.CanonicalJson(null));
    }

    [Fact]
    public void Numbers_are_normalized_so_the_jsonb_round_trip_cannot_change_the_hash()
    {
        Assert.Equal(AuditHash.CanonicalJson("""{"n":100}"""), AuditHash.CanonicalJson("""{"n":1e2}"""));
        Assert.Equal(AuditHash.CanonicalJson("""{"n":1.5}"""), AuditHash.CanonicalJson("""{"n":1.50}"""));
        Assert.NotEqual(AuditHash.CanonicalJson("""{"n":1}"""), AuditHash.CanonicalJson("""{"n":"1"}"""));
    }

    [Fact]
    public void Hash_is_deterministic_sha256_of_the_canonical_form()
    {
        var a = AuditHash.Compute(Sample());
        var b = AuditHash.Compute(Sample() with { Before = """{"a":1,"b":2}""" });

        Assert.Equal(32, a.Length);
        Assert.Equal(a, b); // same content, different key order
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AuditHash.Canonical(Sample()))), a);
    }

    [Fact]
    public void Every_hashed_field_changes_the_hash()
    {
        var baseline = AuditHash.Compute(Sample());
        var variants = new AuditRecordData[]
        {
            Sample() with { Id = Guid.NewGuid() },
            Sample() with { OrganizationId = Guid.NewGuid() },
            Sample() with { UnitId = null },
            Sample() with { UserId = Guid.NewGuid() },
            Sample() with { SessionId = Guid.NewGuid() },
            Sample() with { Action = "sessao:outra" },
            Sample() with { ObjectType = "outro" },
            Sample() with { ObjectId = "u2" },
            Sample() with { Before = null },
            Sample() with { After = """{"x":"z"}""" },
            Sample() with { Result = "negado" },
            Sample() with { CorrelationId = "00000000000000000000000000000001" },
            Sample() with { OccurredAt = Sample().OccurredAt.AddTicks(10) },
            Sample() with { Sequence = 3 },
            Sample() with { PreviousHash = new byte[32] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } },
            Sample() with { PreviousHash = null },
        };

        foreach (var variant in variants)
        {
            Assert.NotEqual(baseline, AuditHash.Compute(variant));
        }
    }

    [Fact]
    public void Fields_cannot_be_shifted_into_each_other()
    {
        // "ab" + "c" must not hash like "a" + "bc": the canonical form is JSON with explicit field names.
        Assert.NotEqual(
            AuditHash.Compute(Sample() with { ObjectType = "ab", ObjectId = "c" }),
            AuditHash.Compute(Sample() with { ObjectType = "a", ObjectId = "bc" }));
    }

    [Fact]
    public void Instants_are_truncated_to_microseconds_like_timestamptz()
    {
        var value = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

        var truncated = AuditHash.ToMicroseconds(value);

        Assert.Equal(value.AddTicks(-7), truncated);
        Assert.Equal(TimeSpan.Zero, truncated.Offset);
    }
}
