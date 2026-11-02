using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Organizacao.Infrastructure;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Isolation.Tests;

/// <summary>Ticket 01: the Organizacao module's EF mapping matches the migrated schema, and its adapter (app credential, RLS) lists only the signed-in user's own units.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class OrganizacaoVinculosIntegrationTests(PostgresFixture postgres)
{
    private static readonly Guid UserA = Guid.Parse("0197a000-0000-7000-8000-0000000000c1");
    private static readonly Guid UserB = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");

    private async Task<(NpgsqlDataSource App, TenantDatabase Tenant)> ArrangeAsync()
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        var app = NpgsqlDataSource.Create(db.AppConnectionString);
        var tenant = new TenantDatabase(app);
        foreach (var (org, unit, user) in new[] { (OrgA, UnitA, UserA), (OrgB, UnitB, UserB) })
        {
            await using var tx = await tenant.BeginAsync(Ctx(org));
            await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", user), ("n", unit));
            await tx.CommitAsync();
        }

        return (app, tenant);
    }

    [Fact]
    public async Task The_organizacao_model_has_no_drift_against_the_migrated_database()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var appSource = app;
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await using var context = new OrganizacaoDbContext(tx);

        Assert.Empty(await SchemaDrift.FindAsync(context));
    }

    [Fact]
    public async Task The_adapter_lists_only_the_users_own_units_and_never_sees_those_of_the_other_tenant()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var appSource = app;
        var uow = new TenantUnitOfWork(tenant);
        var service = new UnidadesDoUsuario(uow, new PostgresUnidadeVinculos());

        var asA = await service.ListarAsync(new FakeRequest(UserA, Ctx(OrgA)));
        Assert.Equal([UnitA], asA.Select(u => u.Id));

        // User of A asking with B's context sees nothing of A's, and B's user cannot be confused with A's: links are per organization AND user.
        var userBInA = await service.ListarAsync(new FakeRequest(UserB, Ctx(OrgA)));
        Assert.Empty(userBInA);

        Assert.True(await service.TemVinculoAsync(UserA, Ctx(OrgA)));
        Assert.False(await service.TemVinculoAsync(UserA, Ctx(OrgB)));
        Assert.False(await service.TemVinculoAsync(UserB, Ctx(OrgA)));
        Assert.False(await service.PodeAtivarAsync(new FakeRequest(UserA, Ctx(OrgA)), UnitB));
    }

    private sealed class FakeRequest(Guid user, OrganizationContext organization) : IRequestContext
    {
        public Guid UserId => user;

        public OrganizationContext Organization => organization;
    }
}
