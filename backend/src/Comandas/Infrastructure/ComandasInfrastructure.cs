using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using System.Text.Json.Nodes;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
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

    public Guid? AreaId { get; set; }
}

internal sealed class AreaRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public string Nome { get; set; } = "";

    public bool Ativa { get; set; }

    public DateTimeOffset CriadaEm { get; set; }
}

/// <summary>Projection of the map query: one row per table held by open comandas.</summary>
internal sealed class OcupacaoRow
{
    public Guid MesaId { get; set; }

    public int Abertas { get; set; }

    public DateTimeOffset PrimeiraAbertura { get; set; }
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

    public string? Observacao { get; set; }

    public string? MotivoCancelamento { get; set; }
}

internal sealed class RodadaRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public Guid ComandaId { get; set; }

    public Guid EnviadaPor { get; set; }

    public DateTimeOffset EnviadaEm { get; set; }

    public DateTimeOffset? DesfeitaEm { get; set; }

    public int ComandaVersao { get; set; }
}

internal sealed class ItemRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid ComandaId { get; set; }

    public Guid RodadaId { get; set; }

    public int Posicao { get; set; }

    public Guid ProdutoId { get; set; }

    public string ProdutoNome { get; set; } = "";

    public long PrecoCentavos { get; set; }

    public int Quantidade { get; set; }

    public string? Observacao { get; set; }

    public string Estado { get; set; } = "";

    public string? MotivoCancelamento { get; set; }

    public Guid? CanceladoPor { get; set; }

    public DateTimeOffset? CanceladoEm { get; set; }
}

internal sealed class ItemOpcaoRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid ItemId { get; set; }

    public Guid OpcaoId { get; set; }

    public Guid GrupoId { get; set; }

    public string Nome { get; set; } = "";

    public long PrecoCentavos { get; set; }
}

internal sealed class NumeracaoRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public DateOnly DiaOperacional { get; set; }

    public int Ultimo { get; set; }
}

