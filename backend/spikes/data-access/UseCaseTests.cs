using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace DataAccess.Spike;

/// <summary>The same use case (insert + query amostra_itens under RLS with per-transaction context) against every option.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class UseCaseTests(PostgresFixture postgres)
{
    public static TheoryData<string> Impls => new() { "raw", "dapper", "ef" };

    private async Task<(IItemStore Store, TenantDatabase Tenant, NpgsqlDataSource Source, TestDatabase Db)> ArrangeAsync(string impl)
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        var source = NpgsqlDataSource.Create(db.AppConnectionString);
        var tenant = new TenantDatabase(source);
        return (Stores.Create(impl, tenant), tenant, source, db);
    }

    [Theory, MemberData(nameof(Impls))]
    public async Task Insert_then_query_returns_only_the_own_tenant_rows(string impl)
    {
        var (store, _, source, _) = await ArrangeAsync(impl);
        await using var _s = source;
        var id = Guid.NewGuid();
        await store.AddAsync(Ctx(OrgA), new Item(id, OrgA, UnitA, "A-Cerveja", 1234));

        var a = await store.ListAsync(Ctx(OrgA));
        Assert.Contains(a, i => i.Id == id && i.Nome == "A-Cerveja" && i.ValorCentavos == 1234);
        Assert.All(a, i => Assert.Equal(OrgA, i.OrganizacaoId));
        Assert.DoesNotContain(await store.ListAsync(Ctx(OrgB)), i => i.Id == id || i.Nome.StartsWith("A-", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Impls))]
    public async Task Inserting_a_row_of_another_tenant_is_refused_by_the_database(string impl)
    {
        var (store, _, source, _) = await ArrangeAsync(impl);
        await using var _s = source;
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => store.AddAsync(Ctx(OrgA), new Item(Guid.NewGuid(), OrgB, UnitB, "B-forjado", 1)));
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.NotNull(pg);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, pg.SqlState);
        Assert.DoesNotContain(await store.ListAsync(Ctx(OrgB)), i => i.Nome == "B-forjado");
    }

    [Theory, MemberData(nameof(Impls))]
    public async Task Context_is_transaction_local_and_never_leaks_to_the_pooled_connection(string impl)
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        await using var source = NpgsqlDataSource.Create(db.AppConnectionString + ";Maximum Pool Size=1;Minimum Pool Size=1");
        var tenant = new TenantDatabase(source);
        var store = Stores.Create(impl, tenant);

        Assert.Single(await store.ListAsync(Ctx(OrgA)));
        Assert.Single(await store.ListAsync(Ctx(OrgB)));

        // Same physical connection, no tenant transaction: zero rows, whatever the data-access library did before.
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM amostra_itens", connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Theory, MemberData(nameof(Impls))]
    public async Task Rollback_discards_the_write_and_the_context(string impl)
    {
        var (store, _, source, _) = await ArrangeAsync(impl);
        await using var _s = source;
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddThenFailAsync(Ctx(OrgA), new Item(id, OrgA, UnitA, "A-volta", 1)));
        Assert.DoesNotContain(await store.ListAsync(Ctx(OrgA)), i => i.Id == id);
    }
}
