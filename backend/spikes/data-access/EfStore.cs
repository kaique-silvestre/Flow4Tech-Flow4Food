using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataAccess.Spike;

public sealed class ItemRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid? UnidadeId { get; set; }

    public string Nome { get; set; } = "";

    public long ValorCentavos { get; set; }
}

/// <summary>The DbContext never owns a connection string: it is handed the already-open tenant connection.</summary>
public sealed class SpikeDbContext(NpgsqlConnection connection) : DbContext
{
    public DbSet<ItemRow> Itens => Set<ItemRow>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNpgsql(connection);

    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<ItemRow>(e =>
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

/// <summary>EF Core must be joined to the tenant transaction explicitly (UseTransaction); otherwise SaveChanges starts its own.</summary>
public sealed class EfStore(TenantDatabase tenant) : IItemStore
{
    public async Task AddAsync(OrganizationContext ctx, Item item)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        await using var db = await Open(tx);
        db.Itens.Add(ToRow(item));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task AddThenFailAsync(OrganizationContext ctx, Item item)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        await using var db = await Open(tx);
        db.Itens.Add(ToRow(item));
        await db.SaveChangesAsync();
        throw new InvalidOperationException("boom");
    }

    public async Task<IReadOnlyList<Item>> ListAsync(OrganizationContext ctx)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        await using var db = await Open(tx);
        var rows = await db.Itens.AsNoTracking().OrderBy(x => x.Nome).ToListAsync();
        await tx.CommitAsync();
        return rows.ConvertAll(r => new Item(r.Id, r.OrganizacaoId, r.UnidadeId, r.Nome, r.ValorCentavos));
    }

    internal static async Task<SpikeDbContext> Open(TenantTransaction tx)
    {
        var db = new SpikeDbContext(tx.Connection);
        await db.Database.UseTransactionAsync(tx.Transaction);
        return db;
    }

    private static ItemRow ToRow(Item i) => new() { Id = i.Id, OrganizacaoId = i.OrganizacaoId, UnidadeId = i.UnidadeId, Nome = i.Nome, ValorCentavos = i.ValorCentavos };
}
