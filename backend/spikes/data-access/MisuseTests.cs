using Dapper;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace DataAccess.Spike;

/// <summary>What happens when a developer forgets the transaction wiring or bypasses TenantDatabase: must fail loud or see zero rows, never another tenant.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class MisuseTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Dapper_without_passing_the_transaction_still_runs_inside_it_on_npgsql()
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        await using var source = NpgsqlDataSource.Create(db.AppConnectionString);
        await using var tx = await new TenantDatabase(source).BeginAsync(Ctx(OrgA));
        // transaction argument forgotten: Npgsql uses the connection's current transaction, so the context still applies.
        var names = (await tx.Connection.QueryAsync<string>("SELECT nome FROM amostra_itens")).AsList();
        Assert.Equal(["A-Chopp"], names);
    }

    [Fact]
    public async Task Ef_without_UseTransaction_still_joins_the_tenant_transaction_on_a_shared_connection()
    {
        // Measured, not assumed: EF does not throw; it enlists in the connection's open transaction, so a rollback discards the write.
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        await using var source = NpgsqlDataSource.Create(db.AppConnectionString);
        var tenant = new TenantDatabase(source);
        var id = Guid.NewGuid();
        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            await using var ef = new SpikeDbContext(tx.Connection); // UseTransaction forgotten
            ef.Itens.Add(new ItemRow { Id = id, OrganizacaoId = OrgA, UnidadeId = UnitA, Nome = "A-x", ValorCentavos = 1 });
            await ef.SaveChangesAsync();
        } // disposed without commit

        await using var check = await tenant.BeginAsync(Ctx(OrgA));
        Assert.DoesNotContain(await check.Connection.QueryAsync<Guid>("SELECT id FROM amostra_itens"), x => x == id);
    }

    [Theory]
    [InlineData("dapper")]
    [InlineData("ef")]
    public async Task Bypassing_TenantDatabase_with_a_raw_data_source_sees_zero_rows(string impl)
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        await using var source = NpgsqlDataSource.Create(db.AppConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        if (impl == "dapper")
        {
            Assert.Empty(await connection.QueryAsync<string>("SELECT nome FROM amostra_itens"));
        }
        else
        {
            await using var ef = new SpikeDbContext(connection);
            Assert.Empty(await ef.Itens.AsNoTracking().ToListAsync());
        }
    }
}
