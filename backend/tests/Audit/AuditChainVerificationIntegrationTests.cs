using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Audit.Tests;

/// <summary>
/// Spec 5.6.5: the chain verification detects an altered, removed or reordered record. The attacker is simulated with the OWNER credential
/// (database access; the app credential cannot even try, see AuditTrailIntegrationTests). A violation raises a security event in the outbox.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class AuditChainVerificationIntegrationTests(PostgresFixture postgres)
{
    private static readonly OrganizationContext A = Ctx(OrgA);
    private static readonly OrganizationContext B = Ctx(OrgB);

    private async Task<AuditEnv> SeededAsync(int count = 5)
    {
        var env = await AuditEnv.CreateAsync(postgres);
        for (var i = 1; i <= count; i++)
        {
            await env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry("r" + i));
        }

        await env.RecordInUnitOfWorkAsync(B, AuditEnv.Entry("b1"));
        return env;
    }

    private static Task<long> ViolationEventsAsync(AuditEnv env, OrganizationContext org) =>
        AuditEnv.ScalarAsync(env.App, org, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'auditoria.integridade-violada'");

    [Fact]
    public async Task An_untouched_chain_verifies_and_raises_no_event()
    {
        await using var env = await SeededAsync();

        var result = await env.Verifier.VerifyAsync(A);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
        Assert.Equal(5, result.Records);
        Assert.Equal(5, result.Head!.Sequence);
        Assert.Equal(0, await ViolationEventsAsync(env, A));
    }

    [Fact]
    public async Task An_empty_chain_is_valid()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        var result = await env.Verifier.VerifyAsync(A);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.Records);
        Assert.Null(result.Head);
    }

    [Theory]
    [InlineData("UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 3")]
    [InlineData("UPDATE auditoria_registros SET depois = '{\"nome\":\"outro\"}'::jsonb WHERE sequencia = 3")]
    [InlineData("UPDATE auditoria_registros SET antes = NULL WHERE sequencia = 3")]
    [InlineData("UPDATE auditoria_registros SET usuario_id = gen_random_uuid() WHERE sequencia = 3")]
    [InlineData("UPDATE auditoria_registros SET acao = 'sessao:apagada' WHERE sequencia = 3")]
    [InlineData("UPDATE auditoria_registros SET ocorrido_em = ocorrido_em + interval '1 second' WHERE sequencia = 3")]
    [InlineData("UPDATE auditoria_registros SET correlation_id = 'outra' WHERE sequencia = 3")]
    [InlineData("UPDATE auditoria_registros SET hash_anterior = decode(repeat('ab', 32), 'hex') WHERE sequencia = 3")]
    [InlineData("UPDATE auditoria_registros SET hash = decode(repeat('ab', 32), 'hex') WHERE sequencia = 3")]
    public async Task An_altered_record_is_detected_at_its_sequence(string tamper)
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, tamper);

        var result = await env.Verifier.VerifyAsync(A);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Violations.Min(v => v.Sequence));
        Assert.Contains(result.Violations, v => v is { Sequence: 3, Kind: AuditViolationKind.Altered });
        Assert.Equal(1, await ViolationEventsAsync(env, A));
    }

    [Fact]
    public async Task A_removed_record_in_the_middle_is_detected()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "DELETE FROM auditoria_registros WHERE sequencia = 3");

        var result = await env.Verifier.VerifyAsync(A);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v is { Sequence: 4, Kind: AuditViolationKind.Removed }); // 4 follows 2: the gap is where 3 was
    }

    [Fact]
    public async Task A_removed_first_record_is_detected()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "DELETE FROM auditoria_registros WHERE sequencia = 1");

        var result = await env.Verifier.VerifyAsync(A);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Kind == AuditViolationKind.Removed);
    }

    [Fact]
    public async Task Swapped_records_are_detected()
    {
        await using var env = await SeededAsync();
        // Swap the positions of records 2 and 3 (three steps: the sequence is unique).
        await env.OwnerExecAsync(
            A,
            "UPDATE auditoria_registros SET sequencia = 99 WHERE sequencia = 2",
            "UPDATE auditoria_registros SET sequencia = 2 WHERE sequencia = 3",
            "UPDATE auditoria_registros SET sequencia = 3 WHERE sequencia = 99");

        var result = await env.Verifier.VerifyAsync(A);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Sequence is 2 or 3);
        Assert.Equal(1, await ViolationEventsAsync(env, A));
    }

    [Fact]
    public async Task A_record_moved_to_the_end_of_the_chain_is_detected()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET sequencia = 6 WHERE sequencia = 2");

        var result = await env.Verifier.VerifyAsync(A);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Removing_the_tail_is_detected_when_a_known_head_is_given_and_otherwise_is_the_documented_limit()
    {
        await using var env = await SeededAsync();
        var last = (await env.ChainAsync(A)).Last(); // read straight from the table: no verification yet, so no stored anchor exists
        var head = new AuditHead(last.Sequence, last.Hash);
        await env.OwnerExecAsync(A, "DELETE FROM auditoria_registros WHERE sequencia IN (4, 5)");

        var withoutAnchor = await env.Verifier.VerifyAsync(A);
        var withAnchor = await env.Verifier.VerifyAsync(A, head);

        Assert.True(withoutAnchor.IsValid); // limit: with no anchor at all (never verified, none given) a chain cannot prove its own tail
        Assert.False(withAnchor.IsValid);
        Assert.Contains(withAnchor.Violations, v => v.Kind == AuditViolationKind.Truncated);
    }

    [Fact]
    public async Task A_known_head_that_still_matches_keeps_the_chain_valid_after_it_grows()
    {
        await using var env = await SeededAsync();
        var head = (await env.Verifier.VerifyAsync(A)).Head!;
        await env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry("novo"));

        var result = await env.Verifier.VerifyAsync(A, head);

        Assert.True(result.IsValid);
        Assert.Equal(6, result.Head!.Sequence);
    }

    [Fact]
    public async Task A_known_head_whose_hash_no_longer_matches_is_a_violation()
    {
        await using var env = await SeededAsync();
        var head = (await env.Verifier.VerifyAsync(A)).Head!;
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 5");

        var result = await env.Verifier.VerifyAsync(A, head);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Tampering_with_one_organization_does_not_affect_the_other()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 2");

        Assert.False((await env.Verifier.VerifyAsync(A)).IsValid);
        Assert.True((await env.Verifier.VerifyAsync(B)).IsValid);
        Assert.Equal(0, await ViolationEventsAsync(env, B));
    }

    [Fact]
    public async Task The_violation_event_has_the_stable_type_and_names_the_first_broken_sequence_but_no_audit_content()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 4");

        await env.Verifier.VerifyAsync(A);

        Assert.Equal("auditoria.integridade-violada", AuditIntegrityViolated.Type);
        await using var tx = await env.Tenant.BeginAsync(A);
        await using var command = new Npgsql.NpgsqlCommand("SELECT payload::text FROM outbox_eventos WHERE tipo = 'auditoria.integridade-violada'", tx.Connection, tx.Transaction);
        var payload = (string)(await command.ExecuteScalarAsync())!;
        Assert.Equal(4, System.Text.Json.JsonDocument.Parse(payload).RootElement.GetProperty("FirstBrokenSequence").GetInt64());
        Assert.DoesNotContain("antes-r4", payload, StringComparison.Ordinal);
    }

    // ---- background job ----

    [Fact]
    public async Task The_periodic_job_calls_the_verifier_for_the_organization_it_carries_and_reports_a_violation()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 2");
        var jobs = new InProcessBackgroundJobs();
        await jobs.StartAsync(AuditJobs.Handler(env.Verifier));

        await jobs.EnqueueAsync(AuditJobs.VerifyChain(A));
        await jobs.EnqueueAsync(AuditJobs.VerifyChain(B));
        await jobs.StopAsync();

        Assert.Equal("auditoria.verificar-cadeia", AuditJobs.VerifyChainType);
        Assert.Equal(1, await ViolationEventsAsync(env, A));
        Assert.Equal(0, await ViolationEventsAsync(env, B));
    }

    [Fact]
    public async Task The_job_handler_ignores_job_types_that_are_not_its_own()
    {
        await using var env = await SeededAsync();
        var handler = AuditJobs.Handler(env.Verifier);

        await handler(new Job(A, "outro.job", "{}"), CancellationToken.None);

        Assert.Equal(0, await ViolationEventsAsync(env, A));
    }
}
