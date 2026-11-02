using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Isolation.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// One real PostgreSQL for the run. Mirrors production: a superuser only bootstraps; the owner/migration role
/// (<see cref="OwnerRole"/>) is a plain role that owns each database; the app role is created BY THE MIGRATION.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string OwnerRole = "flow4tech_owner";
    public const string AppRole = "flow4tech_app";
    private const string OwnerPassword = "owner-pw";
    private const string AppPassword = "app-pw";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public static string RealMigrationsDirectory => Path.Combine(FindBackendRoot(), "migrations");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteAsSuperuser($"CREATE ROLE {OwnerRole} LOGIN PASSWORD '{OwnerPassword}' CREATEROLE");
    }

    Task IAsyncLifetime.DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Fresh database owned by the owner role, migrated from <paramref name="migrationsDirectory"/> (default: the real ones).</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(string? migrationsDirectory = null)
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await ExecuteAsSuperuser($"CREATE DATABASE {name} OWNER {OwnerRole}");
        var db = new TestDatabase(Connection(OwnerRole, OwnerPassword, name), Connection(AppRole, AppPassword, name));
        await MigrationRunner.ApplyAsync(db.OwnerConnectionString, migrationsDirectory ?? RealMigrationsDirectory);
        // Provisioning (out of band, not in the repo): the migration creates the role NOLOGIN; ops give it a login.
        await ExecuteAsSuperuser($"ALTER ROLE {AppRole} LOGIN PASSWORD '{AppPassword}'");
        return db;
    }

    private string Connection(string user, string password, string database)
    {
        var b = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = user,
            Password = password,
            Database = database,
        };
        return b.ConnectionString;
    }

    private async Task ExecuteAsSuperuser(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string FindBackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "modules.dependencies.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("backend/modules.dependencies.json not found");
    }
}

public sealed record TestDatabase(string OwnerConnectionString, string AppConnectionString)
{
    public static readonly Guid OrgA = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    public static readonly Guid OrgB = Guid.Parse("0197b000-0000-7000-8000-00000000000b");
    public static readonly Guid UnitA = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    public static readonly Guid UnitB = Guid.Parse("0197b000-0000-7000-8000-0000000000b1");
    public static readonly Guid ItemA = Guid.Parse("0197a000-0000-7000-8000-0000000000a2");
    public static readonly Guid ItemB = Guid.Parse("0197b000-0000-7000-8000-0000000000b2");

    public static OrganizationContext Ctx(Guid org) => new(org, org == OrgA ? UnitA : UnitB);

    /// <summary>Two tenants with the same data shape; every B record carries the "B-" marker and a value ending .77 (T2.1).</summary>
    public async Task SeedTwoTenantsAsync()
    {
        // The owner is subject to FORCE ROW LEVEL SECURITY too, so even seeding goes through a tenant transaction.
        await using var owner = NpgsqlDataSource.Create(OwnerConnectionString);
        var database = new TenantDatabase(owner);
        foreach (var (org, unit, item, prefix, cents) in new[]
        {
            (OrgA, UnitA, ItemA, "A", 1000L),
            (OrgB, UnitB, ItemB, "B", 1077L),
        })
        {
            await using var tx = await database.BeginAsync(Ctx(org));
            await Exec(tx, "INSERT INTO organizacoes (id, nome) VALUES (@org, @nome)", ("org", org), ("nome", $"{prefix}-Bar"));
            await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @org, @nome)", ("u", unit), ("org", org), ("nome", $"{prefix}-Centro"));
            await Exec(
                tx,
                "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@i, @org, @u, @nome, @v)",
                ("i", item), ("org", org), ("u", unit), ("nome", $"{prefix}-Chopp"), ("v", cents));
            await tx.CommitAsync();
        }
    }

    public static async Task<int> Exec(TenantTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(args);
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<string>> Names(TenantTransaction tx, string table)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var command = new NpgsqlCommand($"SELECT nome FROM {table} ORDER BY nome", tx.Connection, tx.Transaction);
        return await ReadAll(command);
    }

    public static async Task<List<string>> ReadAll(NpgsqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
