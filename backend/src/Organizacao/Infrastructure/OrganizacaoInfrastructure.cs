using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Organizacao.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Organizacao.Infrastructure;

internal sealed class UnidadeRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public string Nome { get; set; } = "";
}

internal sealed class UsuarioUnidadeRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid UsuarioId { get; set; }

    public Guid UnidadeId { get; set; }
}

internal sealed class ConfiguracaoVersaoRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid? UnidadeId { get; set; }

    public string Chave { get; set; } = "";

    public int Versao { get; set; }

    public string? Valor { get; set; }

    public bool Travado { get; set; }

    public DateTimeOffset VigenteAPartirDe { get; set; }

    public DateTimeOffset CriadoEm { get; set; }

    public Guid? CriadoPor { get; set; }

    public string? Motivo { get; set; }

    public int? RestauraVersao { get; set; }
}

/// <summary>EF mapping of units, user-unit links and setting versions (schema owned by <c>0001</c>, <c>0004_usuario_unidades.sql</c> and <c>0014_configuracoes.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class OrganizacaoDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<UnidadeRow> Unidades => Set<UnidadeRow>();

    public DbSet<UsuarioUnidadeRow> Vinculos => Set<UsuarioUnidadeRow>();

    public DbSet<ConfiguracaoVersaoRow> ConfiguracaoVersoes => Set<ConfiguracaoVersaoRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<UnidadeRow>(e =>
        {
            e.ToTable("unidades");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.Nome).HasColumnName("nome");
        });
        modelBuilder.Entity<ConfiguracaoVersaoRow>(e =>
        {
            e.ToTable("configuracoes_versoes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.Chave).HasColumnName("chave");
            e.Property(x => x.Versao).HasColumnName("versao");
            e.Property(x => x.Valor).HasColumnName("valor");
            e.Property(x => x.Travado).HasColumnName("travado");
            e.Property(x => x.VigenteAPartirDe).HasColumnName("vigente_a_partir_de");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
            e.Property(x => x.CriadoPor).HasColumnName("criado_por");
            e.Property(x => x.Motivo).HasColumnName("motivo");
            e.Property(x => x.RestauraVersao).HasColumnName("restaura_versao");
        });
        modelBuilder.Entity<UsuarioUnidadeRow>(e =>
        {
            e.ToTable("usuario_unidades");
            e.HasKey(x => new { x.OrganizacaoId, x.UsuarioId, x.UnidadeId });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
        });
    }
}

/// <summary>
/// Links on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>), so the organization context is the one
/// the host resolved from the session. Never filters on <c>organizacao_id</c>: RLS does.
/// </summary>
public sealed class PostgresUnidadeVinculos : IUnidadeVinculos
{
    public async Task<bool> TemVinculoAsync(Guid organizationId, Guid userId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = new OrganizacaoDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Vinculos.AsNoTracking().AnyAsync(v => v.UsuarioId == userId && v.UnidadeId == unitId, cancellationToken);
    }