/// <summary>EF mapping of the tables, areas and comandas (schemas owned by <c>0017_mesas.sql</c>, <c>0018_comandas.sql</c> and <c>0024_areas.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class MesasDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<MesaRow> Mesas => Set<MesaRow>();

    public DbSet<AreaRow> Areas => Set<AreaRow>();

    public DbSet<ComandaRow> Comandas => Set<ComandaRow>();

    public DbSet<NumeracaoRow> Numeracoes => Set<NumeracaoRow>();

    public DbSet<RodadaRow> Rodadas => Set<RodadaRow>();

    public DbSet<ItemRow> Itens => Set<ItemRow>();

    public DbSet<ItemOpcaoRow> ItemOpcoes => Set<ItemOpcaoRow>();

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
            e.Property(x => x.AreaId).HasColumnName("area_id");
        });
        modelBuilder.Entity<AreaRow>(e =>
        {
            e.ToTable("areas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.Nome).HasColumnName("nome");
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
            e.Property(x => x.Observacao).HasColumnName("observacao");
            e.Property(x => x.MotivoCancelamento).HasColumnName("motivo_cancelamento");
        });
        modelBuilder.Entity<RodadaRow>(e =>
        {
            e.ToTable("rodadas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.ComandaId).HasColumnName("comanda_id");
            e.Property(x => x.EnviadaPor).HasColumnName("enviada_por");
            e.Property(x => x.EnviadaEm).HasColumnName("enviada_em");
            e.Property(x => x.DesfeitaEm).HasColumnName("desfeita_em");
            e.Property(x => x.ComandaVersao).HasColumnName("comanda_versao");
        });
        modelBuilder.Entity<ItemRow>(e =>
        {
            e.ToTable("itens_comanda");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.ComandaId).HasColumnName("comanda_id");
            e.Property(x => x.RodadaId).HasColumnName("rodada_id");
            e.Property(x => x.Posicao).HasColumnName("posicao");
            e.Property(x => x.ProdutoId).HasColumnName("produto_id");
            e.Property(x => x.ProdutoNome).HasColumnName("produto_nome");
            e.Property(x => x.PrecoCentavos).HasColumnName("preco_centavos");
            e.Property(x => x.Quantidade).HasColumnName("quantidade");
            e.Property(x => x.Observacao).HasColumnName("observacao");
            e.Property(x => x.Estado).HasColumnName("estado");
            e.Property(x => x.MotivoCancelamento).HasColumnName("motivo_cancelamento");
            e.Property(x => x.CanceladoPor).HasColumnName("cancelado_por");
            e.Property(x => x.CanceladoEm).HasColumnName("cancelado_em");
        });
        modelBuilder.Entity<ItemOpcaoRow>(e =>
        {
            e.ToTable("item_opcoes");
            e.HasKey(x => new { x.OrganizacaoId, x.ItemId, x.OpcaoId });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.ItemId).HasColumnName("item_id");
            e.Property(x => x.OpcaoId).HasColumnName("opcao_id");
            e.Property(x => x.GrupoId).HasColumnName("grupo_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.PrecoCentavos).HasColumnName("preco_centavos");
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
            INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, capacidade, ativa, criada_em, area_id)
            VALUES ({mesa.Id}, {organizationId}, {mesa.UnidadeId}, {mesa.Identificacao}, {mesa.Capacidade}, {mesa.Ativa}, {now}, {mesa.AreaId})
            ON CONFLICT (organizacao_id, unidade_id, lower(identificacao)) DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Mesa>> ListarAtivasAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await Ativas(context, unitId).Select(m => new Mesa(m.Id, m.UnidadeId, m.Identificacao, m.Capacidade, m.Ativa, m.AreaId)).ToListAsync(cancellationToken);
    }

    public async Task<Mesa?> AcharAtivaAsync(Guid organizationId, Guid unitId, Guid mesaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Mesas.AsNoTracking()
            .Where(m => m.Id == mesaId && m.UnidadeId == unitId && m.Ativa)
            .Select(m => new Mesa(m.Id, m.UnidadeId, m.Identificacao, m.Capacidade, m.Ativa, m.AreaId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Mesa?> AcharAsync(Guid organizationId, Guid unitId, Guid mesaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Mesas.AsNoTracking()
            .Where(m => m.Id == mesaId && m.UnidadeId == unitId)
            .Select(m => new Mesa(m.Id, m.UnidadeId, m.Identificacao, m.Capacidade, m.Ativa, m.AreaId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, OcupacaoDaMesa>> OcupacaoAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await Ocupacao(context, unitId).ToListAsync(cancellationToken);
        // Waiting for the bill needs the pre-bill (F4-0067): until it lands no comanda has asked for it.
        return rows.ToDictionary(r => r.MesaId, r => new OcupacaoDaMesa(r.Abertas, 0, r.PrimeiraAbertura));
    }

    public async Task<IReadOnlyList<Area>> ListarAreasAtivasAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await AreasAtivas(context, unitId).Select(a => new Area(a.Id, a.UnidadeId, a.Nome, a.Ativa)).ToListAsync(cancellationToken);
    }

    public async Task<Area?> AcharAreaAtivaAsync(Guid organizationId, Guid unitId, Guid areaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Areas.AsNoTracking()
            .Where(a => a.Id == areaId && a.UnidadeId == unitId && a.Ativa)
            .Select(a => new Area(a.Id, a.UnidadeId, a.Nome, a.Ativa))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Area?> AcharAreaAsync(Guid organizationId, Guid unitId, Guid areaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Areas.AsNoTracking()
            .Where(a => a.Id == areaId && a.UnidadeId == unitId)
            .Select(a => new Area(a.Id, a.UnidadeId, a.Nome, a.Ativa))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TentarInserirAreaAsync(Guid organizationId, Area area, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        await TravarAreasAsync(context, area.UnidadeId, cancellationToken);
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO areas (id, organizacao_id, unidade_id, nome, ativa, criada_em)
            VALUES ({area.Id}, {organizationId}, {area.UnidadeId}, {area.Nome}, {area.Ativa}, {now})
            ON CONFLICT (organizacao_id, unidade_id, lower(nome)) DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> TentarAtualizarAreaAsync(Guid organizationId, Guid unitId, Guid areaId, string nome, bool ativa, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        await TravarAreasAsync(context, unitId, cancellationToken);
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE areas SET nome = {nome}, ativa = {ativa}
            WHERE id = {areaId} AND unidade_id = {unitId}
              AND NOT EXISTS (SELECT 1 FROM areas o WHERE o.unidade_id = {unitId} AND o.id <> {areaId} AND lower(o.nome) = lower({nome}))
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> DefinirAreaDaMesaAsync(Guid organizationId, Guid unitId, Guid mesaId, Guid? areaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE mesas SET area_id = {areaId} WHERE id = {mesaId} AND unidade_id = {unitId} AND ativa",
            cancellationToken);
        return rows == 1;
    }

    /// <summary>
    /// Serializes the writers of one unit's areas until the unit of work ends, so two simultaneous creates/renames to the same name cannot abort a transaction on the unique index:
    /// the second one waits, then sees the first one's row.
    /// </summary>
    private static Task<int> TravarAreasAsync(MesasDbContext context, Guid unitId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"areas:" + unitId.ToString("D")}, 0))", cancellationToken);

    public async Task<IReadOnlySet<Guid>> OcupadasAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return (await Ocupadas(context, unitId).ToListAsync(cancellationToken)).ToHashSet();
    }

    /// <summary>Tables held by an open comanda, exposed so a test can assert its SQL text.</summary>
    internal static IQueryable<Guid> Ocupadas(MesasDbContext context, Guid unitId) =>
        context.Comandas.AsNoTracking().Where(c => c.UnidadeId == unitId && c.Estado == "aberta" && c.MesaId != null).Select(c => c.MesaId!.Value).Distinct();

    /// <summary>Open comandas per table (count and first opening), exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<OcupacaoRow> Ocupacao(MesasDbContext context, Guid unitId) =>
        context.Comandas.AsNoTracking()
            .Where(c => c.UnidadeId == unitId && c.Estado == "aberta" && c.MesaId != null)
            .GroupBy(c => c.MesaId!.Value)
            .Select(g => new OcupacaoRow { MesaId = g.Key, Abertas = g.Count(), PrimeiraAbertura = g.Min(c => c.AbertaEm) });

    /// <summary>The active areas of the unit by name, exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<AreaRow> AreasAtivas(MesasDbContext context, Guid unitId) =>
        context.Areas.AsNoTracking().Where(a => a.UnidadeId == unitId && a.Ativa).OrderBy(a => a.Nome).ThenBy(a => a.Id);

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

    public async Task<Comanda?> AcharAsync(Guid organizationId, Guid unitId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var c = await Achar(context, unitId, comandaId).FirstOrDefaultAsync(cancellationToken);
        return c is null ? null : ParaComanda(c);
    }

    public async Task<bool> AvancarVersaoAsync(Guid organizationId, Guid comandaId, int versaoEsperada, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE comandas SET versao = versao + 1 WHERE id = {comandaId} AND versao = {versaoEsperada} AND estado = 'aberta'",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> AvancarVersaoSemConferirAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE comandas SET versao = versao + 1 WHERE id = {comandaId} AND estado = 'aberta'", cancellationToken);
        return rows == 1;
    }

    public async Task<bool> FecharAsync(Guid organizationId, Guid comandaId, DateTimeOffset fechadaEm, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE comandas SET estado = 'fechada', fechada_em = {fechadaEm} WHERE id = {comandaId} AND estado = 'aberta'",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> TrocarMesaAsync(Guid organizationId, Guid comandaId, Guid mesaId, string identificacao, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE comandas SET mesa_id = {mesaId}, identificacao = {identificacao} WHERE id = {comandaId} AND estado = 'aberta'",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> RenomearAsync(Guid organizationId, Guid comandaId, string identificacao, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE comandas SET identificacao = {identificacao} WHERE id = {comandaId} AND estado = 'aberta'", cancellationToken);
        return rows == 1;
    }

    public async Task<bool> TrocarGarcomAsync(Guid organizationId, Guid comandaId, Guid garcomId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE comandas SET garcom_id = {garcomId} WHERE id = {comandaId} AND estado = 'aberta'", cancellationToken);
        return rows == 1;
    }

    public async Task<bool> AtualizarDadosAsync(Guid organizationId, Guid comandaId, int? pessoas, string? observacao, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE comandas SET pessoas = {pessoas}, observacao = {observacao} WHERE id = {comandaId} AND estado = 'aberta'", cancellationToken);
        return rows == 1;
    }

    public async Task<bool> CancelarAsync(Guid organizationId, Guid comandaId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE comandas SET estado = 'cancelada', fechada_em = {quando}, motivo_cancelamento = {motivo}, cancelada_por = {por} WHERE id = {comandaId} AND estado = 'aberta'",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> RemoverTaxaServicoAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE comandas SET taxa_servico_removida = true WHERE id = {comandaId} AND estado = 'aberta' AND NOT taxa_servico_removida",
            cancellationToken);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Comanda>> ListarAbertasAsync(Guid organizationId, Guid unitId, Guid? garcomId, int limite, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await Abertas(context, unitId, garcomId).Take(limite).ToListAsync(cancellationToken);
        return rows.Select(c => ParaComanda(c)).ToList();
    }

    public async Task<Comanda?> UltimaFechadaDaMesaAsync(Guid organizationId, Guid unitId, Guid mesaId, Guid? garcomId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var c = await UltimaFechadaDaMesa(context, unitId, mesaId, garcomId).FirstOrDefaultAsync(cancellationToken);
        return c is null ? null : ParaComanda(c);
    }

    private static Comanda ParaComanda(ComandaRow c) =>
        new(c.Id, c.UnidadeId, c.DiaOperacional, c.Numero, c.Identificacao, c.MesaId, c.Canal, c.GarcomId, c.Pessoas, RegrasDeComanda.Estado(c.Estado), c.AbertaEm, c.FechadaEm, c.TaxaServicoPercentual, c.TaxaServicoRemovida, c.Versao, c.Observacao, c.MotivoCancelamento);

    /// <summary>The last-closed query, exposed so a test can assert its SQL text (unit filter explicit, no organization filter: RLS does it).</summary>
    internal static IQueryable<ComandaRow> UltimaFechadaDaMesa(MesasDbContext context, Guid unitId, Guid mesaId, Guid? garcomId)
    {
        var query = context.Comandas.AsNoTracking().Where(c => c.UnidadeId == unitId && c.MesaId == mesaId && c.Estado == "fechada");
        if (garcomId is { } own)
        {
            query = query.Where(c => c.GarcomId == own);
        }

        return query.OrderByDescending(c => c.FechadaEm).ThenBy(c => c.Id).Take(1);
    }

    /// <summary>The lookup query, exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<ComandaRow> Achar(MesasDbContext context, Guid unitId, Guid comandaId) =>
        context.Comandas.AsNoTracking().Where(c => c.UnidadeId == unitId && c.Id == comandaId);

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

/// <summary>
/// The rounds and items on PostgreSQL. Joins the caller's unit of work; never filters on <c>organizacao_id</c> (RLS does). The round id is the primary key: a concurrent repeat cannot add a second
/// round (the comanda row lock taken by <see cref="PostgresComandasStore.AvancarVersaoAsync"/> already serializes writers of one comanda).
/// </summary>
public sealed class PostgresRodadasStore : IRodadasStore
{
    public async Task<RodadaComItens?> AcharRodadaAsync(Guid organizationId, Guid rodadaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rodada = await context.Rodadas.AsNoTracking().Where(r => r.Id == rodadaId).FirstOrDefaultAsync(cancellationToken);
        if (rodada is null)
        {
            return null;
        }

        var itens = await ItensDaRodada(context, rodadaId).ToListAsync(cancellationToken);
        var escolhas = await EscolhasAsync(context, itens, cancellationToken);
        return new RodadaComItens(new Rodada(rodada.Id, rodada.ComandaId, rodada.UnidadeId, rodada.EnviadaPor, rodada.EnviadaEm, rodada.DesfeitaEm, rodada.ComandaVersao), itens.Select(i => ParaItem(i, escolhas)).ToList());
    }

    public async Task<IReadOnlyList<ItemDaComanda>> ListarItensAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await ItensDaComanda(context, comandaId).ToListAsync(cancellationToken);
        var escolhas = await EscolhasAsync(context, rows, cancellationToken);
        return rows.Select(i => ParaItem(i, escolhas)).ToList();
    }

    public async Task<ItemDaComanda?> AcharItemAsync(Guid organizationId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var row = await context.Itens.AsNoTracking().Where(i => i.Id == itemId).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ParaItem(row, await EscolhasAsync(context, [row], cancellationToken));
    }

    public async Task InserirRodadaAsync(Guid organizationId, RodadaComItens rodada, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rodada);
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var r = rodada.Rodada;
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO rodadas (id, organizacao_id, unidade_id, comanda_id, enviada_por, enviada_em, desfeita_em, comanda_versao)
            VALUES ({r.Id}, {organizationId}, {r.UnidadeId}, {r.ComandaId}, {r.EnviadaPor}, {r.EnviadaEm}, {r.DesfeitaEm}, {r.ComandaVersao})
            """,
            cancellationToken);
        foreach (var i in rodada.Itens)
        {
            var estado = RegrasDeRodada.Palavra(i.Estado);
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO itens_comanda (id, organizacao_id, comanda_id, rodada_id, posicao, produto_id, produto_nome, preco_centavos, quantidade, observacao, estado, motivo_cancelamento, cancelado_por, cancelado_em)
                VALUES ({i.Id}, {organizationId}, {i.ComandaId}, {i.RodadaId}, {i.Posicao}, {i.ProdutoId}, {i.ProdutoNome}, {i.PrecoCentavos}, {i.Quantidade}, {i.Observacao}, {estado}, {i.MotivoCancelamento}, {i.CanceladoPor}, {i.CanceladoEm})
                """,
                cancellationToken);
            foreach (var o in i.Opcoes)
            {
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO item_opcoes (organizacao_id, item_id, opcao_id, grupo_id, nome, preco_centavos)
                    VALUES ({organizationId}, {i.Id}, {o.OpcaoId}, {o.GrupoId}, {o.Nome}, {o.PrecoCentavos})
                    """,
                    cancellationToken);
            }
        }
    }

    public async Task RegistrarFrequenciaAsync(Guid organizationId, Guid unitId, Guid garcomId, IReadOnlyCollection<Guid> produtos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(produtos);
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        foreach (var produto in produtos)
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO produtos_frequencia (organizacao_id, unidade_id, produto_id, lancamentos)
                VALUES ({organizationId}, {unitId}, {produto}, 1)
                ON CONFLICT (organizacao_id, unidade_id, produto_id) DO UPDATE SET lancamentos = produtos_frequencia.lancamentos + 1
                """,
                cancellationToken);
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO produtos_frequencia_garcom (organizacao_id, unidade_id, garcom_id, produto_id, lancamentos)
                VALUES ({organizationId}, {unitId}, {garcomId}, {produto}, 1)
                ON CONFLICT (organizacao_id, unidade_id, garcom_id, produto_id) DO UPDATE SET lancamentos = produtos_frequencia_garcom.lancamentos + 1
                """,
                cancellationToken);
        }
    }

    public async Task RegistrarFrequenciaDeOpcoesAsync(Guid organizationId, Guid unitId, IReadOnlyCollection<(Guid ProdutoId, Guid OpcaoId)> usos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usos);
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        foreach (var (produto, opcao) in usos)
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO opcoes_frequencia (organizacao_id, unidade_id, produto_id, opcao_id, lancamentos)
                VALUES ({organizationId}, {unitId}, {produto}, {opcao}, 1)
                ON CONFLICT (organizacao_id, unidade_id, produto_id, opcao_id) DO UPDATE SET lancamentos = opcoes_frequencia.lancamentos + 1
                """,
                cancellationToken);
        }
    }

    public async Task<bool> CancelarItemAsync(Guid organizationId, Guid itemId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE itens_comanda SET estado = 'cancelado', motivo_cancelamento = {motivo}, cancelado_por = {por}, cancelado_em = {quando} WHERE id = {itemId} AND estado = 'lancado'",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> AlterarItemAsync(Guid organizationId, Guid itemId, int quantidade, string? observacao, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE itens_comanda SET quantidade = {quantidade}, observacao = {observacao} WHERE id = {itemId} AND estado = 'lancado'", cancellationToken);
        return rows == 1;
    }

    public async Task<int> CancelarItensVivosAsync(Guid organizationId, Guid comandaId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Database.ExecuteSqlAsync(
            $"UPDATE itens_comanda SET estado = 'cancelado', motivo_cancelamento = {motivo}, cancelado_por = {por}, cancelado_em = {quando} WHERE comanda_id = {comandaId} AND estado = 'lancado'",
            cancellationToken);
    }

    public async Task<bool> DesfazerRodadaAsync(Guid organizationId, Guid rodadaId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE rodadas SET desfeita_em = {quando} WHERE id = {rodadaId} AND desfeita_em IS NULL", cancellationToken);
        if (rows != 1)
        {
            return false;
        }

        await context.Database.ExecuteSqlAsync(
            $"UPDATE itens_comanda SET estado = 'cancelado', motivo_cancelamento = {motivo}, cancelado_por = {por}, cancelado_em = {quando} WHERE rodada_id = {rodadaId} AND estado = 'lancado'",
            cancellationToken);
        return true;
    }

    /// <summary>The items of one comanda in launch order (round send time, then position in the round), exposed so a test can assert its SQL text.</summary>
    internal static IQueryable<ItemRow> ItensDaComanda(MesasDbContext context, Guid comandaId) =>
        context.Itens.AsNoTracking().Where(i => i.ComandaId == comandaId)
            .Join(context.Rodadas.AsNoTracking(), i => i.RodadaId, r => r.Id, (i, r) => new { Item = i, r.EnviadaEm })
            .OrderBy(x => x.EnviadaEm).ThenBy(x => x.Item.RodadaId).ThenBy(x => x.Item.Posicao)
            .Select(x => x.Item);

    /// <summary>The items of one round in launch order, exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<ItemRow> ItensDaRodada(MesasDbContext context, Guid rodadaId) =>
        context.Itens.AsNoTracking().Where(i => i.RodadaId == rodadaId).OrderBy(i => i.Posicao);

    /// <summary>The frozen options of these items, in the order they were chosen at launch (name, then id: a stable order).</summary>
    private static async Task<ILookup<Guid, OpcaoDoItem>> EscolhasAsync(MesasDbContext context, IReadOnlyList<ItemRow> itens, CancellationToken cancellationToken)
    {
        var ids = itens.Select(i => i.Id).ToList();
        var rows = ids.Count == 0 ? [] : await context.ItemOpcoes.AsNoTracking().Where(o => ids.Contains(o.ItemId)).OrderBy(o => o.Nome).ThenBy(o => o.OpcaoId).ToListAsync(cancellationToken);
        return rows.ToLookup(o => o.ItemId, o => new OpcaoDoItem(o.OpcaoId, o.GrupoId, o.Nome, o.PrecoCentavos));
    }

    private static ItemDaComanda ParaItem(ItemRow i, ILookup<Guid, OpcaoDoItem> escolhas) =>
        new(i.Id, i.ComandaId, i.RodadaId, i.Posicao, i.ProdutoId, i.ProdutoNome, i.PrecoCentavos, i.Quantidade, i.Observacao, RegrasDeRodada.EstadoDe(i.Estado), i.MotivoCancelamento, i.CanceladoPor, i.CanceladoEm, escolhas[i.Id].ToList());
}

