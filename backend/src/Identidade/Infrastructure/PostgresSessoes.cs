using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Identidade.Infrastructure;

internal sealed class RefreshTravadoRow
{
    public Guid Id { get; set; }

    public Guid SessaoId { get; set; }

    public DateTimeOffset? UsadoEm { get; set; }
}

/// <summary>
/// Sessions on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>): the organization is the one the unit was opened with, RLS filters, nothing
/// here filters on <c>organizacao_id</c>. Writes are explicit interpolated SQL (the statement is visible); reads are EF with <c>AsNoTracking</c>.
/// </summary>
public sealed class PostgresSessaoStore : ISessaoStore, IRevogacaoDeSessoes
{
    public async Task InserirAsync(Guid organizacaoId, NovaSessao sessao, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessao);
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO sessoes (id, organizacao_id, conta_id, unidade_ativa_id, familia_id, deve_trocar_senha, versao_credencial, criada_em, ultima_atividade_em, expira_inatividade_em, expira_em, ip, agente)
            VALUES ({sessao.Id}, {organizacaoId}, {sessao.ContaId}, {sessao.UnidadeAtivaId}, {sessao.FamiliaId}, {sessao.DeveTrocarSenha}, {sessao.VersaoCredencial}, {sessao.CriadaEm}, {sessao.CriadaEm}, {sessao.ExpiraInatividadeEm}, {sessao.ExpiraEm}, {sessao.Ip}, {sessao.Agente})
            """,
            cancellationToken);
    }

    public async Task InserirRefreshAsync(Guid organizacaoId, Guid id, Guid sessaoId, byte[] hash, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO sessao_refresh (id, organizacao_id, sessao_id, hash, criado_em) VALUES ({id}, {organizacaoId}, {sessaoId}, {hash}, {agora})",
            cancellationToken);
    }

    public async Task<SessaoParaRequisicao?> ObterParaRequisicaoAsync(Guid organizacaoId, Guid sessaoId, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var row = await (from s in context.Sessoes.AsNoTracking()
                         where s.Id == sessaoId
                         join c in context.Contas.AsNoTracking() on s.ContaId equals c.Id
                         select new { Session = s, c.Ativa, c.VersaoCredencial })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new SessaoParaRequisicao(
                row.Session.Id,
                row.Session.ContaId,
                row.Session.UnidadeAtivaId,
                row.Session.DeveTrocarSenha,
                row.Session.VersaoCredencial,
                row.VersaoCredencial,
                row.Ativa,
                row.Session.UltimaAtividadeEm,
                row.Session.ExpiraInatividadeEm,
                row.Session.ExpiraEm,
                row.Session.RevogadaEm);
    }

    public async Task<RefreshEncontrado?> ObterRefreshTravadoAsync(Guid organizacaoId, byte[] hash, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        // FOR UPDATE: a concurrent request with the same token waits here until this unit of work commits, then re-reads the row (READ COMMITTED) and sees usado_em set.
        var rows = await context.Database
            .SqlQuery<RefreshTravadoRow>($"SELECT id AS \"Id\", sessao_id AS \"SessaoId\", usado_em AS \"UsadoEm\" FROM sessao_refresh WHERE hash = {hash} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : new RefreshEncontrado(rows[0].Id, rows[0].SessaoId, rows[0].UsadoEm);
    }

    public async Task MarcarRefreshUsadoAsync(Guid organizacaoId, Guid refreshId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync($"UPDATE sessao_refresh SET usado_em = {agora} WHERE id = {refreshId}", cancellationToken);
    }

    public async Task PodarRefreshUsadosAsync(Guid organizacaoId, Guid sessaoId, DateTimeOffset usadosAntesDe, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync($"DELETE FROM sessao_refresh WHERE sessao_id = {sessaoId} AND usado_em < {usadosAntesDe}", cancellationToken);
    }

    public async Task TocarAtividadeAsync(Guid organizacaoId, Guid sessaoId, DateTimeOffset agora, DateTimeOffset expiraInatividadeEm, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync(
            $"UPDATE sessoes SET ultima_atividade_em = {agora}, expira_inatividade_em = {expiraInatividadeEm} WHERE id = {sessaoId} AND revogada_em IS NULL",
            cancellationToken);
    }

    public async Task<bool> RevogarAsync(Guid organizacaoId, Guid sessaoId, string motivo, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE sessoes SET revogada_em = {agora}, revogada_motivo = {motivo} WHERE id = {sessaoId} AND revogada_em IS NULL",
            cancellationToken);
        return rows == 1;
    }

    public async Task RevogarOutrasDaContaAsync(Guid organizacaoId, Guid contaId, Guid exceto, string motivo, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync(
            $"UPDATE sessoes SET revogada_em = {agora}, revogada_motivo = {motivo} WHERE conta_id = {contaId} AND id <> {exceto} AND revogada_em IS NULL",
            cancellationToken);
    }

    public async Task RevogarTodasDaContaAsync(Guid organizacaoId, Guid contaId, string motivo, DateTimeOffset agora, CancellationToken cancellationToken) =>
        await RevogarOutrasDaContaAsync(organizacaoId, contaId, Guid.Empty, motivo, agora, cancellationToken);

    public async Task DefinirUnidadeAtivaAsync(Guid organizacaoId, Guid sessaoId, Guid unidadeId, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        await context.Database.ExecuteSqlAsync($"UPDATE sessoes SET unidade_ativa_id = {unidadeId} WHERE id = {sessaoId} AND revogada_em IS NULL", cancellationToken);
    }

    public async Task<SessaoParaListar?> ObterVivaAsync(Guid organizacaoId, Guid sessaoId, DateTimeOffset agora, CancellationToken cancellationToken) =>
        await ListarAsync(organizacaoId, sessaoId, null, agora, cancellationToken) is [var first, ..] ? first : null;

    public Task<IReadOnlyList<SessaoParaListar>> ListarVivasAsync(Guid organizacaoId, Guid? contaId, DateTimeOffset agora, CancellationToken cancellationToken) =>
        ListarAsync(organizacaoId, null, contaId, agora, cancellationToken);

    private static async Task<IReadOnlyList<SessaoParaListar>> ListarAsync(Guid organizacaoId, Guid? sessaoId, Guid? contaId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));
        // "Live" as the per-request check sees it: not revoked, inside both clocks, the account active and the credential version unchanged.
        var query = from s in context.Sessoes.AsNoTracking()
                    join c in context.Contas.AsNoTracking() on s.ContaId equals c.Id
                    where s.RevogadaEm == null && s.ExpiraEm > agora && s.ExpiraInatividadeEm > agora && c.Ativa && c.VersaoCredencial == s.VersaoCredencial
                    select s;
        if (sessaoId is { } id)
        {
            query = query.Where(s => s.Id == id);
        }

        if (contaId is { } conta)
        {
            query = query.Where(s => s.ContaId == conta);
        }

        var rows = await query.OrderBy(s => s.CriadaEm).ThenBy(s => s.Id).ToListAsync(cancellationToken);
        return [.. rows.Select(s => new SessaoParaListar(s.Id, s.ContaId, s.UnidadeAtivaId, s.CriadaEm, s.UltimaAtividadeEm, s.ExpiraEm, s.Ip, s.Agente))];
    }
}

/// <summary>Security events through the outbox, on the unit of work in progress (they commit or roll back with the revocation that raised them).</summary>
public sealed class OutboxEventosDeSeguranca(OutboxWriter outbox) : IEventosDeSeguranca
{
    public Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return outbox.AddAsync(TenantUnitOfWork.Require(organization.OrganizationId), organization, evento, cancellationToken);
    }
}
