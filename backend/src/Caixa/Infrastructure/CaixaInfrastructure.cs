using Flow4Tech.Caixa.Application;
using Flow4Tech.Caixa.Domain;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flow4Tech.Caixa.Infrastructure;

internal sealed class TurnoRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public Guid OperadorId { get; set; }

    public DateTimeOffset AbertoEm { get; set; }

    public long FundoCentavos { get; set; }

    public DateTimeOffset? FechadoEm { get; set; }

    public long? ContadoCentavos { get; set; }

    public long? EsperadoCentavos { get; set; }

    public long? DiferencaCentavos { get; set; }

    public long RecebidoDinheiroCentavos { get; set; }
}

/// <summary>EF mapping of the shifts (schema owned by <c>0016_caixa.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class CaixaDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<TurnoRow> Turnos => Set<TurnoRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<TurnoRow>(e =>
        {
            e.ToTable("turnos_caixa");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.OperadorId).HasColumnName("operador_id");
            e.Property(x => x.AbertoEm).HasColumnName("aberto_em");
            e.Property(x => x.FundoCentavos).HasColumnName("fundo_centavos");
            e.Property(x => x.FechadoEm).HasColumnName("fechado_em");
            e.Property(x => x.ContadoCentavos).HasColumnName("contado_centavos");
            e.Property(x => x.EsperadoCentavos).HasColumnName("esperado_centavos");
            e.Property(x => x.DiferencaCentavos).HasColumnName("diferenca_centavos");
            e.Property(x => x.RecebidoDinheiroCentavos).HasColumnName("recebido_dinheiro_centavos");
        });
    }
}

/// <summary>
/// The shifts on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>); never filters on <c>organizacao_id</c> (RLS does). Opening is one
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> against the partial unique index, so two simultaneous opens cannot abort the transaction: the loser just gets zero rows.
/// </summary>
public sealed class PostgresCaixaStore : ICaixaStore, IMovimentosDoCaixa
{
    public async Task<bool> TentarAbrirAsync(Guid organizationId, Turno turno, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turno);
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO turnos_caixa (id, organizacao_id, unidade_id, operador_id, aberto_em, fundo_centavos)
            VALUES ({turno.Id}, {organizationId}, {turno.UnidadeId}, {turno.OperadorId}, {turno.AbertoEm}, {turno.FundoCentavos})
            ON CONFLICT (organizacao_id, unidade_id, operador_id) WHERE fechado_em IS NULL DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<Turno?> AcharAbertoAsync(Guid organizationId, Guid unitId, Guid operadorId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Turnos.AsNoTracking().Where(t => t.UnidadeId == unitId && t.OperadorId == operadorId && t.FechadoEm == null).Select(Mapa).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Turno?> AcharAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Turnos.AsNoTracking().Where(t => t.Id == turnoId).Select(Mapa).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> FecharAsync(Guid organizationId, Guid turnoId, DateTimeOffset fechadoEm, long contadoCentavos, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE turnos_caixa
            SET fechado_em = {fechadoEm},
                contado_centavos = {contadoCentavos},
                esperado_centavos = fundo_centavos + recebido_dinheiro_centavos,
                diferenca_centavos = {contadoCentavos} - (fundo_centavos + recebido_dinheiro_centavos)
            WHERE id = {turnoId} AND fechado_em IS NULL
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<Guid?> TurnoAbertoAsync(Guid organizationId, Guid unitId, Guid operadorId, CancellationToken cancellationToken) =>
        (await AcharAbertoAsync(organizationId, unitId, operadorId, cancellationToken))?.Id;

    public async Task<bool> RegistrarRecebimentoEmDinheiroAsync(Guid organizationId, Guid turnoId, long centavos, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE turnos_caixa SET recebido_dinheiro_centavos = recebido_dinheiro_centavos + {centavos} WHERE id = {turnoId} AND fechado_em IS NULL",
            cancellationToken);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Turno>> ListarAsync(Guid organizationId, Guid unitId, Guid? operadorId, int limite, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Turnos.AsNoTracking()
            .Where(t => t.UnidadeId == unitId && (operadorId == null || t.OperadorId == operadorId))
            .OrderByDescending(t => t.AbertoEm).ThenBy(t => t.Id)
            .Take(limite)
            .Select(Mapa)
            .ToListAsync(cancellationToken);
    }

    private static readonly System.Linq.Expressions.Expression<Func<TurnoRow, Turno>> Mapa =
        t => new Turno(t.Id, t.UnidadeId, t.OperadorId, t.AbertoEm, t.FundoCentavos, t.FechadoEm, t.ContadoCentavos, t.EsperadoCentavos, t.DiferencaCentavos, t.RecebidoDinheiroCentavos);
}

/// <summary>Register events through the outbox, on the unit of work in progress (they commit or roll back with the change that raised them).</summary>
public sealed class OutboxEventosDoCaixa(OutboxWriter outbox) : IEventosDoCaixa
{
    public Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return outbox.AddAsync(TenantUnitOfWork.Require(organization.OrganizationId), organization, evento, cancellationToken);
    }
}

public static class CaixaServices
{
    /// <summary>Registers the cash register module. Needs <c>AddTenantData</c>, <c>AddAuditoria</c>, <c>IClock</c> and an <see cref="IPermissaoDoOperador"/> (adapted in the host).</summary>
    public static IServiceCollection AddCaixa(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OutboxWriter>();
        services.AddSingleton<PostgresCaixaStore>();
        services.AddSingleton<ICaixaStore>(p => p.GetRequiredService<PostgresCaixaStore>());
        services.AddSingleton<IMovimentosDoCaixa>(p => p.GetRequiredService<PostgresCaixaStore>());
        services.AddSingleton<IEventosDoCaixa, OutboxEventosDoCaixa>();
        services.AddSingleton<GestaoDoCaixa>();
        return services;
    }
}
