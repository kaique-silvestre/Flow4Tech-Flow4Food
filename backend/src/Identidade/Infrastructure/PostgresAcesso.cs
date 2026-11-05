using System.Data.Common;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>
/// Invitations on PostgreSQL (migration 0013). Joins the caller's unit of work: the organization is the one the unit was opened with and RLS filters; nothing here filters on <c>organizacao_id</c>. The token is only ever
/// compared by its SHA-256. Writes are explicit interpolated SQL (the statement is visible); reads are EF with <c>AsNoTracking</c>.
/// </summary>
public sealed class PostgresConvitesStore : IConvitesStore
{
    private const string ViolacaoDeChaveEstrangeira = "23503";

    public async Task<bool> EmailJaEhMembroAsync(Guid organizacaoId, string email, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        return await context.Contas.AsNoTracking().AnyAsync(c => c.Email == email, cancellationToken);
    }

    public async Task RevogarPendentesDoEmailAsync(Guid organizacaoId, string email, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync($"UPDATE convites SET revogado_em = {agora} WHERE email = {email} AND usado_em IS NULL AND revogado_em IS NULL", cancellationToken);
    }

    public async Task InserirAsync(Guid organizacaoId, NovoConvite convite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(convite);
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var hash = convite.TokenHash.ToArray();
        try
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO convites (id, organizacao_id, email, nome, perfil_id, unidade_id, token_hash, criado_por, criado_em, expira_em)
                VALUES ({convite.Id}, {organizacaoId}, {convite.Email}, {convite.Nome}, {convite.PerfilId}, {convite.UnidadeId}, {hash}, {convite.CriadoPor}, {convite.CriadoEm}, {convite.ExpiraEm})
                """,
                cancellationToken);
        }
        catch (DbException ex) when (ex.SqlState == ViolacaoDeChaveEstrangeira)
        {
            // The composite foreign keys only accept a profile or a unit of THIS organization (FK checks bypass RLS, so a foreign id cannot even be probed through them).
            throw new ReferenciaInvalidaException("Profile or unit not found in this organization.", ex);
        }
    }

    public async Task<ConviteEncontrado?> ObterPorHashAsync(Guid organizacaoId, byte[] hash, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var row = await context.Convites.AsNoTracking().Where(c => c.TokenHash == hash).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new ConviteEncontrado(row.Id, row.Email, row.Nome, row.PerfilId, row.UnidadeId, row.CriadoPor, row.ExpiraEm, row.UsadoEm, row.RevogadoEm);
    }

    public async Task<bool> MarcarUsadoAsync(Guid organizacaoId, Guid conviteId, Guid contaId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE convites SET usado_em = {agora}, conta_id = {contaId} WHERE id = {conviteId} AND usado_em IS NULL AND revogado_em IS NULL AND expira_em > {agora}",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> RevogarAsync(Guid organizacaoId, Guid conviteId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE convites SET revogado_em = {agora} WHERE id = {conviteId} AND usado_em IS NULL AND revogado_em IS NULL", cancellationToken);
        return rows == 1;
    }
}

/// <summary>Recovery tokens on PostgreSQL (migration 0013). Same rules as the invitations: tenant unit of work, hash only, atomic single use.</summary>
public sealed class PostgresRecuperacoesStore : IRecuperacoesStore
{
    public async Task InvalidarPendentesAsync(Guid organizacaoId, Guid contaId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync($"UPDATE recuperacoes SET usado_em = {agora} WHERE conta_id = {contaId} AND usado_em IS NULL", cancellationToken);
    }

    public async Task InserirAsync(Guid organizacaoId, Guid id, Guid contaId, byte[] hash, DateTimeOffset agora, DateTimeOffset expiraEm, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO recuperacoes (id, organizacao_id, conta_id, token_hash, criado_em, expira_em) VALUES ({id}, {organizacaoId}, {contaId}, {hash}, {agora}, {expiraEm})",
            cancellationToken);
    }

    public async Task<RecuperacaoEncontrada?> ObterPorHashAsync(Guid organizacaoId, byte[] hash, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var row = await context.Recuperacoes.AsNoTracking().Where(r => r.TokenHash == hash).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new RecuperacaoEncontrada(row.Id, row.ContaId, row.ExpiraEm, row.UsadoEm);
    }

    public async Task<bool> ConsumirAsync(Guid organizacaoId, Guid id, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        // The WHERE is the lock and the single-use rule at once: of two simultaneous redemptions the second waits for the first, re-reads, and updates no row.
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE recuperacoes SET usado_em = {agora} WHERE id = {id} AND usado_em IS NULL AND expira_em > {agora}", cancellationToken);
        return rows == 1;
    }
}

/// <summary>External identities (Google) on PostgreSQL (migration 0013). The database refuses a link for an account that is not an account with e-mail (composite FK on the type).</summary>
public sealed class PostgresIdentidadesExternasStore : IIdentidadesExternasStore
{
    public async Task<string?> ObterSubAsync(Guid organizacaoId, Guid contaId, string provedor, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        return await context.IdentidadesExternas.AsNoTracking().Where(i => i.ContaId == contaId && i.Provedor == provedor).Select(i => i.Sub).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> InserirAsync(Guid organizacaoId, Guid contaId, string provedor, string subject, string email, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        // No conflict target: the account already linked (primary key) and the identity linked elsewhere (unique on provider + sub, across organizations) both mean "not inserted", not an aborted transaction.
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO identidades_externas (organizacao_id, conta_id, provedor, sub, email, tipo_conta, criada_em)
            VALUES ({organizacaoId}, {contaId}, {provedor}, {subject}, {email}, 'email', {agora})
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> RemoverAsync(Guid organizacaoId, Guid contaId, string provedor, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var rows = await context.Database.ExecuteSqlAsync($"DELETE FROM identidades_externas WHERE conta_id = {contaId} AND provedor = {provedor}", cancellationToken);
        return rows == 1;
    }
}
