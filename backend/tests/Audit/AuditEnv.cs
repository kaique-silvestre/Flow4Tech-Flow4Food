using System.Text.Json.Nodes;
using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Audit.Tests;

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 2, 12, 0, 0, 123, 456, TimeSpan.Zero);
}

/// <summary>A migrated database with two tenants, the app credential for the code under test and the OWNER credential to simulate an attacker with database access.</summary>
internal sealed class AuditEnv : IAsyncDisposable
{
    public const string Action = "sessao:trocar-unidade";

    private AuditEnv(TestDatabase db)
    {
        Db = db;
        App = NpgsqlDataSource.Create(db.AppConnectionString);
        Owner = NpgsqlDataSource.Create(db.OwnerConnectionString);
        Tenant = new TenantDatabase(App);
        OwnerTenant = new TenantDatabase(Owner);
        Uow = new TenantUnitOfWork(Tenant);
        Log = new PostgresAuditLog(Tenant, Clock);
        Verifier = new AuditChainVerifier(Tenant, new OutboxWriter(Clock), Clock);
    }

    public TestDatabase Db { get; }

    public NpgsqlDataSource App { get; }

    public NpgsqlDataSource Owner { get; }

    public TenantDatabase Tenant { get; }

    public TenantDatabase OwnerTenant { get; }

    public FixedClock Clock { get; } = new();

    public TenantUnitOfWork Uow { get; }

    public PostgresAuditLog Log { get; }

    public AuditChainVerifier Verifier { get; }

    public static async Task<AuditEnv> CreateAsync(PostgresFixture postgres)
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        return new AuditEnv(db);
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Owner.DisposeAsync();
    }

    public static AuditEntry Entry(string name = "x", AuditResult result = AuditResult.Sucesso) => new()
    {
        Action = Action,
        Result = result,
        UserId = Guid.Parse("0197a000-0000-7000-8000-0000000000c1"),
        SessionId = Guid.Parse("0197a000-0000-7000-8000-0000000000d1"),
        UnitId = UnitA,
        ObjectType = "unidade",
        ObjectId = name,
        Before = new JsonObject { ["nome"] = "antes-" + name },
        After = new JsonObject { ["nome"] = "depois-" + name, ["quantidade"] = 3 },
    };

    public Task RecordInUnitOfWorkAsync(OrganizationContext org, AuditEntry entry) =>
        Uow.ExecuteAsync(org, ct => Log.RecordAsync(org, entry with { UnitId = org.UnitId }, ct));

    public static async Task<long> ScalarAsync(NpgsqlDataSource source, OrganizationContext org, string sql, params (string Name, object Value)[] args)
    {
        await using var tx = await new TenantDatabase(source).BeginAsync(org);
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<long> CountAsync(OrganizationContext org) => ScalarAsync(App, org, "SELECT count(*) FROM auditoria_registros");

    /// <summary>Runs SQL as the OWNER (the attacker with database access): FORCE RLS still applies, so it runs inside the victim organization's tenant transaction.</summary>
    public async Task OwnerExecAsync(OrganizationContext org, params string[] statements)
    {
        await using var tx = await OwnerTenant.BeginAsync(org);
        foreach (var sql in statements)
        {
            await Exec(tx, sql);
        }

        await tx.CommitAsync();
    }

    public async Task<List<(long Sequence, string Hash, string? Previous)>> ChainAsync(OrganizationContext org)
    {
        await using var tx = await Tenant.BeginAsync(org);
        await using var command = new NpgsqlCommand("SELECT sequencia, encode(hash, 'hex'), encode(hash_anterior, 'hex') FROM auditoria_registros ORDER BY sequencia", tx.Connection, tx.Transaction);
        var result = new List<(long, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1), await reader.IsDBNullAsync(2) ? null : reader.GetString(2)));
        }

        return result;
    }
}
