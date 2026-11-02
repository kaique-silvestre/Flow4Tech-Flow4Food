using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Npgsql;

namespace Flow4Tech.Isolation.Tests;

/// <summary>The deploy-time migrate entrypoint, driven by environment variables exactly as Railway's pre-deploy command is.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class MigratorIntegrationTests(PostgresFixture postgres)
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    private static string AsUrl(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        return $"postgres://{Uri.EscapeDataString(b.Username!)}:{Uri.EscapeDataString(b.Password!)}@{b.Host}:{b.Port}/{b.Database}";
    }

    private static async Task<(int Code, string Output)> Run(Func<string, string?> env)
    {
        using var output = new StringWriter();
        var code = await MigratorHost.RunAsync(env, output, CancellationToken.None);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Migrates_with_the_owner_credential_provisions_the_app_login_and_the_app_credential_is_restricted()
    {
        var db = await postgres.CreateEmptyDatabaseAsync();

        var (code, output) = await Run(Env(
            ("MIGRATION_DATABASE_URL", AsUrl(db.OwnerConnectionString)),
            ("APP_DB_PASSWORD", PostgresFixture.AppPasswordValue),
            ("DATABASE_URL", AsUrl(db.AppConnectionString)),
            ("MIGRATIONS_DIR", PostgresFixture.RealMigrationsDirectory)));

        Assert.True(code == 0, output);
        Assert.Contains("0001_isolamento_base", output, StringComparison.Ordinal);

        // The application credential now logs in, sees no tenant rows without a context, and cannot do DDL.
        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync();
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM organizacoes", app))
        {
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }

        await using var ddl = new NpgsqlCommand("CREATE TABLE tabela_do_app (id int)", app);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

        // Migrations were recorded by the owner credential.
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync();
        await using var applied = new NpgsqlCommand("SELECT count(*) FROM schema_migrations", owner);
        Assert.True((long)(await applied.ExecuteScalarAsync())! >= 3);
    }

    [Fact]
    public async Task Running_twice_is_idempotent()
    {
        var db = await postgres.CreateEmptyDatabaseAsync();
        var env = Env(
            ("ConnectionStrings__Migration", db.OwnerConnectionString),
            ("APP_DB_PASSWORD", PostgresFixture.AppPasswordValue),
            ("ConnectionStrings__Default", db.AppConnectionString),
            ("MIGRATIONS_DIR", PostgresFixture.RealMigrationsDirectory));

        Assert.Equal(0, (await Run(env)).Code);
        var (code, output) = await Run(env);

        Assert.True(code == 0, output);
        Assert.Contains("already up to date", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_owner_credential_it_exits_2_and_names_the_variables()
    {
        var (code, output) = await Run(Env(("ConnectionStrings__Default", "Host=x;Username=app;Password=SECRETVALUE")));

        Assert.Equal(2, code);
        Assert.Contains("MIGRATION_DATABASE_URL", output, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__Migration", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETVALUE", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_url_exits_2_without_echoing_the_secret()
    {
        var (code, output) = await Run(Env(("MIGRATION_DATABASE_URL", "postgres://owner:TOPSECRET@:badport/db")));

        Assert.Equal(2, code);
        Assert.DoesNotContain("TOPSECRET", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_migration_exits_1_and_does_not_leak_the_password()
    {
        var db = await postgres.CreateEmptyDatabaseAsync();
        var dir = Directory.CreateTempSubdirectory("migrator-bad").FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "0001_quebrada.sql"), "SELECT * FROM tabela_que_nao_existe;");

        var (code, output) = await Run(Env(
            ("MIGRATION_DATABASE_URL", AsUrl(db.OwnerConnectionString)),
            ("MIGRATIONS_DIR", dir)));

        Assert.Equal(1, code);
        Assert.Contains("tabela_que_nao_existe", output, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-pw", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_migrations_directory_exits_2()
    {
        var db = await postgres.CreateEmptyDatabaseAsync();

        var (code, output) = await Run(Env(
            ("MIGRATION_DATABASE_URL", AsUrl(db.OwnerConnectionString)),
            ("MIGRATIONS_DIR", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")))));

        Assert.Equal(2, code);
        Assert.Contains("MIGRATIONS_DIR", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_is_refused_when_the_application_credential_is_actually_the_owner()
    {
        var db = await postgres.CreateEmptyDatabaseAsync();

        var (code, output) = await Run(Env(
            ("MIGRATION_DATABASE_URL", AsUrl(db.OwnerConnectionString)),
            ("DATABASE_URL", AsUrl(db.OwnerConnectionString)), // misconfiguration: app runs as the owner
            ("MIGRATIONS_DIR", PostgresFixture.RealMigrationsDirectory)));

        Assert.Equal(1, code);
        Assert.Contains("not restricted", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner-pw", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_restricted_check_passes_for_the_provisioned_app_role()
    {
        var db = await postgres.CreateDatabaseAsync();

        Assert.Empty(await AppRole.FindProblemsAsync(db.AppConnectionString));
        Assert.NotEmpty(await AppRole.FindProblemsAsync(db.OwnerConnectionString));
    }

    private async Task<(TestDatabase Db, string Role, string Connection)> DatabaseWithExtraLogin(string grantSql)
    {
        var db = await postgres.CreateDatabaseAsync();
        var role = "probe_" + Guid.NewGuid().ToString("N");
        await postgres.ExecuteAsSuperuserAsync($"CREATE ROLE {role} LOGIN PASSWORD 'probe-pw' NOSUPERUSER NOBYPASSRLS");
        if (grantSql.Length > 0)
        {
            await postgres.ExecuteAsSuperuserAsync(grantSql.Replace("{role}", role, StringComparison.Ordinal));
        }

        var database = new NpgsqlConnectionStringBuilder(db.OwnerConnectionString).Database!;
        return (db, role, postgres.ConnectionFor(role, "probe-pw", database));
    }

    [Fact]
    public async Task The_check_flags_membership_in_the_owner_role()
    {
        var (_, _, connection) = await DatabaseWithExtraLogin($"GRANT {PostgresFixture.OwnerRole} TO {{role}}");

        var problems = await AppRole.FindProblemsAsync(connection);

        Assert.Contains(problems, p => p.Contains("member of the role that owns", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pg_write_all_data")]
    [InlineData("pg_read_all_data")]
    [InlineData("pg_execute_server_program")]
    public async Task The_check_flags_privileged_builtin_memberships(string builtin)
    {
        var (_, _, connection) = await DatabaseWithExtraLogin($"GRANT {builtin} TO {{role}}");

        var problems = await AppRole.FindProblemsAsync(connection);

        Assert.Contains(problems, p => p.Contains(builtin, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_check_flags_a_create_privilege_on_the_schema_even_before_trying_ddl()
    {
        var (db, role, connection) = await DatabaseWithExtraLogin(string.Empty);
        await using (var owner = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await using var grant = new NpgsqlCommand($"GRANT CREATE ON SCHEMA public TO {role}", owner);
            await grant.ExecuteNonQueryAsync();
        }

        var problems = await AppRole.FindProblemsAsync(connection);

        Assert.Contains(problems, p => p.Contains("CREATE privilege on schema public", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plain_login_role_without_extra_grants_has_no_problems()
    {
        var (_, _, connection) = await DatabaseWithExtraLogin(string.Empty);

        Assert.Empty(await AppRole.FindProblemsAsync(connection));
    }

    [Fact]
    public async Task A_missing_application_credential_fails_the_deploy_by_default()
    {
        var db = await postgres.CreateEmptyDatabaseAsync();

        var (code, output) = await Run(Env(
            ("MIGRATION_DATABASE_URL", AsUrl(db.OwnerConnectionString)),
            ("MIGRATIONS_DIR", PostgresFixture.RealMigrationsDirectory)));

        Assert.Equal(1, code);
        Assert.Contains("SKIP_APP_CHECK", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SKIP_APP_CHECK_is_the_explicit_opt_out_for_a_migrate_only_run()
    {
        var db = await postgres.CreateEmptyDatabaseAsync();

        var (code, output) = await Run(Env(
            ("MIGRATION_DATABASE_URL", AsUrl(db.OwnerConnectionString)),
            ("MIGRATIONS_DIR", PostgresFixture.RealMigrationsDirectory),
            ("SKIP_APP_CHECK", "1")));

        Assert.True(code == 0, output);
        Assert.Contains("skipped", output, StringComparison.OrdinalIgnoreCase);
    }
}
