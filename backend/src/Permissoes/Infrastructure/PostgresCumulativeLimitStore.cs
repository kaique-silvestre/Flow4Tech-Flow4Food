using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Permissoes.Application;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Permissoes.Infrastructure;

internal sealed class LimiteConsumoRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid UsuarioId { get; set; }

    public string Permissao { get; set; } = "";

    public DateOnly Dia { get; set; }

    public long ConsumidoCentavos { get; set; }
}

/// <summary>EF mapping of the cumulative-limit counter (schema owned by <c>0003_limites_consumo.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class PermissoesDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<LimiteConsumoRow> Limites => Set<LimiteConsumoRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<LimiteConsumoRow>(e =>
        {
            e.ToTable("limites_consumo");
            e.HasKey(x => new { x.OrganizacaoId, x.UsuarioId, x.Permissao, x.Dia });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
            e.Property(x => x.Permissao).HasColumnName("permissao");
            e.Property(x => x.Dia).HasColumnName("dia");
            e.Property(x => x.ConsumidoCentavos).HasColumnName("consumido_centavos");
        });
    }
}

/// <summary>
/// Cumulative limits on PostgreSQL. Check and update are ONE statement inside one tenant transaction: an upsert whose conflict branch
/// only applies when the new total stays within the cap. INSERT ... ON CONFLICT DO UPDATE takes the row lock of the counter, so concurrent
/// consumers of the same (organization, user, permission, day) queue on that row and each re-evaluates the cap against the committed total
/// (READ COMMITTED is enough). Zero rows affected means the cap would be exceeded. No application-side read, no advisory lock, no retry loop.
/// </summary>
public sealed class PostgresCumulativeLimitStore(TenantDatabase tenant) : ICumulativeLimitStore
{
    public async Task<bool> TryConsumeAsync(Guid organizationId, Guid? unitId, Guid userId, string permission, DateOnly day, long cents, long capCents, CancellationToken cancellationToken)
    {
        // The first insert has no WHERE to guard it, so an amount above the cap alone is refused here.
        if (cents < 0 || cents > capCents)
        {
            return false;
        }

        await using var tx = await tenant.BeginAsync(new Flow4Tech.Kernel.OrganizationContext(organizationId, unitId ?? Guid.Empty), cancellationToken);
        await using var context = new PermissoesDbContext(tx);
        var affected = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO limites_consumo AS l (organizacao_id, usuario_id, permissao, dia, consumido_centavos)
            VALUES ({organizationId}, {userId}, {permission}, {day}, {cents})
            ON CONFLICT (organizacao_id, usuario_id, permissao, dia)
            DO UPDATE SET consumido_centavos = l.consumido_centavos + EXCLUDED.consumido_centavos
            WHERE l.consumido_centavos + EXCLUDED.consumido_centavos <= {capCents}
            """,
            cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return affected == 1;
    }
}
