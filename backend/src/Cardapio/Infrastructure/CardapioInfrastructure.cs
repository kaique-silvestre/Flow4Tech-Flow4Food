using Flow4Tech.Cardapio.Application;
using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flow4Tech.Cardapio.Infrastructure;

internal sealed class CategoriaRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public string Nome { get; set; } = "";

    public bool Ativa { get; set; }

    public int Versao { get; set; }

    public Guid? CategoriaPaiId { get; set; }

    public DateTimeOffset CriadaEm { get; set; }

    public DateTimeOffset AtualizadaEm { get; set; }
}

internal sealed class ProdutoRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid CategoriaId { get; set; }

    public string Codigo { get; set; } = "";

    public string Nome { get; set; } = "";

    public long PrecoCentavos { get; set; }

    public string Moeda { get; set; } = "BRL";

    public bool Ativo { get; set; }

    public bool Indisponivel { get; set; }

    public long? PrecoPromocionalCentavos { get; set; }

    public DateTimeOffset? PromocaoInicio { get; set; }

    public DateTimeOffset? PromocaoFim { get; set; }

    public int Versao { get; set; }

    public DateTimeOffset CriadoEm { get; set; }

    public DateTimeOffset AtualizadoEm { get; set; }
}

internal sealed class PrecoDaUnidadeRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public Guid ProdutoId { get; set; }

    public long PrecoCentavos { get; set; }

    public DateTimeOffset AtualizadoEm { get; set; }
}

internal sealed class ProdutoFrequenciaRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public Guid ProdutoId { get; set; }

    public long Lancamentos { get; set; }
}

/// <summary>EF mapping of the menu (schema owned by <c>0015_cardapio.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class CardapioDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<CategoriaRow> Categorias => Set<CategoriaRow>();

    public DbSet<ProdutoRow> Produtos => Set<ProdutoRow>();

    public DbSet<ProdutoFrequenciaRow> Frequencia => Set<ProdutoFrequenciaRow>();

    public DbSet<PrecoDaUnidadeRow> PrecosDaUnidade => Set<PrecoDaUnidadeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CategoriaRow>(e =>
        {
            e.ToTable("categorias");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Ativa).HasColumnName("ativa");
            e.Property(x => x.Versao).HasColumnName("versao");
            e.Property(x => x.CategoriaPaiId).HasColumnName("categoria_pai_id");
            e.Property(x => x.CriadaEm).HasColumnName("criada_em");
            e.Property(x => x.AtualizadaEm).HasColumnName("atualizada_em");
        });
        modelBuilder.Entity<ProdutoRow>(e =>
        {
            e.ToTable("produtos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.CategoriaId).HasColumnName("categoria_id");
            e.Property(x => x.Codigo).HasColumnName("codigo");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.PrecoCentavos).HasColumnName("preco_centavos");
            e.Property(x => x.Moeda).HasColumnName("moeda");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.Indisponivel).HasColumnName("indisponivel");
            e.Property(x => x.PrecoPromocionalCentavos).HasColumnName("preco_promocional_centavos");
            e.Property(x => x.PromocaoInicio).HasColumnName("promocao_inicio");
            e.Property(x => x.PromocaoFim).HasColumnName("promocao_fim");
            e.Property(x => x.Versao).HasColumnName("versao");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em");
        });
        modelBuilder.Entity<PrecoDaUnidadeRow>(e =>
        {
            e.ToTable("produtos_preco_unidade");
            e.HasKey(x => new { x.OrganizacaoId, x.UnidadeId, x.ProdutoId });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.ProdutoId).HasColumnName("produto_id");
            e.Property(x => x.PrecoCentavos).HasColumnName("preco_centavos");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em");
        });
        modelBuilder.Entity<ProdutoFrequenciaRow>(e =>
        {
            e.ToTable("produtos_frequencia");
            e.HasKey(x => new { x.OrganizacaoId, x.UnidadeId, x.ProdutoId });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.ProdutoId).HasColumnName("produto_id");
            e.Property(x => x.Lancamentos).HasColumnName("lancamentos");
        });
    }
}

