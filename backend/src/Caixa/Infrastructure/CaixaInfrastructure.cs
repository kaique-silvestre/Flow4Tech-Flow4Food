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
        });
    }
}

/// <summary>
/// The shifts on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>); never filters on <c>organizacao_id</c> (RLS does). Opening is one
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> against the partial unique index, so two simultaneous opens cannot abort the transaction: the loser just gets zero rows.
/// </summary>
public sealed class PostgresCaixaStore : ICaixaStore
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

    public async Task<bool> FecharAsync(Guid organizationId, Turno fechado, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fechado);
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Turnos
            .Where(t => t.Id == fechado.Id && t.FechadoEm == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.FechadoEm, fechado.FechadoEm).SetProperty(t => t.ContadoCentavos, fechado.ContadoCentavos)
                    .SetProperty(t => t.EsperadoCentavos, fechado.EsperadoCentavos).SetProperty(t => t.DiferencaCentavos, fechado.DiferencaCentavos),
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
        t => new Turno(t.Id, t.UnidadeId, t.OperadorId, t.AbertoEm, t.FundoCentavos, t.FechadoEm, t.ContadoCentavos, t.EsperadoCentavos, t.DiferencaCentavos);
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
        services.AddSingleton<ICaixaStore, PostgresCaixaStore>();
        services.AddSingleton<IEventosDoCaixa, OutboxEventosDoCaixa>();
        services.AddSingleton<GestaoDoCaixa>();
        return services;
    }
}
