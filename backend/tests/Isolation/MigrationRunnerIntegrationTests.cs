using Flow4Tech.Infrastructure.Data;
using Npgsql;

namespace Flow4Tech.Isolation.Tests;

[Collection(PostgresDefinition.Name)]
public sealed class MigrationRunnerIntegrationTests(PostgresFixture postgres)
{
    private static string CopyOfRealMigrations()
    {
        var dir = Directory.CreateTempSubdirectory("migrations-copy").FullName;
        foreach (var file in Directory.EnumerateFiles(PostgresFixture.RealMigrationsDirectory, "*.sql"))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }

        return dir;
    }

    [Fact]
    public async Task Unchanged_applied_files_are_skipped_and_new_files_are_applied()
    {
        var db = await postgres.CreateDatabaseAsync();
        var dir = CopyOfRealMigrations();
        Assert.Empty(await MigrationRunner.ApplyAsync(db.OwnerConnectionString, dir));

        await File.WriteAllTextAsync(Path.Combine(dir, "9999_novo.sql"), "CREATE TABLE sem_dados_de_negocio (id int);");
        Assert.Equal(["9999_novo"], await MigrationRunner.ApplyAsync(db.OwnerConnectionString, dir));
    }

    [Fact]
    public async Task Editing_an_applied_file_is_refused_with_the_version_named()
    {
        var db = await postgres.CreateDatabaseAsync();
        var dir = CopyOfRealMigrations();
        await File.AppendAllTextAsync(Path.Combine(dir, "0001_isolamento_base.sql"), "\n-- edited after being applied\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunner.ApplyAsync(db.OwnerConnectionString, dir));
        Assert.Contains("0001_isolamento_base", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Line_ending_differences_are_not_an_edit()
    {
        var db = await postgres.CreateDatabaseAsync();
        var dir = CopyOfRealMigrations();
        var path = Path.Combine(dir, "0001_isolamento_base.sql");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Empty(await MigrationRunner.ApplyAsync(db.OwnerConnectionString, dir));
    }

    [Fact]
    public async Task Two_simultaneous_runners_apply_each_file_once()
    {
        var db = await postgres.CreateDatabaseAsync();
        var dir = CopyOfRealMigrations();
        await File.WriteAllTextAsync(Path.Combine(dir, "9999_novo.sql"), "CREATE TABLE sem_dados_de_negocio (id int);");
        var runs = await Task.WhenAll(MigrationRunner.ApplyAsync(db.OwnerConnectionString, dir), MigrationRunner.ApplyAsync(db.OwnerConnectionString, dir));
        Assert.Equal(1, runs.Sum(r => r.Count));
    }

    [Fact]
    public async Task A_journal_created_by_the_previous_runner_version_without_checksums_is_upgraded_not_rejected()
    {
        var db = await postgres.CreateDatabaseAsync();
        await using (var c = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await c.OpenAsync();
            await using var drop = new NpgsqlCommand("ALTER TABLE schema_migrations DROP COLUMN checksum", c);
            await drop.ExecuteNonQueryAsync();
        }

        Assert.Empty(await MigrationRunner.ApplyAsync(db.OwnerConnectionString, CopyOfRealMigrations()));
    }
}
