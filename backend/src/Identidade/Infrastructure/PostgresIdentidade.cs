using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Identidade.Infrastructure;

internal sealed record UnidadeDaConta(Guid ContaId, Guid UnidadeId);

internal static class TipoContaText
{
    public static string Text(TipoConta tipo) => tipo == TipoConta.Email ? "email" : "operacional";

    public static TipoConta Parse(string text) => text == "email" ? TipoConta.Email : TipoConta.Operacional;
}

/// <summary>
/// Accounts on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>): the organization context is the one the unit was opened with and RLS does
/// the filtering; nothing here filters on <c>organizacao_id</c>. Writes are explicit interpolated SQL so the statement is visible; reads are EF with <c>AsNoTracking</c>.
/// </summary>
public sealed class PostgresContaStore(IClock clock) : IContaStore
{
    public async Task<bool> InserirAsync(Guid organizacaoId, NovaConta conta, string senhaHash, bool deveTrocarSenha, DateTimeOffset? temporariaExpiraEm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conta);
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var tipo = TipoContaText.Text(conta.Tipo);

        // ON CONFLICT DO NOTHING without a target: a taken user name or e-mail in this organization is "not inserted", not an aborted transaction.
        var inserted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO contas (id, organizacao_id, tipo, nome, email, usuario, dono, criada_em)
            VALUES ({conta.Id}, {organizacaoId}, {tipo}, {conta.Nome}, {conta.Email}, {conta.Usuario}, {conta.Dono}, {conta.CriadaEm})
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            return false;
        }

        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO credenciais (organizacao_id, conta_id, senha_hash, deve_trocar_senha, temporaria_expira_em, atualizada_em)
            VALUES ({organizacaoId}, {conta.Id}, {senhaHash}, {deveTrocarSenha}, {temporariaExpiraEm}, {conta.CriadaEm})
            """,
            cancellationToken);
        return true;
    }

    public async Task<bool> RegistrarEmailAsync(Guid organizacaoId, Guid contaId, string emailNormalizado, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var result = await context.Database.SqlQuery<bool>($"SELECT identidade_registrar_email({contaId}, {emailNormalizado}) AS \"Value\"").ToListAsync(cancellationToken);
        return result.Single();
    }

    public async Task<IReadOnlyList<ContaListada>> ListarAsync(Guid organizacaoId, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var rows = await context.Contas.AsNoTracking().OrderBy(c => c.Nome).ThenBy(c => c.Id).Select(c => new { c.Id, c.Nome, c.Usuario, c.Dono, c.Ativa }).ToListAsync(cancellationToken);
        var links = await context.Database.SqlQuery<UnidadeDaConta>($"SELECT usuario_id AS \"ContaId\", unidade_id AS \"UnidadeId\" FROM usuario_unidades").ToListAsync(cancellationToken);
        var byAccount = links.ToLookup(l => l.ContaId, l => l.UnidadeId);
        return [.. rows.Select(r => new ContaListada(r.Id, r.Nome, r.Usuario, r.Dono, r.Ativa, [.. byAccount[r.Id].Order()]))];
    }

    public async Task<Conta?> ObterAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var row = await context.Contas.AsNoTracking().Where(c => c.Id == contaId).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new Conta(row.Id, row.OrganizacaoId, TipoContaText.Parse(row.Tipo), row.Nome, row.Email, row.Usuario, row.Dono, row.Ativa, row.VersaoCredencial);
    }

    public async Task<ContaParaLogin?> ObterComCredencialAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var row = await context.Contas.AsNoTracking()
            .Where(c => c.Id == contaId)
            .Join(context.Credenciais.AsNoTracking(), c => c.Id, k => k.ContaId, (c, k) => new { c.Id, c.OrganizacaoId, c.Tipo, c.Ativa, c.VersaoCredencial, k.SenhaHash, k.DeveTrocarSenha, k.TemporariaExpiraEm })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new ContaParaLogin(row.Id, row.OrganizacaoId, TipoContaText.Parse(row.Tipo), row.SenhaHash, row.DeveTrocarSenha, row.Ativa, row.VersaoCredencial, row.TemporariaExpiraEm);
    }

    public async Task TrocarSenhaAsync(Guid organizacaoId, Guid contaId, string novoHash, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var now = clock.UtcNow;
        await context.Database.ExecuteSqlAsync(
            $"UPDATE credenciais SET senha_hash = {novoHash}, deve_trocar_senha = false, temporaria_expira_em = NULL, atualizada_em = {now} WHERE conta_id = {contaId}",
            cancellationToken);
        await context.Database.ExecuteSqlAsync($"UPDATE contas SET versao_credencial = versao_credencial + 1 WHERE id = {contaId}", cancellationToken);
    }

    public async Task<bool> RedefinirCredencialTemporariaAsync(Guid organizacaoId, Guid contaId, string novoHash, DateTimeOffset expiraEm, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var now = clock.UtcNow;
        // Only an ACTIVE OPERATIONAL account: the guard is in the statement, not only in the caller. The credential and the version change in the same unit of work.
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE credenciais SET senha_hash = {novoHash}, deve_trocar_senha = true, temporaria_expira_em = {expiraEm}, atualizada_em = {now}
             WHERE conta_id = {contaId} AND EXISTS (SELECT 1 FROM contas c WHERE c.id = credenciais.conta_id AND c.tipo = 'operacional' AND c.ativa)
            """,
            cancellationToken);
        if (rows != 1)
        {
            return false;
        }

        await context.Database.ExecuteSqlAsync($"UPDATE contas SET versao_credencial = versao_credencial + 1 WHERE id = {contaId}", cancellationToken);
        return true;
    }

    public async Task<bool> RehashAsync(Guid organizacaoId, Guid contaId, string hashAnterior, string novoHash, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var now = clock.UtcNow;
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE credenciais SET senha_hash = {novoHash}, atualizada_em = {now} WHERE conta_id = {contaId} AND senha_hash = {hashAnterior}",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> DesativarAsync(Guid organizacaoId, Guid contaId, DateTimeOffset quando, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE contas SET ativa = false, desativada_em = {quando}, versao_credencial = versao_credencial + 1 WHERE id = {contaId} AND ativa",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> DefinirDonoAsync(Guid organizacaoId, Guid contaId, bool dono, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        // AND ativa: the caller read the account as active, but another unit may have deactivated it since; never leave an inactive owner.
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE contas SET dono = {dono}, versao_credencial = versao_credencial + 1 WHERE id = {contaId} AND ativa", cancellationToken);
        return rows == 1;
    }

    public async Task<int> TravarOrganizacaoEContarContasAsync(Guid organizacaoId, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        // The lock is on the row of the organization (RLS shows only the caller's own). NO KEY UPDATE conflicts with itself, not with the FOR KEY SHARE of a foreign key check.
        var locked = await context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM organizacoes WHERE id = {organizacaoId} FOR NO KEY UPDATE").ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            return int.MaxValue; // unknown organization: nothing to found
        }

        var accounts = await context.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM contas").ToListAsync(cancellationToken);
        return (int)accounts.Single();
    }

    public async Task<int> TravarDonosAtivosAsync(Guid organizacaoId, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        // FOR UPDATE: a concurrent unit that wants the same owners waits here until this one commits, then re-reads the committed state.
        var owners = await context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM contas WHERE dono AND ativa FOR UPDATE").ToListAsync(cancellationToken);
        return owners.Count;
    }
}

/// <summary>Pre-authentication lookup (spec 5.2.4) over <see cref="PreAuthDatabase"/> and the SECURITY DEFINER functions of migration 0007.</summary>
public sealed class PostgresPreAuthLookup(PreAuthDatabase database) : IPreAuthLookup
{
    public async Task<ContaParaLogin?> LocalizarOperacionalAsync(Guid organizacaoId, string usuarioNormalizado, CancellationToken cancellationToken) =>
        Map(await database.FindOperationalAsync(organizacaoId, usuarioNormalizado, cancellationToken));

    public async Task<ContaParaLogin?> LocalizarPorEmailAsync(string emailNormalizado, CancellationToken cancellationToken) =>
        Map(await database.FindByEmailAsync(emailNormalizado, cancellationToken));

    private static ContaParaLogin? Map(PreAuthAccount? account) =>
        account is null
            ? null
            : new ContaParaLogin(account.AccountId, account.OrganizationId, TipoContaText.Parse(account.Type), account.PasswordHash, account.MustChangePassword, account.Active, account.CredentialVersion, account.TemporaryExpiresAt);
}
