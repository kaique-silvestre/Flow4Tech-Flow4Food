using Npgsql;

namespace Flow4Tech.Isolation.Tests;

/// <summary>
/// Architecture rule over the REAL schema (migrations applied to PostgreSQL, catalog inspected), so a table cannot dodge
/// it by how the migration is written. Rule: every table in schema public has the tenant column, RLS enabled AND forced,
/// and exactly one policy FOR ALL TO PUBLIC whose USING and WITH CHECK equal the isolation expression. Exceptions are listed here with a reason.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class RlsArchitectureIntegrationTests(PostgresFixture postgres)
{
    /// <summary>table -> tenant column. Everything else uses <c>organizacao_id</c>.</summary>
    private static readonly Dictionary<string, string> TenantColumnOverride = new(StringComparer.Ordinal)
    {
        ["organizacoes"] = "id", // the tenant root itself
    };

    /// <summary>Tables outside the rule, with the reason. Owner-only (the app role has no privilege, proven in TenantIsolationIntegrationTests).</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["schema_migrations"] = "migration bookkeeping, owner credential only",
    };

    [Fact]
    public async Task Real_migrations_satisfy_the_rule()
    {
        var db = await postgres.CreateDatabaseAsync();
        var violations = await FindViolationsAsync(db.OwnerConnectionString);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public async Task Violating_migration_is_reported_table_by_table()
    {
        var dir = Directory.CreateTempSubdirectory("violating-migrations").FullName;
        try
        {
            foreach (var file in Directory.EnumerateFiles(PostgresFixture.RealMigrationsDirectory, "*.sql"))
            {
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            }

            await File.WriteAllTextAsync(Path.Combine(dir, "9999_violation.sql"), ViolatingSql);
            var db = await postgres.CreateDatabaseAsync(dir);

            var violations = await FindViolationsAsync(db.OwnerConnectionString);

            const string shape = "is not the isolation shape (FOR ALL TO PUBLIC, USING and WITH CHECK organizacao_id = (SELECT app_organizacao_id()))";
            Assert.Equal(["sem_coluna: missing column organizacao_id", $"sem_coluna: policy p {shape}"], For(violations, "sem_coluna"));
            Assert.Equal(["sem_rls: row level security is not enabled", "sem_rls: expected exactly 1 policy, found 0"], For(violations, "sem_rls"));
            Assert.Equal(["sem_politica: expected exactly 1 policy, found 0"], For(violations, "sem_politica"));
            Assert.Equal(["nao_forcada: row level security is not FORCED"], For(violations, "nao_forcada"));
            Assert.Equal([$"politica_aberta: policy p {shape}"], For(violations, "politica_aberta"));
            Assert.Equal(["extra_permissiva: expected exactly 1 policy, found 2", $"extra_permissiva: policy aberta {shape}"], For(violations, "extra_permissiva"));
            Assert.Equal([$"so_select: policy p {shape}"], For(violations, "so_select"));
            Assert.Equal([$"outro_papel: policy p {shape}"], For(violations, "outro_papel"));
            Assert.Equal([$"or_true: policy p {shape}"], For(violations, "or_true"));
            Assert.Empty(For(violations, "ok_tabela"));
            Assert.Empty(For(violations, "amostra_itens"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static List<string> For(List<string> violations, string table) =>
        violations.Where(v => v.StartsWith(table + ":", StringComparison.Ordinal)).ToList();

    private const string ViolatingSql = """
        CREATE TABLE sem_coluna (id uuid PRIMARY KEY);
        ALTER TABLE sem_coluna ENABLE ROW LEVEL SECURITY; ALTER TABLE sem_coluna FORCE ROW LEVEL SECURITY;
        CREATE POLICY p ON sem_coluna USING (id = (SELECT app_organizacao_id())) WITH CHECK (id = (SELECT app_organizacao_id()));

        CREATE TABLE sem_rls (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);

        CREATE TABLE sem_politica (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);
        ALTER TABLE sem_politica ENABLE ROW LEVEL SECURITY; ALTER TABLE sem_politica FORCE ROW LEVEL SECURITY;

        CREATE TABLE nao_forcada (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);
        ALTER TABLE nao_forcada ENABLE ROW LEVEL SECURITY;
        CREATE POLICY p ON nao_forcada USING (organizacao_id = (SELECT app_organizacao_id())) WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

        CREATE TABLE politica_aberta (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);
        ALTER TABLE politica_aberta ENABLE ROW LEVEL SECURITY; ALTER TABLE politica_aberta FORCE ROW LEVEL SECURITY;
        CREATE POLICY p ON politica_aberta USING (true) WITH CHECK (true);

        CREATE TABLE extra_permissiva (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);
        ALTER TABLE extra_permissiva ENABLE ROW LEVEL SECURITY; ALTER TABLE extra_permissiva FORCE ROW LEVEL SECURITY;
        CREATE POLICY p ON extra_permissiva USING (organizacao_id = (SELECT app_organizacao_id())) WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
        CREATE POLICY aberta ON extra_permissiva USING (true);

        CREATE TABLE so_select (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);
        ALTER TABLE so_select ENABLE ROW LEVEL SECURITY; ALTER TABLE so_select FORCE ROW LEVEL SECURITY;
        CREATE POLICY p ON so_select FOR SELECT USING (organizacao_id = (SELECT app_organizacao_id()));

        CREATE TABLE outro_papel (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);
        ALTER TABLE outro_papel ENABLE ROW LEVEL SECURITY; ALTER TABLE outro_papel FORCE ROW LEVEL SECURITY;
        CREATE POLICY p ON outro_papel TO flow4tech_app USING (organizacao_id = (SELECT app_organizacao_id())) WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

        CREATE TABLE or_true (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);
        ALTER TABLE or_true ENABLE ROW LEVEL SECURITY; ALTER TABLE or_true FORCE ROW LEVEL SECURITY;
        CREATE POLICY p ON or_true USING (organizacao_id = (SELECT app_organizacao_id()) OR true) WITH CHECK (organizacao_id = (SELECT app_organizacao_id()) OR true);

        CREATE TABLE ok_tabela (id uuid PRIMARY KEY, organizacao_id uuid NOT NULL);
        ALTER TABLE ok_tabela ENABLE ROW LEVEL SECURITY; ALTER TABLE ok_tabela FORCE ROW LEVEL SECURITY;
        CREATE POLICY p ON ok_tabela USING (organizacao_id = (SELECT app_organizacao_id())) WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
        """;

    private const string CatalogSql = """
        SELECT c.relname,
               EXISTS (SELECT 1 FROM pg_attribute a
                        WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped AND a.attname = @col),
               c.relrowsecurity,
               c.relforcerowsecurity
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relispartition
         ORDER BY c.relname
        """;

    /// <summary>The ONLY accepted shape: exactly one policy, FOR ALL, TO PUBLIC, USING and WITH CHECK equal to the isolation expression.</summary>
    private static async Task<List<string>> PolicyViolationsAsync(NpgsqlConnection connection, string table, string column)
    {
        var expected = Normalize($"{column} = (SELECT app_organizacao_id())");
        var violations = new List<string>();
        var policies = new List<(string Name, string[] Roles, string Cmd, string Qual, string Check)>();
        await using (var command = new NpgsqlCommand(
            "SELECT policyname::text, roles::text[], cmd, coalesce(qual, ''), coalesce(with_check, '') FROM pg_policies WHERE schemaname = 'public' AND tablename = @t",
            connection))
        {
            command.Parameters.AddWithValue("t", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                policies.Add((reader.GetString(0), await reader.GetFieldValueAsync<string[]>(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }

        if (policies.Count != 1)
        {
            violations.Add($"{table}: expected exactly 1 policy, found {policies.Count}");
        }

        foreach (var p in policies.Where(p =>
            p.Roles.Length != 1 || p.Roles[0] != "public" || p.Cmd != "ALL" || Normalize(p.Qual) != expected || Normalize(p.Check) != expected))
        {
            violations.Add($"{table}: policy {p.Name} is not the isolation shape (FOR ALL TO PUBLIC, USING and WITH CHECK {column} = (SELECT app_organizacao_id()))");
        }

        return violations;
    }

    private static string Normalize(string expression) =>
        new string(expression.Where(c => !char.IsWhiteSpace(c) && c is not '(' and not ')').ToArray())
            .ToUpperInvariant()
            .Replace("ASAPP_ORGANIZACAO_ID", string.Empty, StringComparison.Ordinal);

    /// <summary>Reads the catalog; one line per violation, "table: reason".</summary>
    private static async Task<List<string>> FindViolationsAsync(string connectionString)
    {
        var tables = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var list = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY 1", connection))
        {
            tables.AddRange(await TestDatabase.ReadAll(list));
        }

        var violations = new List<string>();
        foreach (var table in tables.Where(t => !Exempt.ContainsKey(t)))
        {
            var column = TenantColumnOverride.GetValueOrDefault(table, "organizacao_id");
            var checkPolicies = false;
            await using (var command = new NpgsqlCommand(CatalogSql, connection))
            {
                command.Parameters.AddWithValue("col", column);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (reader.GetString(0) != table)
                    {
                        continue;
                    }

                    if (!reader.GetBoolean(1))
                    {
                        violations.Add($"{table}: missing column {column}");
                    }

                    if (!reader.GetBoolean(2))
                    {
                        violations.Add($"{table}: row level security is not enabled");
                    }
                    else if (!reader.GetBoolean(3))
                    {
                        violations.Add($"{table}: row level security is not FORCED");
                    }

                    checkPolicies = true;
                }

            }

            if (checkPolicies)
            {
                violations.AddRange(await PolicyViolationsAsync(connection, table, column));
            }
        }

        return violations;
    }
}