/// <summary>
/// The menu on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>); never filters on <c>organizacao_id</c> (RLS does). Updates are conditional on the
/// stored version, so two editors cannot overwrite each other.
/// </summary>
public sealed class PostgresCardapioStore : ICardapioStore
{
    public async Task<IReadOnlyList<Categoria>> ListarCategoriasAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Categorias.AsNoTracking().OrderBy(c => c.Nome).Select(c => new Categoria(c.Id, c.Nome, c.Ativa, c.Versao, c.CategoriaPaiId)).ToListAsync(cancellationToken);
    }

    public async Task<Categoria?> AcharCategoriaAsync(Guid organizationId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Categorias.AsNoTracking().Where(c => c.Id == id).Select(c => new Categoria(c.Id, c.Nome, c.Ativa, c.Versao, c.CategoriaPaiId)).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task InserirCategoriaAsync(Guid organizationId, Categoria categoria, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categoria);
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        context.Categorias.Add(new CategoriaRow { Id = categoria.Id, OrganizacaoId = organizationId, Nome = categoria.Nome, Ativa = categoria.Ativa, Versao = categoria.Versao, CategoriaPaiId = categoria.CategoriaPaiId, CriadaEm = now, AtualizadaEm = now });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> AlterarCategoriaAsync(Guid organizationId, Categoria nova, int versaoEsperada, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nova);
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Categorias
            .Where(c => c.Id == nova.Id && c.Versao == versaoEsperada)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Nome, nova.Nome).SetProperty(c => c.Ativa, nova.Ativa).SetProperty(c => c.Versao, nova.Versao).SetProperty(c => c.CategoriaPaiId, nova.CategoriaPaiId).SetProperty(c => c.AtualizadaEm, now), cancellationToken);
        return rows == 1;
    }

    public async Task<bool> TemSubcategoriasAsync(Guid organizationId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Categorias.AsNoTracking().AnyAsync(c => c.CategoriaPaiId == id, cancellationToken);
    }

    public async Task<bool> NomeEmUsoAsync(Guid organizationId, string nome, Guid? exceto, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Categorias.AsNoTracking().AnyAsync(c => c.Nome == nome && c.Id != exceto, cancellationToken);
    }

    public async Task<IReadOnlyList<Produto>> ListarProdutosAsync(Guid organizationId, Guid unitId, string? busca, bool incluirInativos, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        var query = context.Produtos.AsNoTracking().Where(p => incluirInativos || p.Ativo);
        if (busca is not null)
        {
            // ILIKE with the wildcards of the typed text escaped: a search for "50%" finds "50%", not everything.
            var padrao = "%" + busca.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(p => EF.Functions.ILike(p.Nome, padrao, "\\") || EF.Functions.ILike(p.Codigo, padrao, "\\"));
        }

        var rows = await query.OrderBy(p => p.Nome).ThenBy(p => p.Codigo).Select(p => new { Row = p, Unidade = context.PrecosDaUnidade.Where(u => u.UnidadeId == unitId && u.ProdutoId == p.Id).Select(u => (long?)u.PrecoCentavos).FirstOrDefault() }).ToListAsync(cancellationToken);
        return [.. rows.Select(x => Mapa(x.Row, x.Unidade))];
    }

    public async Task<IReadOnlyList<Produto>> FrequentesAsync(Guid organizationId, Guid unitId, int limite, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Frequencia.AsNoTracking()
            .Where(f => f.UnidadeId == unitId && f.Lancamentos > 0)
            .Join(context.Produtos.AsNoTracking().Where(p => p.Ativo), f => f.ProdutoId, p => p.Id, (f, p) => new { f.Lancamentos, Produto = p })
            .OrderByDescending(x => x.Lancamentos).ThenBy(x => x.Produto.Nome)
            .Take(limite)
            .Select(x => new { Row = x.Produto, Unidade = context.PrecosDaUnidade.Where(u => u.UnidadeId == unitId && u.ProdutoId == x.Produto.Id).Select(u => (long?)u.PrecoCentavos).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(x => Mapa(x.Row, x.Unidade))];
    }

    public async Task<Produto?> AcharProdutoAsync(Guid organizationId, Guid unitId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        var found = await context.Produtos.AsNoTracking().Where(p => p.Id == id).Select(p => new { Row = p, Unidade = context.PrecosDaUnidade.Where(u => u.UnidadeId == unitId && u.ProdutoId == p.Id).Select(u => (long?)u.PrecoCentavos).FirstOrDefault() }).FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : Mapa(found.Row, found.Unidade);
    }

    public async Task<bool> CodigoEmUsoAsync(Guid organizationId, string codigo, Guid? exceto, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Produtos.AsNoTracking().AnyAsync(p => p.Codigo == codigo && p.Id != exceto, cancellationToken);
    }

    public async Task InserirProdutoAsync(Guid organizationId, Produto produto, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(produto);
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        context.Produtos.Add(new ProdutoRow
        {
            Id = produto.Id,
            OrganizacaoId = organizationId,
            CategoriaId = produto.CategoriaId,
            Codigo = produto.Codigo,
            Nome = produto.Nome,
            PrecoCentavos = produto.PrecoCentavos,
            Moeda = produto.Moeda,
            Ativo = produto.Ativo,
            Indisponivel = produto.Indisponivel,
            PrecoPromocionalCentavos = produto.Promocao?.PrecoCentavos,
            PromocaoInicio = produto.Promocao?.Inicio,
            PromocaoFim = produto.Promocao?.Fim,
            Versao = produto.Versao,
            CriadoEm = now,
            AtualizadoEm = now,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> AlterarProdutoAsync(Guid organizationId, Produto novo, int versaoEsperada, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(novo);
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Produtos
            .Where(p => p.Id == novo.Id && p.Versao == versaoEsperada)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.CategoriaId, novo.CategoriaId).SetProperty(p => p.Codigo, novo.Codigo).SetProperty(p => p.Nome, novo.Nome)
                    .SetProperty(p => p.PrecoCentavos, novo.PrecoCentavos).SetProperty(p => p.Ativo, novo.Ativo)
                    .SetProperty(p => p.Indisponivel, novo.Indisponivel).SetProperty(p => p.PrecoPromocionalCentavos, novo.Promocao == null ? null : novo.Promocao.PrecoCentavos)
                    .SetProperty(p => p.PromocaoInicio, novo.Promocao == null ? null : novo.Promocao.Inicio).SetProperty(p => p.PromocaoFim, novo.Promocao == null ? null : novo.Promocao.Fim).SetProperty(p => p.Versao, novo.Versao).SetProperty(p => p.AtualizadoEm, now),
                cancellationToken);
        return rows == 1;
    }

    public async Task DefinirPrecoDaUnidadeAsync(Guid organizationId, Guid unitId, Guid produtoId, long precoCentavos, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO produtos_preco_unidade (organizacao_id, unidade_id, produto_id, preco_centavos, atualizado_em)
            VALUES ({organizationId}, {unitId}, {produtoId}, {precoCentavos}, {now})
            ON CONFLICT (organizacao_id, unidade_id, produto_id) DO UPDATE SET preco_centavos = EXCLUDED.preco_centavos, atualizado_em = EXCLUDED.atualizado_em
            """,
            cancellationToken);
    }

    public async Task<bool> RemoverPrecoDaUnidadeAsync(Guid organizationId, Guid unitId, Guid produtoId, CancellationToken cancellationToken)
    {
        await using var context = new CardapioDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.PrecosDaUnidade.Where(x => x.UnidadeId == unitId && x.ProdutoId == produtoId).ExecuteDeleteAsync(cancellationToken) == 1;
    }

    private static Produto Mapa(ProdutoRow p, long? unidade) => new(
        p.Id,
        p.CategoriaId,
        p.Codigo,
        p.Nome,
        p.PrecoCentavos,
        p.Moeda,
        p.Ativo,
        p.Versao,
        p.Indisponivel,
        p.PrecoPromocionalCentavos is { } preco && p.PromocaoInicio is { } inicio && p.PromocaoFim is { } fim ? new Promocao(preco, inicio, fim) : null,
        unidade);
}

/// <summary>Menu events through the outbox, on the unit of work in progress (they commit or roll back with the change that raised them).</summary>
public sealed class OutboxEventosDoCardapio(OutboxWriter outbox) : IEventosDoCardapio
{
    public Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return outbox.AddAsync(TenantUnitOfWork.Require(organization.OrganizationId), organization, evento, cancellationToken);
    }
}

public static class CardapioServices
{
    /// <summary>Registers the menu module. Needs <c>AddTenantData</c>, <c>AddAuditoria</c> (audit log) and <c>IClock</c>.</summary>
    public static IServiceCollection AddCardapio(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OutboxWriter>();
        services.AddSingleton<ICardapioStore, PostgresCardapioStore>();
        services.AddSingleton<IEventosDoCardapio, OutboxEventosDoCardapio>();
        services.AddSingleton<GestaoDoCardapio>();
        return services;
    }
}
