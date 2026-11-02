using Flow4Tech.Infrastructure.Data;
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

/// <summary>EF mapping of units and user-unit links (schema owned by <c>0001</c> and <c>0004_usuario_unidades.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class OrganizacaoDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<UnidadeRow> Unidades => Set<UnidadeRow>();

    public DbSet<UsuarioUnidadeRow> Vinculos => Set<UsuarioUnidadeRow>();

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

public static class OrganizacaoServices
{
    /// <summary>Registers the Organizacao module: the link adapter and the application service (needs <c>IUnitOfWork</c>, see <c>AddTenantData</c>).</summary>
    public static IServiceCollection AddOrganizacao(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IUnidadeVinculos, PostgresUnidadeVinculos>();
        services.AddScoped<UnidadesDoUsuario>();
        return services;
    }
}
