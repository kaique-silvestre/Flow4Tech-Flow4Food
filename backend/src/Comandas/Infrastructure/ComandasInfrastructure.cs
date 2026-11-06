using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

internal sealed class ComandaRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public DateOnly DiaOperacional { get; set; }

    public int Numero { get; set; }

    public string Identificacao { get; set; } = "";

    public Guid? MesaId { get; set; }

    public string Canal { get; set; } = "";

    public Guid GarcomId { get; set; }

    public int? Pessoas { get; set; }

    public string Estado { get; set; } = "";

    public DateTimeOffset AbertaEm { get; set; }

    public DateTimeOffset? FechadaEm { get; set; }

    public decimal TaxaServicoPercentual { get; set; }

    public bool TaxaServicoRemovida { get; set; }

    public int Versao { get; set; }
}

internal sealed class NumeracaoRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public DateOnly DiaOperacional { get; set; }

    public int Ultimo { get; set; }
}

/// <summary>EF mapping of the tables and comandas (schemas owned by <c>0017_mesas.sql</c> and <c>0018_comandas.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class MesasDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<MesaRow> Mesas => Set<MesaRow>();

    public DbSet<ComandaRow> Comandas => Set<ComandaRow>();

    public DbSet<NumeracaoRow> Numeracoes => Set<NumeracaoRow>();

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
        modelBuilder.Entity<ComandaRow>(e =>
        {
            e.ToTable("comandas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.DiaOperacional).HasColumnName("dia_operacional");
            e.Property(x => x.Numero).HasColumnName("numero");
            e.Property(x => x.Identificacao).HasColumnName("identificacao");
            e.Property(x => x.MesaId).HasColumnName("mesa_id");
            e.Property(x => x.Canal).HasColumnName("canal");
            e.Property(x => x.GarcomId).HasColumnName("garcom_id");
            e.Property(x => x.Pessoas).HasColumnName("pessoas");
            e.Property(x => x.Estado).HasColumnName("estado");
            e.Property(x => x.AbertaEm).HasColumnName("aberta_em");
            e.Property(x => x.FechadaEm).HasColumnName("fechada_em");
            e.Property(x => x.TaxaServicoPercentual).HasColumnName("taxa_servico_percentual").HasColumnType("numeric(5,2)");
            e.Property(x => x.TaxaServicoRemovida).HasColumnName("taxa_servico_removida");
            e.Property(x => x.Versao).HasColumnName("versao");
        });
        modelBuilder.Entity<NumeracaoRow>(e =>
        {
            e.ToTable("comanda_numeracao");
            e.HasKey(x => new { x.OrganizacaoId, x.UnidadeId, x.DiaOperacional });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.DiaOperacional).HasColumnName("dia_operacional");
            e.Property(x => x.Ultimo).HasColumnName("ultimo");
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

    public async Task<Mesa?> AcharAtivaAsync(Guid organizationId, Guid unitId, Guid mesaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Mesas.AsNoTracking()
            .Where(m => m.Id == mesaId && m.UnidadeId == unitId && m.Ativa)
            .Select(m => new Mesa(m.Id, m.UnidadeId, m.Identificacao, m.Capacidade, m.Ativa))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> OcupadasAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return (await Ocupadas(context, unitId).ToListAsync(cancellationToken)).ToHashSet();
    }

    /// <summary>Tables held by an open comanda, exposed so a test can assert its SQL text.</summary>
    internal static IQueryable<Guid> Ocupadas(MesasDbContext context, Guid unitId) =>
        context.Comandas.AsNoTracking().Where(c => c.UnidadeId == unitId && c.Estado == "aberta" && c.MesaId != null).Select(c => c.MesaId!.Value).Distinct();

    /// <summary>The list query, exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<MesaRow> Ativas(MesasDbContext context, Guid unitId) =>
        context.Mesas.AsNoTracking().Where(m => m.UnidadeId == unitId && m.Ativa).OrderBy(m => m.Identificacao).ThenBy(m => m.Id);
}

/// <summary>
/// The comandas on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>); never filters on <c>organizacao_id</c> (RLS does). The display number is one
/// upsert on the per-day counter row: concurrent opens queue on the row lock (no aborted transaction, no gap, no repeat) and a rollback gives the number back.
/// </summary>
public sealed class PostgresComandasStore : IComandasStore
{
    public async Task<int> ProximoNumeroAsync(Guid organizationId, Guid unitId, DateOnly dia, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO comanda_numeracao (organizacao_id, unidade_id, dia_operacional, ultimo)
            VALUES ({organizationId}, {unitId}, {dia}, 1)
            ON CONFLICT (organizacao_id, unidade_id, dia_operacional) DO UPDATE SET ultimo = comanda_numeracao.ultimo + 1
            """,
            cancellationToken);
        return await context.Numeracoes.AsNoTracking().Where(n => n.UnidadeId == unitId && n.DiaOperacional == dia).Select(n => n.Ultimo).SingleAsync(cancellationToken);
    }

    public async Task InserirAsync(Guid organizationId, Comanda comanda, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comanda);
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var estado = RegrasDeComanda.Palavra(comanda.Estado);
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO comandas (id, organizacao_id, unidade_id, dia_operacional, numero, identificacao, mesa_id, canal, garcom_id, pessoas, estado, aberta_em, fechada_em, taxa_servico_percentual, taxa_servico_removida, versao)
            VALUES ({comanda.Id}, {organizationId}, {comanda.UnidadeId}, {comanda.DiaOperacional}, {comanda.Numero}, {comanda.Identificacao}, {comanda.MesaId}, {comanda.Canal}, {comanda.GarcomId}, {comanda.Pessoas}, {estado}, {comanda.AbertaEm}, {comanda.FechadaEm}, {comanda.TaxaServicoPercentual}, {comanda.TaxaServicoRemovida}, {comanda.Versao})
            """,
            cancellationToken);
    }

    public async Task<IReadOnlyList<Comanda>> ListarAbertasAsync(Guid organizationId, Guid unitId, Guid? garcomId, int limite, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await Abertas(context, unitId, garcomId).Take(limite).ToListAsync(cancellationToken);
        return rows.Select(c => new Comanda(c.Id, c.UnidadeId, c.DiaOperacional, c.Numero, c.Identificacao, c.MesaId, c.Canal, c.GarcomId, c.Pessoas, RegrasDeComanda.Estado(c.Estado), c.AbertaEm, c.FechadaEm, c.TaxaServicoPercentual, c.TaxaServicoRemovida, c.Versao)).ToList();
    }

    /// <summary>The list query, exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<ComandaRow> Abertas(MesasDbContext context, Guid unitId, Guid? garcomId)
    {
        var query = context.Comandas.AsNoTracking().Where(c => c.UnidadeId == unitId && c.Estado == "aberta");
        if (garcomId is { } own)
        {
            query = query.Where(c => c.GarcomId == own);
        }

        return query.OrderByDescending(c => c.AbertaEm).ThenBy(c => c.Id);
    }
}

/// <summary>Comanda events through the outbox, on the unit of work in progress (they commit or roll back with the change that raised them).</summary>
public sealed class OutboxEventosDeComandas(OutboxWriter outbox) : IEventosDeComandas
{
    public Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return outbox.AddAsync(TenantUnitOfWork.Require(organization.OrganizationId), organization, evento, cancellationToken);
    }
}

public static class ComandasServices
{
    /// <summary>Registers the comandas module (tables and comandas). Needs <c>AddTenantData</c>, <c>AddAuditoria</c>, <c>ISettings</c>, <c>IClock</c>, an <see cref="IPermissaoDaComanda"/> and an <see cref="IDiaOperacional"/> (adapted in the host).</summary>
    public static IServiceCollection AddComandas(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMesasStore, PostgresMesasStore>();
        services.AddSingleton<GestaoDeMesas>();
        services.TryAddSingleton<OutboxWriter>();
        services.AddSingleton<IComandasStore, PostgresComandasStore>();
        services.AddSingleton<IEventosDeComandas, OutboxEventosDeComandas>();
        services.AddSingleton<GestaoDeComandas>();
        return services;
    }
}
