using Flow4Tech.Infrastructure.Data;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Isolation.Tests;

/// <summary>Ticket 01: the user-unit link (<c>usuario_unidades</c>) is a tenant table; tenant A never sees or creates links of tenant B (app credential).</summary>
[Collection(PostgresDefinition.Name)]
public sealed class UsuarioUnidadeIsolationIntegrationTests(PostgresFixture postgres)
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
            await InsertContaAsync(tx, org, user);
            await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", user), ("n", unit));
            await tx.CommitAsync();
        }

        return (app, tenant);
    }

    private static async Task<long> CountAsync(TenantTransaction tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Tenant_A_sees_only_its_own_links_and_units_never_those_of_B()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var appSource = app;

        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        Assert.Equal(1L, await CountAsync(tx, "SELECT count(*) FROM usuario_unidades"));
        Assert.Equal(0L, await CountAsync(tx, $"SELECT count(*) FROM usuario_unidades WHERE usuario_id = '{UserB}'"));
        Assert.Equal(1L, await CountAsync(tx, "SELECT count(*) FROM unidades"));
        Assert.Equal(0L, await CountAsync(tx, $"SELECT count(*) FROM unidades WHERE id = '{UnitB}'"));
    }

    [Fact]
    public async Task Without_context_no_link_is_visible_and_none_can_be_written()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var appSource = app;
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await Exec(tx, "SELECT set_config('app.organizacao_id', '', true)");

        Assert.Equal(0L, await CountAsync(tx, "SELECT count(*) FROM usuario_unidades"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", OrgA), ("u", Guid.NewGuid()), ("n", UnitA)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Tenant_A_cannot_write_a_link_for_B_nor_link_a_user_to_a_unit_of_B()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var appSource = app;

        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", OrgB), ("u", UserA), ("n", UnitB)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // Own organization column, but a unit that belongs to B: the composite foreign key refuses it (FK checks bypass RLS, so the key must include the organization).
        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", OrgA), ("u", UserA), ("n", UnitB)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        }

        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            Assert.Equal(0, await Exec(tx, $"UPDATE usuario_unidades SET usuario_id = usuario_id WHERE usuario_id = '{UserB}'"));
            Assert.Equal(0, await Exec(tx, $"DELETE FROM usuario_unidades WHERE usuario_id = '{UserB}'"));
        }

        await using (var tx = await tenant.BeginAsync(Ctx(OrgB)))
        {
            Assert.Equal(1L, await CountAsync(tx, "SELECT count(*) FROM usuario_unidades"));
        }
    }
}