internal sealed class HistoricoRow
{
    public Guid Id { get; set; }

    public string Acao { get; set; } = "";

    public DateTimeOffset Quando { get; set; }

    public Guid? UsuarioId { get; set; }

    public string? UsuarioNome { get; set; }

    public string? Antes { get; set; }

    public string? Depois { get; set; }
}

/// <summary>
/// The timeline of a comanda from the audit trail (<c>auditoria_registros</c>, successes only): records about the comanda itself, about its rounds and items (their ids are the audit object) and about its
/// payments (the record carries the comanda id). RLS filters the organization. Only top-level scalar fields are exposed, and never a key <see cref="SensitiveData"/> calls sensitive (the audit already refuses
/// to store one; this is the second wall).
/// </summary>
public sealed class PostgresHistoricoDaComanda : IHistoricoDaComanda
{
    private static readonly HashSet<string> Escondidos = new(StringComparer.Ordinal) { "versao" };

    public async Task<IReadOnlyList<EventoDoHistorico>> ListarAsync(Guid organizationId, Guid comandaId, int limite, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var id = comandaId.ToString("D");
        var rows = await context.Database.SqlQuery<HistoricoRow>(
            $"""
            SELECT a.id AS "Id", a.acao AS "Acao", a.ocorrido_em AS "Quando", a.usuario_id AS "UsuarioId", c.nome AS "UsuarioNome", a.antes::text AS "Antes", a.depois::text AS "Depois"
            FROM auditoria_registros a
            LEFT JOIN contas c ON c.organizacao_id = a.organizacao_id AND c.id = a.usuario_id
            WHERE a.resultado = 'sucesso'
              AND ((a.objeto_tipo = 'comanda' AND a.objeto_id = {id})
                OR (a.objeto_tipo = 'rodada' AND a.objeto_id IN (SELECT r.id::text FROM rodadas r WHERE r.comanda_id = {comandaId}))
                OR (a.objeto_tipo = 'item-comanda' AND a.objeto_id IN (SELECT i.id::text FROM itens_comanda i WHERE i.comanda_id = {comandaId}))
                OR (a.objeto_tipo = 'pagamento' AND a.depois ->> 'comandaId' = {id}))
            ORDER BY a.sequencia
            LIMIT {limite}
            """).ToListAsync(cancellationToken);
        return rows.Select(r => new EventoDoHistorico(r.Id, r.Acao, r.Quando, r.UsuarioId, r.UsuarioNome, Campos(r.Antes), Campos(r.Depois))).ToList();
    }

