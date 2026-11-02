using System.Diagnostics;
using System.Text.Json.Nodes;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Audit.Tests;

/// <summary>Spec 5.6.1, 5.6.3, 5.6.4: append-only chained trail written in the SAME unit of work, per organization, under RLS. Real PostgreSQL, app credential.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class AuditTrailIntegrationTests(PostgresFixture postgres)
{
    private static readonly OrganizationContext A = Ctx(OrgA);
    private static readonly OrganizationContext B = Ctx(OrgB);

    [Fact]
    public async Task Records_are_chained_per_organization_with_a_sequence_and_the_previous_hash()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        for (var i = 1; i <= 3; i++)
        {
            await env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry("a" + i));
        }

        await env.RecordInUnitOfWorkAsync(B, AuditEnv.Entry("b1"));

        var chainA = await env.ChainAsync(A);
        Assert.Equal([1L, 2L, 3L], chainA.Select(r => r.Sequence));
        Assert.Null(chainA[0].Previous);
        Assert.Equal(chainA[0].Hash, chainA[1].Previous);
        Assert.Equal(chainA[1].Hash, chainA[2].Previous);
        Assert.Equal(3, chainA.Select(r => r.Hash).Distinct(StringComparer.Ordinal).Count());

        // Another organization has its own chain: starts at 1 with no previous hash, and sees none of A's.
        var chainB = await env.ChainAsync(B);
        Assert.Single(chainB);
        Assert.Equal(1, chainB[0].Sequence);
        Assert.Null(chainB[0].Previous);
        Assert.Equal(3, await env.CountAsync(A));
        Assert.Equal(1, await env.CountAsync(B));
    }

    [Fact]
    public async Task A_record_carries_who_what_where_before_after_result_correlation_and_the_clock_instant()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);
        using var activity = new Activity("teste");
        activity.Start();
        var entry = AuditEnv.Entry("u1", AuditResult.Negado);

        await env.RecordInUnitOfWorkAsync(A, entry);

        await using var tx = await env.Tenant.BeginAsync(A);
        await using var command = new NpgsqlCommand(
            "SELECT usuario_id, sessao_id, unidade_id, acao, objeto_tipo, objeto_id, antes->>'nome', depois->>'nome', resultado, correlation_id, ocorrido_em, id FROM auditoria_registros",
            tx.Connection,
            tx.Transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(entry.UserId, reader.GetGuid(0));
        Assert.Equal(entry.SessionId, reader.GetGuid(1));
        Assert.Equal(UnitA, reader.GetGuid(2));
        Assert.Equal("sessao:trocar-unidade", reader.GetString(3));
        Assert.Equal("unidade", reader.GetString(4));
        Assert.Equal("u1", reader.GetString(5));
        Assert.Equal("antes-u1", reader.GetString(6));
        Assert.Equal("depois-u1", reader.GetString(7));
        Assert.Equal("negado", reader.GetString(8));
        Assert.Equal(activity.TraceId.ToHexString(), reader.GetString(9)); // the W3C trace id of the request is the correlation id
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 0, 0, 123, 456, TimeSpan.Zero), await reader.GetFieldValueAsync<DateTimeOffset>(10)); // IClock, UTC, microseconds
        Assert.Equal(7, reader.GetGuid(11).Version); // UUID v7
    }

    [Fact]
    public async Task User_session_and_unit_are_optional_until_identity_exists()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        await env.Uow.ExecuteAsync(A, ct => env.Log.RecordAsync(A, new AuditEntry { Action = "sessao:trocar-unidade" }, ct));

        Assert.Equal(1, await env.CountAsync(A));
        Assert.Equal(1, await AuditEnv.ScalarAsync(env.App, A, "SELECT count(*) FROM auditoria_registros WHERE usuario_id IS NULL AND sessao_id IS NULL AND unidade_id IS NULL AND antes IS NULL AND depois IS NULL"));
    }

    // ---- same unit of work as the action ----

    [Fact]
    public async Task The_record_commits_with_the_action()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        await env.Uow.ExecuteAsync(A, async ct =>
        {
            await env.Log.RecordAsync(A, AuditEnv.Entry("ok"), ct);
        });

        Assert.Equal(1, await env.CountAsync(A));
    }

    [Fact]
    public async Task When_the_action_throws_the_record_disappears_with_it()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Uow.ExecuteAsync(A, async ct =>
        {
            await env.Log.RecordAsync(A, AuditEnv.Entry("falha"), ct);
            throw new InvalidOperationException("a ação falhou depois de auditar");
        }));

        Assert.Equal(0, await env.CountAsync(A));
    }

    [Fact]
    public async Task Recording_outside_a_unit_of_work_is_a_bug_not_a_silent_separate_transaction()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Log.RecordAsync(A, AuditEnv.Entry()));

        Assert.Equal(0, await env.CountAsync(A));
    }

    [Fact]
    public async Task A_deliberate_failure_record_goes_on_its_own_transaction_and_survives_the_failed_operation()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        // The operation fails and rolls back (including its normal record); the denial is recorded afterwards, on its own transaction.
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Uow.ExecuteAsync(A, async ct =>
        {
            await env.Log.RecordAsync(A, AuditEnv.Entry("normal"), ct);
            throw new InvalidOperationException("login negado");
        }));
        await env.Log.RecordDetachedAsync(A, AuditEnv.Entry("negado", AuditResult.Negado));

        var chain = await env.ChainAsync(A);
        Assert.Single(chain);
        Assert.Equal(1, await AuditEnv.ScalarAsync(env.App, A, "SELECT count(*) FROM auditoria_registros WHERE resultado = 'negado' AND objeto_id = 'negado'"));
        Assert.True((await env.Verifier.VerifyAsync(A)).IsValid);
    }

    [Fact]
    public async Task The_detached_door_refuses_to_run_inside_a_unit_of_work_because_both_would_contend_for_the_chain_lock()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Uow.ExecuteAsync(A, async ct =>
        {
            await env.Log.RecordAsync(A, AuditEnv.Entry("dentro"), ct);
            await env.Log.RecordDetachedAsync(A, AuditEnv.Entry("fora"), ct);
        }));

        Assert.Equal(0, await env.CountAsync(A));
    }

    // ---- append-only by privilege ----

    [Theory]
    [InlineData("UPDATE auditoria_registros SET resultado = 'falha'")]
    [InlineData("UPDATE auditoria_registros SET depois = '{}'::jsonb WHERE sequencia = 1")]
    [InlineData("DELETE FROM auditoria_registros")]
    [InlineData("DELETE FROM auditoria_registros WHERE sequencia = 1")]
    [InlineData("TRUNCATE auditoria_registros")]
    public async Task The_application_credential_cannot_update_delete_or_truncate_the_trail(string sql)
    {
        await using var env = await AuditEnv.CreateAsync(postgres);
        await env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry());

        await using var tx = await env.Tenant.BeginAsync(A);
        var error = await Assert.ThrowsAsync<PostgresException>(() => Exec(tx, sql));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState); // a privilege, not a convention or a policy
        Assert.Equal(1, await env.CountAsync(A));
    }

    [Fact]
    public async Task The_privileges_are_exactly_select_and_insert_for_the_application_role()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);
        await using var connection = await env.Owner.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT p, has_table_privilege('flow4tech_app', 'auditoria_registros', p) FROM unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) AS p",
            connection);
        var granted = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                granted[reader.GetString(0)] = reader.GetBoolean(1);
            }
        }

        Assert.Equal(["INSERT", "SELECT"], granted.Where(kv => kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_record_for_another_organization_is_refused_by_RLS()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);
        await using var tx = await env.Tenant.BeginAsync(A);

        var error = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            tx,
            "INSERT INTO auditoria_registros (id, organizacao_id, acao, resultado, correlation_id, ocorrido_em, sequencia, hash) VALUES (@i, @o, 'x:y', 'sucesso', 'c', now(), 1, decode(repeat('00', 32), 'hex'))",
            ("i", Guid.NewGuid()), ("o", OrgB)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task A_unit_of_another_organization_cannot_be_referenced_by_the_composite_FK()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        await using var tx = await env.Tenant.BeginAsync(A);
        var pg = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            tx,
            "INSERT INTO auditoria_registros (id, organizacao_id, unidade_id, acao, resultado, correlation_id, ocorrido_em, sequencia, hash) VALUES (@i, @o, @u, 'x:y', 'sucesso', 'c', now(), 1, decode(repeat('00', 32), 'hex'))",
            ("i", Guid.NewGuid()), ("o", OrgA), ("u", UnitB)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, pg.SqlState);
    }

    // ---- concurrency ----

    [Fact]
    public async Task Concurrent_writes_in_the_same_organization_never_fork_the_chain()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);
        const int Writers = 24;
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, Writers).Select(i => Task.Run(async () =>
        {
            start.Wait();
            await env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry("c" + i));
        })).ToList();
        start.Set();
        await Task.WhenAll(tasks);

        var chain = await env.ChainAsync(A);
        Assert.Equal(Enumerable.Range(1, Writers).Select(i => (long)i), chain.Select(r => r.Sequence)); // no gap, no duplicate
        for (var i = 1; i < chain.Count; i++)
        {
            Assert.Equal(chain[i - 1].Hash, chain[i].Previous); // each record points at the one before: a single line, no fork
        }

        var verification = await env.Verifier.VerifyAsync(A);
        Assert.True(verification.IsValid);
        Assert.Equal(Writers, verification.Records);
    }

    [Fact]
    public async Task Concurrent_writes_in_two_organizations_keep_two_independent_chains()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);
        var tasks = Enumerable.Range(0, 16).Select(i => env.RecordInUnitOfWorkAsync(i % 2 == 0 ? A : B, AuditEnv.Entry("m" + i))).ToList();

        await Task.WhenAll(tasks);

        Assert.Equal(8, (await env.Verifier.VerifyAsync(A)).Records);
        Assert.Equal(8, (await env.Verifier.VerifyAsync(B)).Records);
        Assert.True((await env.Verifier.VerifyAsync(A)).IsValid);
        Assert.True((await env.Verifier.VerifyAsync(B)).IsValid);
    }

    // ---- secrets ----

    [Theory]
    [InlineData("senha", "S3nh@Secreta!")]
    [InlineData("passwordHash", "$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy")]
    [InlineData("refreshToken", "abc")]
    [InlineData("observacao", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghijk")]
    public async Task A_secret_in_before_or_after_makes_the_write_fail_and_nothing_is_stored(string key, string value)
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        foreach (var entry in new[]
        {
            AuditEnv.Entry() with { After = new JsonObject { ["usuario"] = new JsonObject { [key] = value } } },
            AuditEnv.Entry() with { Before = new JsonObject { [key] = value } },
        })
        {
            var error = await Assert.ThrowsAsync<AuditSecretException>(() => env.RecordInUnitOfWorkAsync(A, entry));
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<AuditSecretException>(() => env.Log.RecordDetachedAsync(A, entry));
        }

        Assert.Equal(0, await env.CountAsync(A));
    }

    [Fact]
    public async Task A_number_outside_the_decimal_range_is_refused_as_a_validation_error_and_nothing_is_stored()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);
        var entry = AuditEnv.Entry() with { After = new JsonObject { ["itens"] = new JsonArray(new JsonObject { ["enorme"] = 1e300 }) } };

        var error = await Assert.ThrowsAsync<AuditValueException>(() => env.RecordInUnitOfWorkAsync(A, entry));
        await Assert.ThrowsAsync<AuditValueException>(() => env.Log.RecordDetachedAsync(A, entry));

        Assert.Contains("after.itens[0].enorme", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("300", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await env.CountAsync(A));
    }

    [Fact]
    public async Task A_secret_in_the_object_reference_is_refused_too()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        await Assert.ThrowsAsync<AuditSecretException>(() => env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry() with { ObjectId = "Bearer abcdef0123456789abcdef" }));

        Assert.Equal(0, await env.CountAsync(A));
    }

    [Theory]
    [InlineData("login")]
    [InlineData("Sessao:Trocar")]
    [InlineData("")]
    public async Task An_action_that_is_not_resource_colon_action_is_refused(string action)
    {
        await using var env = await AuditEnv.CreateAsync(postgres);

        await Assert.ThrowsAsync<ArgumentException>(() => env.RecordInUnitOfWorkAsync(A, AuditEnv.Entry() with { Action = action }));
    }
}
