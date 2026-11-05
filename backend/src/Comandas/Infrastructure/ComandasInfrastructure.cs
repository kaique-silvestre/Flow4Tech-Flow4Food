using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Comandas.Infrastructure;

internal sealed class MesaRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public string Identificacao { get; set; } = "";

    public int? Capacidade { get; set; }

    public bool Ativa { get; set; }

    public DateTimeOffset CriadaEm { get; set; }
}

/// <summary>EF mapping of the tables (schema owned by <c>0017_mesas.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class MesasDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<MesaRow> Mesas => Set<MesaRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<MesaRow>(e =>
        {
            e.ToTable("mesas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.Identificacao).HasColumnName("identificacao");
            e.Property(x => x.Capacidade).HasColumnName("capacidade");
            e.Property(x => x.Ativa).HasColumnName("ativa");
            e.Property(x => x.CriadaEm).HasColumnName("criada_em");
        });
    }
}

/// <summary>
/// The tables on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>); never filters on <c>organizacao_id</c> (RLS does). Inserting is one
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> against the unique index on the lower-cased identification, so two simultaneous inserts cannot abort the transaction: the loser gets zero rows.
/// </summary>
public sealed class PostgresMesasStore : IMesasStore
{
    public async Task<bool> TentarInserirAsync(Guid organizationId, Mesa mesa, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesa);
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, capacidade, ativa, criada_em)
            VALUES ({mesa.Id}, {organizationId}, {mesa.UnidadeId}, {mesa.Identificacao}, {mesa.Capacidade}, {mesa.Ativa}, {now})
            ON CONFLICT (organizacao_id, unidade_id, lower(identificacao)) DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Mesa>> ListarAtivasAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await Ativas(context, unitId).Select(m => new Mesa(m.Id, m.UnidadeId, m.Identificacao, m.Capacidade, m.Ativa)).ToListAsync(cancellationToken);
    }

    /// <summary>The list query, exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<MesaRow> Ativas(MesasDbContext context, Guid unitId) =>
        context.Mesas.AsNoTracking().Where(m => m.UnidadeId == unitId && m.Ativa).OrderBy(m => m.Identificacao).ThenBy(m => m.Id);
}

public static class ComandasServices
{
    /// <summary>Registers the comandas module (tables so far). Needs <c>AddTenantData</c>, <c>AddAuditoria</c>, <c>ISettings</c> and <c>IClock</c>.</summary>
    public static IServiceCollection AddComandas(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMesasStore, PostgresMesasStore>();
        services.AddSingleton<GestaoDeMesas>();
        return services;
    }
}
