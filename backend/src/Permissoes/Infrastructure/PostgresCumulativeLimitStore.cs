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

internal sealed class PerfilRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public string Nome { get; set; } = "";

    public string? Modelo { get; set; }

    public bool AcompanhaModelo { get; set; }

    public DateTimeOffset CriadoEm { get; set; }
}

/// <summary>The columns every rule table shares (profile rules and individual overrides).</summary>
internal abstract class RegraRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public string Permissao { get; set; } = "";

    public string Efeito { get; set; } = "allow";

    public Guid? UnidadeId { get; set; }

    public decimal? MaxPercentual { get; set; }

    public long? MaxValorCentavos { get; set; }

    public long? TetoDiarioCentavos { get; set; }
}

internal sealed class RegraDoPerfilRow : RegraRow
{
    public Guid IdDoPerfil { get; set; }
}

internal sealed class RegraDoUsuarioRow : RegraRow
{
    public Guid UsuarioId { get; set; }
}

internal sealed class AtribuicaoRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid UsuarioId { get; set; }

    public Guid IdDoPerfil { get; set; }
}

internal sealed class ContaRow
{
    public Guid Id { get; set; }

    public bool Ativa { get; set; }
}

/// <summary>EF mapping of the cumulative-limit counter and of the profiles, rules, assignments and overrides (schema owned by <c>0003_limites_consumo.sql</c> and <c>0010_perfis_regras.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class PermissoesDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<LimiteConsumoRow> Limites => Set<LimiteConsumoRow>();

    public DbSet<PerfilRow> Perfis => Set<PerfilRow>();

    public DbSet<RegraDoPerfilRow> RegrasDosPerfis => Set<RegraDoPerfilRow>();

    public DbSet<AtribuicaoRow> Atribuicoes => Set<AtribuicaoRow>();

    public DbSet<RegraDoUsuarioRow> RegrasDosUsuarios => Set<RegraDoUsuarioRow>();

    public DbSet<ContaRow> Contas => Set<ContaRow>();

    private static void MapRule<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
        where T : RegraRow
    {
        e.Property(x => x.Permissao).HasColumnName("permissao");
        e.Property(x => x.Efeito).HasColumnName("efeito");
        e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
        e.Property(x => x.MaxPercentual).HasColumnName("max_percentual").HasColumnType("numeric(5,2)");
        e.Property(x => x.MaxValorCentavos).HasColumnName("max_valor_centavos");
        e.Property(x => x.TetoDiarioCentavos).HasColumnName("teto_diario_centavos");
    }

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
        modelBuilder.Entity<PerfilRow>(e =>
        {
            e.ToTable("perfis");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Modelo).HasColumnName("modelo");
            e.Property(x => x.AcompanhaModelo).HasColumnName("acompanha_modelo");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
        });
        modelBuilder.Entity<RegraDoPerfilRow>(e =>
        {
            e.ToTable("perfil_regras");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.IdDoPerfil).HasColumnName("perfil_id");
            MapRule(e);
        });
        modelBuilder.Entity<AtribuicaoRow>(e =>
        {
            e.ToTable("usuario_perfis");
            e.HasKey(x => new { x.OrganizacaoId, x.UsuarioId, x.IdDoPerfil });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
            e.Property(x => x.IdDoPerfil).HasColumnName("perfil_id");
        });
        modelBuilder.Entity<RegraDoUsuarioRow>(e =>
        {
            e.ToTable("usuario_regras");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
            MapRule(e);
        });
        // Read-only view of the account (owned by the Identidade module) to learn whether it exists and is active; never written from here.
        modelBuilder.Entity<ContaRow>(e =>
        {
            e.ToTable("contas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Ativa).HasColumnName("ativa");
        });
    }
}

/// <summary>
/// Cumulative limits on PostgreSQL. Check and update are ONE statement, run on the tenant transaction of the caller's unit of work (<see cref="TenantUnitOfWork"/>), so a failed business operation rolls the reservation back: an upsert whose conflict branch
/// only applies when the new total stays within the cap. INSERT ... ON CONFLICT DO UPDATE takes the row lock of the counter, so concurrent
/// consumers of the same (organization, user, permission, day) queue on that row and each re-evaluates the cap against the committed total
/// (READ COMMITTED is enough). Zero rows affected means the cap would be exceeded. No application-side read, no advisory lock, no retry loop.
/// </summary>
public sealed class PostgresCumulativeLimitStore() : ICumulativeLimitStore
{
    public async Task<bool> TryConsumeAsync(Guid organizationId, Guid? unitId, Guid userId, string permission, DateOnly day, long cents, long capCents, CancellationToken cancellationToken)
    {
        // The first insert has no WHERE to guard it, so an amount above the cap alone is refused here.
        if (cents < 0 || cents > capCents)
        {
            return false;
        }

        // Joins the business transaction (ADR-006 decision C): no transaction of its own, no commit here. Outside a unit of work this throws.
        var tx = TenantUnitOfWork.Require(organizationId);
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
        return affected == 1;
    }
}
