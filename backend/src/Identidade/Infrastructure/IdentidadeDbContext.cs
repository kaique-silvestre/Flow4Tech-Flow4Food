using Flow4Tech.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Identidade.Infrastructure;

internal sealed class ContaRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public string Tipo { get; set; } = "";

    public string Nome { get; set; } = "";

    public string? Email { get; set; }

    public string? Usuario { get; set; }

    public bool Dono { get; set; }

    public bool Ativa { get; set; }

    public int VersaoCredencial { get; set; }

    public DateTimeOffset CriadaEm { get; set; }

    public DateTimeOffset? DesativadaEm { get; set; }
}

internal sealed class CredencialRow
{
    public Guid OrganizacaoId { get; set; }

    public Guid ContaId { get; set; }

    public string SenhaHash { get; set; } = "";

    public bool DeveTrocarSenha { get; set; }

    public DateTimeOffset AtualizadaEm { get; set; }

    public DateTimeOffset? TemporariaExpiraEm { get; set; }
}

/// <summary>EF mapping of accounts and credentials (schema owned by <c>0007_identidade.sql</c>). One instance per unit of work, from a tenant transaction. The global e-mail table is NOT mapped: only the SECURITY DEFINER functions touch it.</summary>
internal sealed class IdentidadeDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<ContaRow> Contas => Set<ContaRow>();

    public DbSet<CredencialRow> Credenciais => Set<CredencialRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ContaRow>(e =>
        {
            e.ToTable("contas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.Usuario).HasColumnName("usuario");
            e.Property(x => x.Dono).HasColumnName("dono");
            e.Property(x => x.Ativa).HasColumnName("ativa");
            e.Property(x => x.VersaoCredencial).HasColumnName("versao_credencial");
            e.Property(x => x.CriadaEm).HasColumnName("criada_em");
            e.Property(x => x.DesativadaEm).HasColumnName("desativada_em");
        });
        modelBuilder.Entity<CredencialRow>(e =>
        {
            e.ToTable("credenciais");
            e.HasKey(x => new { x.OrganizacaoId, x.ContaId });
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.ContaId).HasColumnName("conta_id");
            e.Property(x => x.SenhaHash).HasColumnName("senha_hash");
            e.Property(x => x.DeveTrocarSenha).HasColumnName("deve_trocar_senha");
            e.Property(x => x.AtualizadaEm).HasColumnName("atualizada_em");
            e.Property(x => x.TemporariaExpiraEm).HasColumnName("temporaria_expira_em");
        });
    }
}
