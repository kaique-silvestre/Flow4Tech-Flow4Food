using Flow4Tech.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Infrastructure.Audit;

internal sealed class AuditRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid? UnidadeId { get; set; }

    public Guid? UsuarioId { get; set; }

    public Guid? SessaoId { get; set; }

    public string Acao { get; set; } = "";

    public string? ObjetoTipo { get; set; }

    public string? ObjetoId { get; set; }

    public string? Antes { get; set; }

    public string? Depois { get; set; }

    public string Resultado { get; set; } = "";

    public string CorrelationId { get; set; } = "";

    public DateTimeOffset OcorridoEm { get; set; }

    public long Sequencia { get; set; }

    public byte[]? HashAnterior { get; set; }

    public byte[] Hash { get; set; } = [];
}

internal sealed class VerificationStateRow
{
    public Guid OrganizacaoId { get; set; }

    public long? AncoraSequencia { get; set; }

    public byte[]? AncoraHash { get; set; }

    public long? ViolacaoSequencia { get; set; }

    public string? ViolacaoTipo { get; set; }

    public string? ViolacaoAssinatura { get; set; }

    public DateTimeOffset VerificadoEm { get; set; }
}

/// <summary>EF mapping of the audit table (schema owned by <c>0005_auditoria.sql</c> and <c>0006_auditoria_verificacao.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class AuditContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<AuditRow> Registros => Set<AuditRow>();

    public DbSet<VerificationStateRow> Verificacoes => Set<VerificationStateRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<VerificationStateRow>(e =>
        {
            e.ToTable("auditoria_verificacao");
            e.HasKey(x => x.OrganizacaoId);
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.AncoraSequencia).HasColumnName("ancora_sequencia");
            e.Property(x => x.AncoraHash).HasColumnName("ancora_hash");
            e.Property(x => x.ViolacaoSequencia).HasColumnName("violacao_sequencia");
            e.Property(x => x.ViolacaoTipo).HasColumnName("violacao_tipo");
            e.Property(x => x.ViolacaoAssinatura).HasColumnName("violacao_assinatura");
            e.Property(x => x.VerificadoEm).HasColumnName("verificado_em");
        });

        modelBuilder.Entity<AuditRow>(e =>
        {
            e.ToTable("auditoria_registros");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
            e.Property(x => x.SessaoId).HasColumnName("sessao_id");
            e.Property(x => x.Acao).HasColumnName("acao");
            e.Property(x => x.ObjetoTipo).HasColumnName("objeto_tipo");
            e.Property(x => x.ObjetoId).HasColumnName("objeto_id");
            e.Property(x => x.Antes).HasColumnName("antes").HasColumnType("jsonb");
            e.Property(x => x.Depois).HasColumnName("depois").HasColumnType("jsonb");
            e.Property(x => x.Resultado).HasColumnName("resultado");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OcorridoEm).HasColumnName("ocorrido_em");
            e.Property(x => x.Sequencia).HasColumnName("sequencia");
            e.Property(x => x.HashAnterior).HasColumnName("hash_anterior");
            e.Property(x => x.Hash).HasColumnName("hash");
        });
    }
}
