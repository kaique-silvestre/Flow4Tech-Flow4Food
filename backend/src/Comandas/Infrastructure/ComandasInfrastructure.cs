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

/// <summary>EF mapping of the tables and comandas (schemas owned by <c>0017_mesas.sql</c> and <c>0018_comandas.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class MesasDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<MesaRow> Mesas => Set<MesaRow>();

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

    public async Task<Comanda?> AcharAsync(Guid organizationId, Guid unitId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var c = await Achar(context, unitId, comandaId).FirstOrDefaultAsync(cancellationToken);
        return c is null ? null : new Comanda(c.Id, c.UnidadeId, c.DiaOperacional, c.Numero, c.Identificacao, c.MesaId, c.Canal, c.GarcomId, c.Pessoas, RegrasDeComanda.Estado(c.Estado), c.AbertaEm, c.FechadaEm, c.TaxaServicoPercentual, c.TaxaServicoRemovida, c.Versao);
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
        return rows.Select(c => new Comanda(c.Id, c.UnidadeId, c.DiaOperacional, c.Numero, c.Identificacao, c.MesaId, c.Canal, c.GarcomId, c.Pessoas, RegrasDeComanda.Estado(c.Estado), c.AbertaEm, c.FechadaEm, c.TaxaServicoPercentual, c.TaxaServicoRemovida, c.Versao)).ToList();
    }

    public async Task<Comanda?> UltimaFechadaDaMesaAsync(Guid organizationId, Guid unitId, Guid mesaId, Guid? garcomId, CancellationToken cancellationToken)
    {
        await using var context = new MesasDbContext(TenantUnitOfWork.Require(organizationId));
        var c = await UltimaFechadaDaMesa(context, unitId, mesaId, garcomId).FirstOrDefaultAsync(cancellationToken);
        return c is null ? null : new Comanda(c.Id, c.UnidadeId, c.DiaOperacional, c.Numero, c.Identificacao, c.MesaId, c.Canal, c.GarcomId, c.Pessoas, RegrasDeComanda.Estado(c.Estado), c.AbertaEm, c.FechadaEm, c.TaxaServicoPercentual, c.TaxaServicoRemovida, c.Versao);
    }

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
        services.AddSingleton<IRodadasStore, PostgresRodadasStore>();
        services.AddSingleton<GestaoDeRodadas>();
        services.AddSingleton<GestaoDoRecebimento>();
        services.AddSingleton<ConsultaDaUltimaVez>();
        return services;
    }
}
