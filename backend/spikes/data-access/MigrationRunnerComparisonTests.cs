using System.Diagnostics;
using DbUp;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Npgsql;

namespace DataAccess.Spike;

/// <summary>
/// Same real migration (backend/migrations/0001) applied by our MigrationRunner, DbUp and Flyway, with the OWNER credential,
/// then the same questions: did it apply, can the app role read the journal table, is an edited applied file noticed,
/// do two simultaneous runners survive. Results are recorded in the findings file; asserts only pin the facts the ADR relies on.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class MigrationRunnerComparisonTests(PostgresFixture postgres)
{
    /// <summary>A database with the app role pre-created (so the fixture can give it a login) and NO tables.</summary>
    private async Task<(TestDatabase Db, string WorkDir)> EmptyDatabaseAsync()
    {
        var stubDir = Directory.CreateTempSubdirectory("spike-stub").FullName;
        await File.WriteAllTextAsync(Path.Combine(stubDir, "0000_stub.sql"), "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flow4tech_app') THEN CREATE ROLE flow4tech_app NOLOGIN NOSUPERUSER NOBYPASSRLS; END IF; END $$;");
        var db = await postgres.CreateDatabaseAsync(stubDir);
        var work = Directory.CreateTempSubdirectory("spike-mig").FullName;
        File.Copy(Path.Combine(PostgresFixture.RealMigrationsDirectory, "0001_isolamento_base.sql"), Path.Combine(work, "0001_isolamento_base.sql"));
        return (db, work);
    }

    private static async Task<long> Scalar(string connectionString, string sql)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task<long> TablesWithForcedRls(string owner) =>
        Scalar(owner, "SELECT count(*) FROM pg_class WHERE relkind='r' AND relnamespace='public'::regnamespace AND relforcerowsecurity");

    private static async Task<bool> AppCanRead(TestDatabase db, string table)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT 1 FROM {table} LIMIT 1", c);
        try
        {
            await cmd.ExecuteScalarAsync();
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return false;
        }
    }

    [Fact]
    public async Task Own_runner()
    {
        var (db, work) = await EmptyDatabaseAsync();
        var sw = Stopwatch.StartNew();
        var applied = await MigrationRunner.ApplyAsync(db.OwnerConnectionString, work);
        var ms = sw.ElapsedMilliseconds;
        Assert.Equal(["0001_isolamento_base"], applied);
        Assert.Empty(await MigrationRunner.ApplyAsync(db.OwnerConnectionString, work));
        Assert.Equal(3, await TablesWithForcedRls(db.OwnerConnectionString));
        Assert.False(await AppCanRead(db, "schema_migrations"));

        // edited applied file: refused since the runner got checksums (measured as silently ignored before that change, like DbUp)
        await File.AppendAllTextAsync(Path.Combine(work, "0001_isolamento_base.sql"), "\n-- edited\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunner.ApplyAsync(db.OwnerConnectionString, work));

        // two simultaneous runners on a fresh database: advisory lock serializes them
        var (db2, work2) = await EmptyDatabaseAsync();
        var both = await Task.WhenAll(
            MigrationRunner.ApplyAsync(db2.OwnerConnectionString, work2),
            MigrationRunner.ApplyAsync(db2.OwnerConnectionString, work2));
        Assert.Equal(1, both.Sum(x => x.Count));
        Console.WriteLine($"[migrations] own: first apply {ms} ms; edited file REFUSED (checksum); concurrent runners OK");
    }

    [Fact]
    public async Task DbUp_runner()
    {
        var (db, work) = await EmptyDatabaseAsync();
        var sw = Stopwatch.StartNew();
        var result = Build(db.OwnerConnectionString, work).PerformUpgrade();
        var ms = sw.ElapsedMilliseconds;
        Assert.True(result.Successful, result.Error?.ToString());
        Assert.Equal(3, await TablesWithForcedRls(db.OwnerConnectionString));
        Assert.False(await AppCanRead(db, "schemaversions"));
        Assert.True(Build(db.OwnerConnectionString, work).PerformUpgrade().Successful);

        await File.AppendAllTextAsync(Path.Combine(work, "0001_isolamento_base.sql"), "\n-- edited\n");
        var edited = Build(db.OwnerConnectionString, work).PerformUpgrade();
        Assert.True(edited.Successful); // edited applied file: silently ignored (journal by name)

        // two simultaneous runners: DbUp has no lock for PostgreSQL
        var (db2, work2) = await EmptyDatabaseAsync();
        var runs = await Task.WhenAll(
            Task.Run(() => Build(db2.OwnerConnectionString, work2).PerformUpgrade()),
            Task.Run(() => Build(db2.OwnerConnectionString, work2).PerformUpgrade()));
        Console.WriteLine($"[migrations] dbup: first apply {ms} ms; edited file ignored; concurrent runners successful = [{string.Join(", ", runs.Select(r => r.Successful))}]");
    }

    private static DbUp.Engine.UpgradeEngine Build(string connectionString, string dir) =>
        DeployChanges.To.PostgresqlDatabase(connectionString)
            .WithScriptsFromFileSystem(dir)
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();

    [Fact]
    public async Task Flyway_runner()
    {
        if (Environment.GetEnvironmentVariable("SPIKE_FLYWAY") != "1")
        {
            return;
        }

        var (db, work) = await EmptyDatabaseAsync();
        // Flyway wants V<version>__<description>.sql
        var flywayDir = Directory.CreateTempSubdirectory("spike-flyway").FullName;
        File.Copy(Path.Combine(work, "0001_isolamento_base.sql"), Path.Combine(flywayDir, "V0001__isolamento_base.sql"));

        var sw = Stopwatch.StartNew();
        var (code, output) = await Flyway(db.OwnerConnectionString, flywayDir, "migrate");
        var ms = sw.ElapsedMilliseconds;
        Assert.True(code == 0, output);
        Assert.Equal(3, await TablesWithForcedRls(db.OwnerConnectionString));
        Assert.False(await AppCanRead(db, "flyway_schema_history"));

        File.AppendAllText(Path.Combine(flywayDir, "V0001__isolamento_base.sql"), "\n-- edited\n");
        var (editedCode, editedOutput) = await Flyway(db.OwnerConnectionString, flywayDir, "migrate");
        Assert.NotEqual(0, editedCode); // checksum mismatch is refused
        Assert.Contains("checksum", editedOutput, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"[migrations] flyway: first apply (incl. container start) {ms} ms; edited file REFUSED (checksum)");
    }

    private static async Task<(int Code, string Output)> Flyway(string ownerConnectionString, string dir, string command)
    {
        var b = new NpgsqlConnectionStringBuilder(ownerConnectionString);
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[]
        {
            "run", "--rm", "-v", $"{dir}:/flyway/sql:ro", "flyway/flyway:11",
            $"-url=jdbc:postgresql://host.docker.internal:{b.Port}/{b.Database}", $"-user={b.Username}", $"-password={b.Password}", "-baselineOnMigrate=true", "-baselineVersion=0", command, // baseline: the fixture stub left schema_migrations behind

        })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout + await stderr);
    }
}
