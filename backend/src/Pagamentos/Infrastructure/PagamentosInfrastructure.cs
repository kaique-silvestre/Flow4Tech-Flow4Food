using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Pagamentos.Application;
using Flow4Tech.Pagamentos.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flow4Tech.Pagamentos.Infrastructure;

internal sealed class PagamentoRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public Guid ComandaId { get; set; }

    public Guid? TurnoId { get; set; }

    public string Metodo { get; set; } = "";

    public long RecebidoCentavos { get; set; }

    public long AplicadoCentavos { get; set; }

    public long TrocoCentavos { get; set; }

    public Guid OperadorId { get; set; }

    public DateTimeOffset RegistradoEm { get; set; }

    public long SaldoAposCentavos { get; set; }

    public int ComandaVersao { get; set; }
}

/// <summary>EF mapping of the payments (schema owned by <c>0020_pagamentos.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class PagamentosDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<PagamentoRow> Pagamentos => Set<PagamentoRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<PagamentoRow>(e =>
        {
            e.ToTable("pagamentos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.ComandaId).HasColumnName("comanda_id");
            e.Property(x => x.TurnoId).HasColumnName("turno_id");
            e.Property(x => x.Metodo).HasColumnName("metodo");
            e.Property(x => x.RecebidoCentavos).HasColumnName("recebido_centavos");
            e.Property(x => x.AplicadoCentavos).HasColumnName("aplicado_centavos");
            e.Property(x => x.TrocoCentavos).HasColumnName("troco_centavos");
            e.Property(x => x.OperadorId).HasColumnName("operador_id");
            e.Property(x => x.RegistradoEm).HasColumnName("registrado_em");
            e.Property(x => x.SaldoAposCentavos).HasColumnName("saldo_apos_centavos");
            e.Property(x => x.ComandaVersao).HasColumnName("comanda_versao");
        });
    }
}

/// <summary>
/// The payments on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>); never filters on <c>organizacao_id</c> (RLS does). Insert-only: the application
/// credential has no UPDATE or DELETE on the table.
/// </summary>
public sealed class PostgresPagamentosStore : IPagamentosStore
{
    public async Task<bool> TentarInserirAsync(Guid organizationId, Pagamento pagamento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pagamento);
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO pagamentos (id, organizacao_id, unidade_id, comanda_id, turno_id, metodo, recebido_centavos, aplicado_centavos, troco_centavos, operador_id, registrado_em, saldo_apos_centavos, comanda_versao)
            VALUES ({pagamento.Id}, {organizationId}, {pagamento.UnidadeId}, {pagamento.ComandaId}, {pagamento.TurnoId}, {pagamento.Metodo}, {pagamento.RecebidoCentavos}, {pagamento.AplicadoCentavos}, {pagamento.TrocoCentavos}, {pagamento.OperadorId}, {pagamento.RegistradoEm}, {pagamento.SaldoAposCentavos}, {pagamento.ComandaVersao})
            ON CONFLICT (id) DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<Pagamento?> AcharAsync(Guid organizationId, Guid pagamentoId, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Pagamentos.AsNoTracking().Where(p => p.Id == pagamentoId).Select(Mapa).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<long> SomaAplicadaAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        return await Soma(context, comandaId).SumAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Pagamento>> ListarDaComandaAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        return await DaComanda(context, comandaId).Select(Mapa).ToListAsync(cancellationToken);
    }

    /// <summary>The applied cents of a comanda, exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<long> Soma(PagamentosDbContext context, Guid comandaId) =>
        context.Pagamentos.AsNoTracking().Where(p => p.ComandaId == comandaId).Select(p => p.AplicadoCentavos);

    /// <summary>The payments of a comanda, oldest first, exposed so a test can assert its SQL text.</summary>
    internal static IQueryable<PagamentoRow> DaComanda(PagamentosDbContext context, Guid comandaId) =>
        context.Pagamentos.AsNoTracking().Where(p => p.ComandaId == comandaId).OrderBy(p => p.RegistradoEm).ThenBy(p => p.Id);

    private static readonly System.Linq.Expressions.Expression<Func<PagamentoRow, Pagamento>> Mapa =
        p => new Pagamento(p.Id, p.UnidadeId, p.ComandaId, p.TurnoId, p.Metodo, p.RecebidoCentavos, p.AplicadoCentavos, p.TrocoCentavos, p.OperadorId, p.RegistradoEm, p.SaldoAposCentavos, p.ComandaVersao);
}

/// <summary>Payment events through the outbox, on the unit of work in progress (they commit or roll back with the change that raised them).</summary>
public sealed class OutboxEventosDePagamentos(OutboxWriter outbox) : IEventosDePagamentos
{
    public Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return outbox.AddAsync(TenantUnitOfWork.Require(organization.OrganizationId), organization, evento, cancellationToken);
    }
}

public static class PagamentosServices
{
    /// <summary>Registers the payments module. Needs <c>AddTenantData</c>, <c>AddAuditoria</c>, <c>ISettings</c>, <c>IClock</c> and the register module (<c>AddCaixa</c>).</summary>
    public static IServiceCollection AddPagamentos(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OutboxWriter>();
        services.AddSingleton<IPagamentosStore, PostgresPagamentosStore>();
        services.AddSingleton<IEventosDePagamentos, OutboxEventosDePagamentos>();
        services.AddSingleton<IProvedorDePagamento, ProvedorManual>();
        services.AddSingleton<GestaoDePagamentos>();
        return services;
    }
}
