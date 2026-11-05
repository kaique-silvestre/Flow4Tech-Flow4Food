using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Permissoes.Infrastructure;

/// <summary>
/// Custom profiles, assignments and overrides on PostgreSQL. Every member joins the unit of work in progress (<see cref="TenantUnitOfWork.Require"/>): the write, the audit record
/// and the session-ending triggers of migration 0010 commit or roll back together. Filtering by organization is RLS's job, never the query's.
/// </summary>
public sealed class PostgresPerfilStore : IPerfilStore
{
    public async Task<bool> CriarAsync(Guid organizationId, PerfilPersonalizado novo, DateTimeOffset em, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(novo);
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        // A name clash would abort the whole transaction (23505), so it is looked up first; the unique constraint still guards the race.
        if (await context.Perfis.AsNoTracking().AnyAsync(p => p.Nome == novo.Nome, cancellationToken))
        {
            return false;
        }

        context.Perfis.Add(new PerfilRow { Id = novo.Id, OrganizacaoId = organizationId, Nome = novo.Nome, Modelo = novo.Modelo, AcompanhaModelo = novo.AcompanhaModelo, CriadoEm = em });
        try
        {
            // The model declares no relationship (the schema owns the foreign keys), so EF does not order the inserts: the profile goes first, its rules after.
            await context.SaveChangesAsync(cancellationToken);
            context.RegrasDosPerfis.AddRange(novo.Regras.Select(r => ToRow<RegraDoPerfilRow>(organizationId, r, row => row.IdDoPerfil = novo.Id)));
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("The profile could not be saved (most likely a concurrent request created one with the same name); retry.", ex);
        }

        return true;
    }

    public async Task<PerfilPersonalizado?> ObterAsync(Guid organizationId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        var profile = await context.Perfis.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var rules = await context.RegrasDosPerfis.AsNoTracking().Where(r => r.IdDoPerfil == id).ToListAsync(cancellationToken);
        return new PerfilPersonalizado(profile.Id, profile.Nome, profile.Modelo, profile.AcompanhaModelo, [.. rules.Select(ToRule)]);
    }

    public async Task SubstituirRegrasAsync(Guid organizationId, Guid id, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regras);
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        // Delete first so the session-ending triggers see the removal as well as the new rules.
        await context.Database.ExecuteSqlAsync($"DELETE FROM perfil_regras WHERE perfil_id = {id}", cancellationToken);
        context.RegrasDosPerfis.AddRange(regras.Select(r => ToRow<RegraDoPerfilRow>(organizationId, r, row => row.IdDoPerfil = id)));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> PerfisDoUsuarioAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken)
    {
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Atribuicoes.AsNoTracking().Where(a => a.UsuarioId == userId).Select(a => a.IdDoPerfil).ToListAsync(cancellationToken);
    }

    public async Task<bool> AtribuirAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"INSERT INTO usuario_perfis (organizacao_id, usuario_id, perfil_id) VALUES ({organizationId}, {userId}, {id}) ON CONFLICT DO NOTHING",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> RemoverAtribuicaoAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync($"DELETE FROM usuario_perfis WHERE usuario_id = {userId} AND perfil_id = {id}", cancellationToken);
        return rows == 1;
    }

    public async Task<IReadOnlyList<RegraDeAcesso>> OverridesAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken)
    {
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.RegrasDosUsuarios.AsNoTracking().Where(r => r.UsuarioId == userId).ToListAsync(cancellationToken);
        return [.. rows.Select(ToRule)];
    }

    public async Task SubstituirOverridesAsync(Guid organizationId, Guid userId, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regras);
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        await context.Database.ExecuteSqlAsync($"DELETE FROM usuario_regras WHERE usuario_id = {userId}", cancellationToken);
        context.RegrasDosUsuarios.AddRange(regras.Select(r => ToRow<RegraDoUsuarioRow>(organizationId, r, row => row.UsuarioId = userId)));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ContaAtivaExisteAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken)
    {
        await using var context = new PermissoesDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Contas.AsNoTracking().AnyAsync(c => c.Id == userId && c.Ativa, cancellationToken);
    }

    internal static T ToRow<T>(Guid organizationId, RegraDeAcesso rule, Action<T> owner)
        where T : RegraRow, new()
    {
        var row = new T
        {
            Id = EntityId.New().Value,
            OrganizacaoId = organizationId,
            Permissao = rule.Permission,
            Efeito = rule.Effect == RuleEffect.Allow ? "allow" : "deny",
            UnidadeId = rule.UnitId,
            MaxPercentual = rule.MaxPercent,
            MaxValorCentavos = rule.MaxValueCents,
            TetoDiarioCentavos = rule.DailyCapCents,
        };
        owner(row);
        return row;
    }

    internal static RegraDeAcesso ToRule(RegraRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new RegraDeAcesso(row.Efeito == "deny" ? RuleEffect.Deny : RuleEffect.Allow, row.Permissao, row.UnidadeId, row.MaxPercentual, row.MaxValorCentavos, row.TetoDiarioCentavos);
    }
}
