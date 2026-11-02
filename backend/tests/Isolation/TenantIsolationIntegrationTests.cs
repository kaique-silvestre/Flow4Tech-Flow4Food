using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Isolation.Tests;

[Collection(PostgresDefinition.Name)]
public sealed class TenantIsolationIntegrationTests(PostgresFixture postgres)
{
    private static readonly string[] TenantTables = ["organizacoes", "unidades", "amostra_itens"];

    private async Task<(TestDatabase Db, NpgsqlDataSource App, TenantDatabase Tenant)> ArrangeAsync(string appExtra = "")
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        var app = NpgsqlDataSource.Create(db.AppConnectionString + appExtra);
        return (db, app, new TenantDatabase(app));
    }

    // ---- two credentials ----

    [Fact]
    public async Task App_credential_is_not_owner_cannot_bypass_rls_and_cannot_change_the_schema()
    {
        var (db, app, _) = await ArrangeAsync();
        await using var appSource = app;
        await using var connection = await app.OpenConnectionAsync();

        await using (var roleQuery = new NpgsqlCommand("SELECT rolsuper, rolbypassrls, rolcreaterole, rolcreatedb FROM pg_roles WHERE rolname = current_user", connection))
        await using (var reader = await roleQuery.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0), "superuser");
            Assert.False(reader.GetBoolean(1), "bypassrls");
            Assert.False(reader.GetBoolean(2), "createrole");
            Assert.False(reader.GetBoolean(3), "createdb");
        }

        await using (var owners = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner = current_user", connection))
        {
            Assert.Equal(0L, await owners.ExecuteScalarAsync());
        }

        foreach (var ddl in new[]
        {
            "ALTER TABLE amostra_itens DISABLE ROW LEVEL SECURITY",
            "DROP POLICY isolamento ON amostra_itens",
            "CREATE TABLE intruso (id int)",
            "SELECT * FROM schema_migrations",
        })
        {
            await using var command = new NpgsqlCommand(ddl, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // Turning row_security off does not bypass: PostgreSQL raises an error instead of returning rows.
        await using (var off = new NpgsqlCommand("SET row_security = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using (var select = new NpgsqlCommand("SELECT count(*) FROM amostra_itens", connection))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        Assert.NotEqual(db.OwnerConnectionString, db.AppConnectionString);
    }

    [Fact]
    public async Task Migration_credential_is_a_different_role_that_owns_the_tables()
    {
        var (db, app, _) = await ArrangeAsync();
        await using var appSource = app;
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT current_user::text, (SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner <> current_user)", owner);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(PostgresFixture.OwnerRole, reader.GetString(0));
        Assert.NotEqual(PostgresFixture.AppRole, reader.GetString(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    // ---- no context ----

    [Fact]
    public async Task Without_context_every_tenant_table_returns_zero_rows_and_writes_are_refused()
    {
        var (_, app, _) = await ArrangeAsync();
        await using var appSource = app;
        await using var connection = await app.OpenConnectionAsync();

        foreach (var table in TenantTables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO unidades (id, organizacao_id, nome) VALUES (gen_random_uuid(), @org, 'x')", connection);
        insert.Parameters.AddWithValue("org", OrgA);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    // ---- context per transaction, pooled connection reuse ----

    [Fact]
    public async Task Context_does_not_leak_when_one_pooled_connection_serves_tenant_A_then_B_then_nobody()
    {
        var (_, app, tenant) = await ArrangeAsync(";Maximum Pool Size=1;Minimum Pool Size=0");
        await using var appSource = app;

        int pidA;
        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            pidA = await BackendPid(tx.Connection, tx.Transaction);
            Assert.Equal(["A-Chopp"], await Names(tx, "amostra_itens"));
            await tx.CommitAsync();
        }

        await using (var tx = await tenant.BeginAsync(Ctx(OrgB)))
        {
            Assert.Equal(pidA, await BackendPid(tx.Connection, tx.Transaction));
            Assert.Equal(["B-Chopp"], await Names(tx, "amostra_itens"));
            Assert.Equal(["B-Centro"], await Names(tx, "unidades"));
            Assert.Equal(["B-Bar"], await Names(tx, "organizacoes"));
        }

        // Same physical connection, no transaction context: nothing visible (also after a ROLLBACK path above).
        await using var connection = await app.OpenConnectionAsync();
        Assert.Equal(pidA, await BackendPid(connection, null));
        await using var setting = new NpgsqlCommand("SELECT NULLIF(current_setting('app.organizacao_id', true), '')", connection);
        Assert.True(await setting.ExecuteScalarAsync() is null or DBNull);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM amostra_itens", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Context_is_transaction_local_even_without_the_pool_reset()
    {
        // No Npgsql reset on close: only SET LOCAL semantics stand between tenants.
        var (_, app, tenant) = await ArrangeAsync(";Maximum Pool Size=1;No Reset On Close=true");
        await using var appSource = app;

        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            await tx.CommitAsync();
        }

        await using var connection = await app.OpenConnectionAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM amostra_itens", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Empty_organization_id_is_rejected_before_touching_the_database()
    {
        var (_, app, tenant) = await ArrangeAsync();
        await using var appSource = app;
        await Assert.ThrowsAsync<ArgumentException>(() => tenant.BeginAsync(new OrganizationContext(Guid.Empty, Guid.Empty)));
    }

    // ---- A versus B ----

    [Fact]
    public async Task Tenant_A_reads_only_its_own_rows()
    {
        var (_, app, tenant) = await ArrangeAsync();
        await using var appSource = app;

        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        Assert.Equal(["A-Chopp"], await Names(tx, "amostra_itens"));
        Assert.Equal(["A-Centro"], await Names(tx, "unidades"));
        Assert.Equal(["A-Bar"], await Names(tx, "organizacoes"));

        await using var byId = new NpgsqlCommand("SELECT count(*) FROM amostra_itens WHERE id = @id", tx.Connection, tx.Transaction);
        byId.Parameters.AddWithValue("id", ItemB);
        Assert.Equal(0L, await byId.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Tenant_A_cannot_update_or_delete_tenant_B_rows()
    {
        var (_, app, tenant) = await ArrangeAsync();
        await using var appSource = app;

        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            Assert.Equal(0, await Exec(tx, "UPDATE amostra_itens SET nome = 'hacked', valor_centavos = 1 WHERE id = @id", ("id", ItemB)));
            Assert.Equal(1, await Exec(tx, "UPDATE amostra_itens SET nome = 'A-Chopp'")); // an unfiltered UPDATE only reaches A's own row
            Assert.Equal(0, await Exec(tx, "DELETE FROM amostra_itens WHERE id = @id", ("id", ItemB)));
            Assert.Equal(0, await Exec(tx, "DELETE FROM unidades WHERE id = @id", ("id", UnitB)));
            Assert.Equal(0, await Exec(tx, "DELETE FROM organizacoes WHERE id = @id", ("id", OrgB)));
            await tx.CommitAsync();
        }

        await using var verify = await tenant.BeginAsync(Ctx(OrgB));
        Assert.Equal(["B-Chopp"], await Names(verify, "amostra_itens"));
        Assert.Equal(["B-Centro"], await Names(verify, "unidades"));
        Assert.Equal(["B-Bar"], await Names(verify, "organizacoes"));
        await using var value = new NpgsqlCommand("SELECT valor_centavos FROM amostra_itens", verify.Connection, verify.Transaction);
        Assert.Equal(1077L, await value.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Tenant_A_cannot_write_rows_that_belong_to_tenant_B()
    {
        var (_, app, tenant) = await ArrangeAsync();
        await using var appSource = app;

        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            var insert = await Assert.ThrowsAsync<PostgresException>(() => Exec(
                tx,
                "INSERT INTO amostra_itens (id, organizacao_id, nome, valor_centavos) VALUES (gen_random_uuid(), @org, 'B-planted', 1)",
                ("org", OrgB)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insert.SqlState);
        }

        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            // Moving an own row into B (UPDATE ... WITH CHECK).
            var update = await Assert.ThrowsAsync<PostgresException>(() => Exec(
                tx, "UPDATE amostra_itens SET organizacao_id = @org WHERE id = @id", ("org", OrgB), ("id", ItemA)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
        }

        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            // Cross-tenant reference: item of A pointing at the unit of B is stopped by the composite foreign key.
            var fk = await Assert.ThrowsAsync<PostgresException>(() => Exec(
                tx,
                "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (gen_random_uuid(), @a, @ub, 'A-x', 1)",
                ("a", OrgA), ("ub", UnitB)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fk.SqlState);
        }

        await using var verify = await tenant.BeginAsync(Ctx(OrgB));
        Assert.Equal(["B-Chopp"], await Names(verify, "amostra_itens"));
    }

    private static async Task<int> BackendPid(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
