using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Isolation.Tests;

public sealed class ItemRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid? UnidadeId { get; set; }

    public string Nome { get; set; } = "";

    public long ValorCentavos { get; set; }
}

/// <summary>What a module's context looks like: a TenantDbContext over the sample table, constructed only from a TenantTransaction.</summary>
public sealed class SampleDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<ItemRow> Itens => Set<ItemRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ItemRow>(e =>
        {
            e.ToTable("amostra_itens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.ValorCentavos).HasColumnName("valor_centavos");
        });
    }
}

/// <summary>Deliberately wrong mapping to prove drift is detected.</summary>
public sealed class DriftedDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<ItemRow> Itens => Set<ItemRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ItemRow>(e =>
        {
            e.ToTable("amostra_itens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Nome).HasColumnName("nome_errado");
            e.Property(x => x.ValorCentavos).HasColumnName("valor_centavos").HasColumnType("integer");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id").IsRequired();
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
        });
    }
}

/// <summary>Ticket 06 isolation guarantees again, through EF Core on the real code path (TenantDbContext over TenantDatabase).</summary>
[Collection(PostgresDefinition.Name)]
public sealed class TenantDbContextIntegrationTests(PostgresFixture postgres)
{
    private async Task<(NpgsqlDataSource App, TenantDatabase Tenant)> ArrangeAsync(string appExtra = "")
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        var app = NpgsqlDataSource.Create(db.AppConnectionString + appExtra);
        return (app, new TenantDatabase(app));
    }

    [Fact]
    public async Task Reads_see_only_the_own_tenant_and_writes_of_another_tenant_are_refused()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var _ = app;

        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            await using var ef = new SampleDbContext(tx);
            Assert.Equal(["A-Chopp"], await ef.Itens.AsNoTracking().Select(i => i.Nome).ToListAsync());

            ef.Itens.Add(new ItemRow { Id = Guid.NewGuid(), OrganizacaoId = OrgB, UnidadeId = UnitB, Nome = "B-forjado", ValorCentavos = 1 });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ef.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        }

        await using (var tx = await tenant.BeginAsync(Ctx(OrgB)))
        {
            await using var ef = new SampleDbContext(tx);
            Assert.Equal(["B-Chopp"], await ef.Itens.AsNoTracking().Select(i => i.Nome).ToListAsync());
        }
    }

    [Fact]
    public async Task Updates_and_deletes_by_id_cannot_touch_another_tenants_row()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var _ = app;
        await using (var tx = await tenant.BeginAsync(Ctx(OrgA)))
        {
            await using var ef = new SampleDbContext(tx);
            Assert.Equal(0, await ef.Itens.Where(i => i.Id == ItemB).ExecuteUpdateAsync(s => s.SetProperty(i => i.Nome, "A-roubado")));
            Assert.Equal(0, await ef.Itens.Where(i => i.Id == ItemB).ExecuteDeleteAsync());
        }

        await using var check = await tenant.BeginAsync(Ctx(OrgB));
        await using var efB = new SampleDbContext(check);
        Assert.Equal(["B-Chopp"], await efB.Itens.AsNoTracking().Select(i => i.Nome).ToListAsync());
    }

    [Fact]
    public async Task Pooled_connection_never_leaks_the_context_between_ef_units_of_work()
    {
        var (app, tenant) = await ArrangeAsync(";Maximum Pool Size=1;Minimum Pool Size=1");
        await using var _ = app;
        foreach (var (org, expected) in new[] { (OrgA, "A-Chopp"), (OrgB, "B-Chopp") })
        {
            await using var tx = await tenant.BeginAsync(Ctx(org));
            await using var ef = new SampleDbContext(tx);
            Assert.Equal([expected], await ef.Itens.AsNoTracking().Select(i => i.Nome).ToListAsync());
        }

        await using var connection = await app.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM amostra_itens", connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_context_used_after_its_transaction_ended_throws_instead_of_running_without_context()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var _ = app;
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await using var ef = new SampleDbContext(tx);
        await tx.CommitAsync();

        // Npgsql/EF already refuse a completed transaction (InvalidOperationException); the guard is the second line (next test).
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ef.Itens.AsNoTracking().ToListAsync());
        ef.Itens.Add(new ItemRow { Id = Guid.NewGuid(), OrganizacaoId = OrgA, Nome = "A-tarde", ValorCentavos = 1 });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ef.SaveChangesAsync());
    }

    [Fact]
    public async Task A_stale_context_reused_after_another_tenant_took_the_same_pooled_connection_is_refused()
    {
        var (app, tenant) = await ArrangeAsync(";Maximum Pool Size=1;Minimum Pool Size=1");
        await using var _ = app;
        SampleDbContext stale;
        await using (var txA = await tenant.BeginAsync(Ctx(OrgA)))
        {
            stale = new SampleDbContext(txA);
            Assert.Equal(["A-Chopp"], await stale.Itens.AsNoTracking().Select(i => i.Nome).ToListAsync());
            await txA.CommitAsync();
        }

        await using var txB = await tenant.BeginAsync(Ctx(OrgB)); // same physical connector, tenant B's context is live
        await using (stale)
        {
            // Never B's rows, and never a silent success: the stale context is refused (guard or closed connection).
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => stale.Itens.AsNoTracking().ToListAsync()); // in practice ObjectDisposedException: the old unit of work closed its connection
        }

        await using var efB = new SampleDbContext(txB);
        Assert.Equal(["B-Chopp"], await efB.Itens.AsNoTracking().Select(i => i.Nome).ToListAsync());
    }

    [Fact]
    public async Task Guard_refuses_a_command_whose_connection_is_not_the_one_of_the_transaction_owner()
    {
        var (app, tenant) = await ArrangeAsync(";Maximum Pool Size=2");
        await using var _ = app;
        await using var txA = await tenant.BeginAsync(Ctx(OrgA));
        await using var txB = await tenant.BeginAsync(Ctx(OrgB));
        // tenant A's transaction object paired with tenant B's connection: what a stale context on a reused pooled connector would look like
        await using var crossed = new NpgsqlCommand("SELECT 1", txB.Connection, txA.Transaction);
        await Assert.ThrowsAsync<TenantContextRequiredException>(async () => await TenantCommandGuard.Instance.ReaderExecutingAsync(crossed, null!, default));
        await using var ok = new NpgsqlCommand("SELECT 1", txA.Connection, txA.Transaction);
        await TenantCommandGuard.Instance.ReaderExecutingAsync(ok, null!, default);
    }

    [Fact]
    public async Task A_context_whose_transaction_was_swapped_for_a_foreign_one_is_refused()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var _ = app;
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await using var ef = new SampleDbContext(tx);
        await ef.Database.UseTransactionAsync(null); // detach: EF would now run commands without our transaction object
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => ef.Itens.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Raw_sql_through_FromSql_and_ExecuteSql_runs_in_the_tenant_transaction()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var _ = app;
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await using var ef = new SampleDbContext(tx);
        var minimum = 500L;

        var names = await ef.Itens.FromSql($"SELECT * FROM amostra_itens WHERE valor_centavos >= {minimum} ORDER BY nome").AsNoTracking().Select(i => i.Nome).ToListAsync();
        Assert.Equal(["A-Chopp"], names); // B's row (1077 >= 500) is invisible

        Assert.Equal(1, await ef.Database.ExecuteSqlAsync($"UPDATE amostra_itens SET valor_centavos = valor_centavos + {1L}"));
        await tx.CommitAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ef.Database.ExecuteSqlAsync($"UPDATE amostra_itens SET valor_centavos = 0"));
    }

    // ---- generated SQL is inspected, not trusted (critical queries) ----

    [Fact]
    public async Task Critical_queries_generate_the_expected_sql_with_no_tenant_filter_in_the_application()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var _ = app;
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await using var ef = new SampleDbContext(tx);

        var list = SqlOnly(ef.Itens.AsNoTracking().OrderBy(i => i.Nome).ToQueryString());
        Assert.Equal(
            "SELECT a.id, a.nome, a.organizacao_id, a.unidade_id, a.valor_centavos FROM amostra_itens AS a ORDER BY a.nome",
            list);

        var id = ItemA;
        var byId = SqlOnly(ef.Itens.AsNoTracking().Where(i => i.Id == id).ToQueryString());
        Assert.Equal(
            "SELECT a.id, a.nome, a.organizacao_id, a.unidade_id, a.valor_centavos FROM amostra_itens AS a WHERE a.id = @id",
            byId);

        var minimum = 500L;
        var raw = SqlOnly(ef.Itens.FromSql($"SELECT * FROM amostra_itens WHERE valor_centavos >= {minimum}").ToQueryString());
        Assert.Contains("WHERE valor_centavos >= @p0", raw, StringComparison.Ordinal); // parameterized, never concatenated

        // The tenant is the database's job: no query filters on the organization.
        Assert.DoesNotContain(new[] { list, byId, raw }, sql => sql.Contains("organizacao_id =", StringComparison.Ordinal));
    }

    private static string SqlOnly(string sql) =>
        string.Join(' ', sql.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith("--", StringComparison.Ordinal)));

    // ---- EF model must match the SQL-owned schema ----

    [Fact]
    public async Task Model_matches_the_migrated_schema()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var _ = app;
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await using var ef = new SampleDbContext(tx);
        Assert.Empty(await SchemaDrift.FindAsync(ef));
    }

    [Fact]
    public async Task A_model_that_drifted_from_the_schema_is_reported()
    {
        var (app, tenant) = await ArrangeAsync();
        await using var _ = app;
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await using var ef = new DriftedDbContext(tx);
        var drift = await SchemaDrift.FindAsync(ef);
        Assert.Contains(drift, d => d.Contains("nome_errado", StringComparison.Ordinal));
        Assert.Contains(drift, d => d.Contains("valor_centavos", StringComparison.Ordinal) && d.Contains("integer", StringComparison.Ordinal));
        Assert.Contains(drift, d => d.Contains("unidade_id", StringComparison.Ordinal) && d.Contains("model required", StringComparison.Ordinal));
    }
}
