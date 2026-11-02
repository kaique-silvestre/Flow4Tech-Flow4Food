using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Audit.Tests;

/// <summary>
/// State the verifier keeps per organization (migration 0006): the violation already alerted (one outbox event per DISTINCT violation, not per run)
/// and the anchor (head of the last valid verification) that lets the periodic job see a removed tail.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class AuditVerificationStateIntegrationTests(PostgresFixture postgres)
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

    private static Task<long> EventsAsync(AuditEnv env, OrganizationContext org) =>
        AuditEnv.ScalarAsync(env.App, org, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'auditoria.integridade-violada'");

    private static async Task RunJobAsync(AuditEnv env, OrganizationContext org)
    {
        var jobs = new InProcessBackgroundJobs();
        await jobs.StartAsync(AuditJobs.Handler(env.Verifier));
        await jobs.EnqueueAsync(AuditJobs.VerifyChain(org));
        await jobs.StopAsync();
    }

    // ---- one event per distinct violation ----

    [Fact]
    public async Task Verifying_the_same_tampered_chain_twice_raises_one_event()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 3");

        var first = await env.Verifier.VerifyAsync(A);
        var second = await env.Verifier.VerifyAsync(A);

        Assert.False(first.IsValid);
        Assert.False(second.IsValid); // the report is still complete every time; only the ALERT is deduplicated
        Assert.Equal(1, await EventsAsync(env, A));
    }

    [Fact]
    public async Task A_different_violation_raises_another_event()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 3");
        await env.Verifier.VerifyAsync(A);

        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 5"); // first broken record is still 3
        await env.Verifier.VerifyAsync(A);
        await env.Verifier.VerifyAsync(A);

        Assert.Equal(2, await EventsAsync(env, A));
    }

    [Fact]
    public async Task The_same_violation_after_the_chain_became_valid_again_raises_a_new_event()
    {
        await using var env = await SeededAsync();
        var original = (await env.ChainAsync(A))[2].Hash;
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 3");
        await env.Verifier.VerifyAsync(A);

        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'sucesso' WHERE sequencia = 3"); // restored
        Assert.True((await env.Verifier.VerifyAsync(A)).IsValid);
        Assert.Equal(original, (await env.ChainAsync(A))[2].Hash);

        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 3");
        await env.Verifier.VerifyAsync(A);

        Assert.Equal(2, await EventsAsync(env, A));
    }

    [Fact]
    public async Task The_alert_state_is_per_organization()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 3");
        await env.OwnerExecAsync(B, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 1");

        await env.Verifier.VerifyAsync(A);
        await env.Verifier.VerifyAsync(B);
        await env.Verifier.VerifyAsync(A);
        await env.Verifier.VerifyAsync(B);

        Assert.Equal(1, await EventsAsync(env, A));
        Assert.Equal(1, await EventsAsync(env, B));
    }

    [Fact]
    public async Task Concurrent_verifications_of_the_same_tampered_chain_raise_one_event()
    {
        await using var env = await SeededAsync();
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 3");

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => env.Verifier.VerifyAsync(A)));

        Assert.Equal(1, await EventsAsync(env, A));
    }

    // ---- stored anchor ----

    [Fact]
    public async Task After_a_valid_run_the_job_detects_a_removed_tail_without_any_anchor_from_the_caller()
    {
        await using var env = await SeededAsync();
        await RunJobAsync(env, A); // valid: stores the head (5)
        await env.OwnerExecAsync(A, "DELETE FROM auditoria_registros WHERE sequencia IN (4, 5)"); // attacker with the OWNER credential removes the newest records

        await RunJobAsync(env, A);

        Assert.Equal(1, await EventsAsync(env, A));
        var again = await env.Verifier.VerifyAsync(A);
        Assert.False(again.IsValid);
        Assert.Contains(again.Violations, v => v is { Sequence: 5, Kind: AuditViolationKind.Truncated });
        Assert.Equal(1, await EventsAsync(env, A)); // same violation: no second event
    }

    [Fact]
    public async Task A_chain_that_grew_legitimately_stays_valid_and_the_anchor_follows_it()
    {
        await using var env = await SeededAsync();
        await RunJobAsync(env, A);
        await env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry("novo1"));
        await env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry("novo2"));

        var result = await env.Verifier.VerifyAsync(A);

        Assert.True(result.IsValid);
        Assert.Equal(7, result.Head!.Sequence);
        Assert.Equal(7, await AuditEnv.ScalarAsync(env.App, A, "SELECT ancora_sequencia FROM auditoria_verificacao"));

        // the anchor moved: removing only the two NEW records is a truncation against anchor 7
        await env.OwnerExecAsync(A, "DELETE FROM auditoria_registros WHERE sequencia IN (6, 7)");
        Assert.Contains((await env.Verifier.VerifyAsync(A)).Violations, v => v.Kind == AuditViolationKind.Truncated);
        Assert.Equal(0, await EventsAsync(env, B));
    }

    [Fact]
    public async Task An_invalid_run_never_moves_the_anchor()
    {
        await using var env = await SeededAsync();
        await env.Verifier.VerifyAsync(A); // anchor 5
        await env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry("novo"));
        await env.OwnerExecAsync(A, "UPDATE auditoria_registros SET resultado = 'falha' WHERE sequencia = 6");

        Assert.False((await env.Verifier.VerifyAsync(A)).IsValid);

        Assert.Equal(5, await AuditEnv.ScalarAsync(env.App, A, "SELECT ancora_sequencia FROM auditoria_verificacao"));
    }

    [Fact]
    public async Task An_explicit_anchor_wins_over_the_stored_one()
    {
        await using var env = await SeededAsync();
        await env.Verifier.VerifyAsync(A); // stored anchor 5
        await env.OwnerExecAsync(A, "DELETE FROM auditoria_registros WHERE sequencia = 5");

        var chain = await env.ChainAsync(A);
        var result = await env.Verifier.VerifyAsync(A, new AuditHead(chain[^1].Sequence, chain[^1].Hash)); // caller vouches for 4

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task The_application_credential_can_write_the_state_but_the_trail_stays_append_only_and_the_state_is_isolated()
    {
        await using var env = await SeededAsync();
        await env.Verifier.VerifyAsync(A);
        await env.Verifier.VerifyAsync(B);

        Assert.Equal(1, await AuditEnv.ScalarAsync(env.App, A, "SELECT count(*) FROM auditoria_verificacao"));
        Assert.Equal(1, await AuditEnv.ScalarAsync(env.App, B, "SELECT count(*) FROM auditoria_verificacao"));

        var denied = await Assert.ThrowsAsync<PostgresException>(() => AuditEnv.ScalarAsync(env.App, A, "DELETE FROM auditoria_registros"));
        Assert.Equal("42501", denied.SqlState);
        var deniedDelete = await Assert.ThrowsAsync<PostgresException>(() => AuditEnv.ScalarAsync(env.App, A, "DELETE FROM auditoria_verificacao"));
        Assert.Equal("42501", deniedDelete.SqlState);
    }
}