    private static Dictionary<string, string> Campos(string? json)
    {
        var campos = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json) || JsonNode.Parse(json) is not JsonObject objeto)
        {
            return campos;
        }

        foreach (var (chave, valor) in objeto)
        {
            if (valor is JsonValue escalar && !Escondidos.Contains(chave) && !SensitiveData.IsSensitiveKey(chave))
            {
                campos[chave] = escalar.ToString();
            }
        }

        return campos;
    }
}

/// <summary>Whether an account may take comandas of a unit: an active account linked to it (the composite FK of the comanda already keeps the organization).</summary>
public sealed class PostgresGarconsDaUnidade : IGarconsDaUnidade
{
    public async Task<bool> PodeAtenderAsync(Guid organizationId, Guid unitId, Guid contaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var achados = await context.Database.SqlQuery<int>(
            $"""
            SELECT 1 AS "Value" FROM contas c
            JOIN usuario_unidades u ON u.organizacao_id = c.organizacao_id AND u.usuario_id = c.id
            WHERE c.id = {contaId} AND c.ativa AND u.unidade_id = {unitId}
            """).ToListAsync(cancellationToken);
        return achados.Count > 0;
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
        services.AddSingleton<GestaoDeAreas>();
        services.AddSingleton<ConsultaDoMapaDeMesas>();
        services.TryAddSingleton<OutboxWriter>();
        services.AddSingleton<IComandasStore, PostgresComandasStore>();
        services.AddSingleton<IEventosDeComandas, OutboxEventosDeComandas>();
        services.AddSingleton<GestaoDeComandas>();
        services.AddSingleton<IRodadasStore, PostgresRodadasStore>();
        services.AddSingleton<GestaoDeRodadas>();
        services.AddSingleton<GestaoDoRecebimento>();
        services.AddSingleton<GestaoDaTrocaDeMesa>();
        services.AddSingleton<IHistoricoDaComanda, PostgresHistoricoDaComanda>();
        services.AddSingleton<IGarconsDaUnidade, PostgresGarconsDaUnidade>();
        services.AddSingleton<GestaoDaEdicaoDaComanda>();
        services.AddSingleton<ConsultaDoHistoricoDaComanda>();
        services.AddSingleton<ConsultaDaUltimaVez>();
        return services;
    }
}
