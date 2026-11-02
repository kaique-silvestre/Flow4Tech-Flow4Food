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

internal sealed class SessaoRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid ContaId { get; set; }

    public Guid? UnidadeAtivaId { get; set; }

    public Guid FamiliaId { get; set; }

    public bool DeveTrocarSenha { get; set; }

    public int VersaoCredencial { get; set; }

    public DateTimeOffset CriadaEm { get; set; }

    public DateTimeOffset UltimaAtividadeEm { get; set; }

    public DateTimeOffset ExpiraInatividadeEm { get; set; }

    public DateTimeOffset ExpiraEm { get; set; }

    public DateTimeOffset? RevogadaEm { get; set; }

    public string? RevogadaMotivo { get; set; }

    public string? Ip { get; set; }

    public string? Agente { get; set; }
}

internal sealed class SessaoRefreshRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid SessaoId { get; set; }

    public byte[] Hash { get; set; } = [];

    public DateTimeOffset CriadoEm { get; set; }

    public DateTimeOffset? UsadoEm { get; set; }
}

/// <summary>EF mapping of accounts and credentials (schema owned by <c>0007_identidade.sql</c>). One instance per unit of work, from a tenant transaction. The global e-mail table is NOT mapped: only the SECURITY DEFINER functions touch it.</summary>
internal sealed class IdentidadeDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<ContaRow> Contas => Set<ContaRow>();

    public DbSet<CredencialRow> Credenciais => Set<CredencialRow>();

    public DbSet<SessaoRow> Sessoes => Set<SessaoRow>();

    public DbSet<SessaoRefreshRow> SessaoRefresh => Set<SessaoRefreshRow>();

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
        modelBuilder.Entity<SessaoRow>(e =>
        {
            e.ToTable("sessoes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.ContaId).HasColumnName("conta_id");
            e.Property(x => x.UnidadeAtivaId).HasColumnName("unidade_ativa_id");
            e.Property(x => x.FamiliaId).HasColumnName("familia_id");
            e.Property(x => x.DeveTrocarSenha).HasColumnName("deve_trocar_senha");
            e.Property(x => x.VersaoCredencial).HasColumnName("versao_credencial");
            e.Property(x => x.CriadaEm).HasColumnName("criada_em");
            e.Property(x => x.UltimaAtividadeEm).HasColumnName("ultima_atividade_em");
            e.Property(x => x.ExpiraInatividadeEm).HasColumnName("expira_inatividade_em");
            e.Property(x => x.ExpiraEm).HasColumnName("expira_em");
            e.Property(x => x.RevogadaEm).HasColumnName("revogada_em");
            e.Property(x => x.RevogadaMotivo).HasColumnName("revogada_motivo");
            e.Property(x => x.Ip).HasColumnName("ip");
            e.Property(x => x.Agente).HasColumnName("agente");
        });
        modelBuilder.Entity<SessaoRefreshRow>(e =>
        {
            e.ToTable("sessao_refresh");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.SessaoId).HasColumnName("sessao_id");
            e.Property(x => x.Hash).HasColumnName("hash");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
            e.Property(x => x.UsadoEm).HasColumnName("usado_em");
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