    public async Task<IReadOnlyList<Unidade>> ListarDoUsuarioAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken)
    {
        await using var context = new OrganizacaoDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Vinculos.AsNoTracking()
            .Where(v => v.UsuarioId == userId)
            .Join(context.Unidades.AsNoTracking(), v => v.UnidadeId, u => u.Id, (v, u) => new { u.Id, u.Nome })
            .OrderBy(u => u.Nome)
            .Select(u => new Unidade(u.Id, u.Nome))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>
/// Setting versions on PostgreSQL (spec 5.7). Joins the caller's unit of work; never filters on <c>organizacao_id</c> (RLS does). The version in force is chosen by SQL
/// (<c>DISTINCT ON</c> the scope and key, highest version among those already started), the same rule <c>ResolvedorDeConfiguracao</c> applies in memory. Append only: the
/// application credential has no UPDATE or DELETE on the table.
/// </summary>
public sealed class PostgresConfiguracoesStore(TenantDatabase tenant) : IConfiguracoesStore
{
    /// <summary>Inside a unit of work it joins it (one connection, one snapshot); outside one (a module reading an option) it opens a short read transaction of the organization, like the policy source.</summary>
    public async Task<IReadOnlyList<Organizacao.Domain.VersaoDeConfiguracao>> EmVigorAsync(Guid organizationId, IReadOnlyCollection<string> chaves, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chaves);
        if (TenantUnitOfWork.InProgress(organizationId))
        {
            return await LerAsync(TenantUnitOfWork.Require(organizationId), chaves, unitId, at, cancellationToken);
        }

        await using var transaction = await tenant.BeginAsync(new OrganizationContext(organizationId, Guid.Empty), cancellationToken);
        return await LerAsync(transaction, chaves, unitId, at, cancellationToken);
    }

    private static async Task<IReadOnlyList<Organizacao.Domain.VersaoDeConfiguracao>> LerAsync(TenantTransaction transaction, IReadOnlyCollection<string> chaves, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var context = new OrganizacaoDbContext(transaction);
        var keys = chaves.ToArray();
        var instant = at.ToUniversalTime();
        var rows = await context.ConfiguracaoVersoes
            .FromSql($"""
                SELECT DISTINCT ON (unidade_id, chave) id, organizacao_id, unidade_id, chave, versao, valor, travado, vigente_a_partir_de, criado_em, criado_por, motivo, restaura_versao
                FROM configuracoes_versoes
                WHERE chave = ANY({keys}) AND (unidade_id IS NULL OR unidade_id = {unitId}) AND vigente_a_partir_de <= {instant}
                ORDER BY unidade_id, chave, versao DESC
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToVersion)];
    }

    public async Task<IReadOnlyList<Organizacao.Domain.VersaoDeConfiguracao>> HistoricoAsync(Guid organizationId, string chave, Guid? unitId, CancellationToken cancellationToken)
    {
        await using var context = new OrganizacaoDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.ConfiguracaoVersoes.AsNoTracking()
            .Where(v => v.Chave == chave && v.UnidadeId == unitId)
            .OrderByDescending(v => v.Versao)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToVersion)];
    }

    public async Task<int> ProximaVersaoAsync(Guid organizationId, string chave, Guid? unitId, CancellationToken cancellationToken)
    {
        await using var context = new OrganizacaoDbContext(TenantUnitOfWork.Require(organizationId));
        var lockKey = $"cfg|{organizationId:N}|{unitId:N}|{chave}";
        _ = await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        var atual = await context.ConfiguracaoVersoes.AsNoTracking().Where(v => v.Chave == chave && v.UnidadeId == unitId).MaxAsync(v => (int?)v.Versao, cancellationToken);
        return (atual ?? 0) + 1;
    }

    public async Task InserirAsync(Guid organizationId, Organizacao.Domain.VersaoDeConfiguracao versao, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versao);
        await using var context = new OrganizacaoDbContext(TenantUnitOfWork.Require(organizationId));
        context.ConfiguracaoVersoes.Add(new ConfiguracaoVersaoRow
        {
            Id = versao.Id,
            OrganizacaoId = organizationId,
            UnidadeId = versao.UnidadeId,
            Chave = versao.Chave,
            Versao = versao.Versao,
            Valor = versao.Valor,
            Travado = versao.Travado,
            VigenteAPartirDe = versao.VigenteAPartirDe.ToUniversalTime(),
            CriadoEm = versao.CriadoEm.ToUniversalTime(),
            CriadoPor = versao.CriadoPor,
            Motivo = versao.Motivo,
            RestauraVersao = versao.RestauraVersao,
        });
        _ = await context.SaveChangesAsync(cancellationToken);
    }

    private static Organizacao.Domain.VersaoDeConfiguracao ToVersion(ConfiguracaoVersaoRow r) =>
        new(r.Id, r.Chave, r.UnidadeId, r.Versao, r.Valor, r.Travado, r.VigenteAPartirDe, r.CriadoEm, r.CriadoPor, r.Motivo, r.RestauraVersao);
}

public static class OrganizacaoServices
{
    /// <summary>
    /// Registers the Organizacao module: the link adapter, the application services and the layered settings (needs <c>IUnitOfWork</c>, see <c>AddTenantData</c>, and an <c>IClock</c>).
    /// <paramref name="catalogo"/> is the collected option catalog; <paramref name="valorConfigurado"/> builds, from the provider, the function that reads the SYSTEM layer from
    /// configuration (read late, after every configuration source is loaded). The host also registers <see cref="IPermissaoDoChamador"/>.
    /// </summary>
    public static IServiceCollection AddOrganizacao(this IServiceCollection services, IReadOnlyList<SettingDefinition>? catalogo = null, Func<IServiceProvider, Func<SettingDefinition, string?>>? valorConfigurado = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IUnidadeVinculos, PostgresUnidadeVinculos>();
        services.AddScoped<UnidadesDoUsuario>();
        services.AddSingleton<IConfiguracoesStore, PostgresConfiguracoesStore>();
        services.AddSingleton(new CatalogoDeConfiguracoes(catalogo ?? []));
        services.AddSingleton<ISettings>(provider => new ResolvedorDeConfiguracoes(
            provider.GetRequiredService<IConfiguracoesStore>(),
            valorConfigurado is null ? _ => null : valorConfigurado(provider)));
        services.AddSingleton<Configuracoes>(provider => new Configuracoes(
            provider.GetRequiredService<IUnitOfWork>(),
            provider.GetRequiredService<IConfiguracoesStore>(),
            provider.GetRequiredService<CatalogoDeConfiguracoes>(),
            provider.GetRequiredService<ISettings>(),
            provider.GetRequiredService<IPermissaoDoChamador>(),
            provider.GetRequiredService<Flow4Tech.Kernel.Audit.IAuditLog>(),
            provider.GetRequiredService<IClock>()));
        return services;
    }
}
