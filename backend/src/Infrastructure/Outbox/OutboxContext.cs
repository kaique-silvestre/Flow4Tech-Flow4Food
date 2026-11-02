using Flow4Tech.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Infrastructure.Outbox;

internal sealed class OutboxEventRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public string Tipo { get; set; } = "";

    public string Payload { get; set; } = "";

    public DateTimeOffset OcorridoEm { get; set; }

    public DateTimeOffset? ProcessadoEm { get; set; }

    public int Tentativas { get; set; }

    public DateTimeOffset? FalhouEm { get; set; }

    public string? UltimoErro { get; set; }
}

internal sealed class OutboxConsumoRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid EventoId { get; set; }

    public string Consumidor { get; set; } = "";

    public DateTimeOffset ConsumidoEm { get; set; }
}

/// <summary>EF mapping of the two outbox tables (schema owned by <c>0002_outbox.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class OutboxContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<OutboxEventRow> Eventos => Set<OutboxEventRow>();

    public DbSet<OutboxConsumoRow> Consumos => Set<OutboxConsumoRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<OutboxEventRow>(e =>
        {
            e.ToTable("outbox_eventos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.OcorridoEm).HasColumnName("ocorrido_em");
            e.Property(x => x.ProcessadoEm).HasColumnName("processado_em");
            e.Property(x => x.Tentativas).HasColumnName("tentativas");
            e.Property(x => x.FalhouEm).HasColumnName("falhou_em");
            e.Property(x => x.UltimoErro).HasColumnName("ultimo_erro");
        });
        modelBuilder.Entity<OutboxConsumoRow>(e =>
        {
            e.ToTable("outbox_consumos");
            e.HasKey(x => new { x.OrganizacaoId, x.EventoId, x.Consumidor });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.EventoId).HasColumnName("evento_id");
            e.Property(x => x.Consumidor).HasColumnName("consumidor");
            e.Property(x => x.ConsumidoEm).HasColumnName("consumido_em");
        });
    }
}
