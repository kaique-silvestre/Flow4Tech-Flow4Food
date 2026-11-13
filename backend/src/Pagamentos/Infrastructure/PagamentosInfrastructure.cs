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

    public Guid? MetodoId { get; set; }

    public string MetodoNome { get; set; } = "";

    public long GorjetaCentavos { get; set; }

    public string Estado { get; set; } = "confirmado";

    public Guid? DivisaoId { get; set; }

    public int? DivisaoParte { get; set; }
}

internal sealed class MetodoRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public string? Chave { get; set; }

    public string Nome { get; set; } = "";

    public string Tipo { get; set; } = "outro";

    public bool Ativo { get; set; }

    public int Ordem { get; set; }

    public DateTimeOffset CriadoEm { get; set; }
}

/// <summary>EF mapping of the payments (schema owned by <c>0020_pagamentos.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class PagamentosDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<PagamentoRow> Pagamentos => Set<PagamentoRow>();

    public DbSet<MetodoRow> Metodos => Set<MetodoRow>();

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
            e.Property(x => x.MetodoId).HasColumnName("metodo_id");
            e.Property(x => x.MetodoNome).HasColumnName("metodo_nome");
            e.Property(x => x.GorjetaCentavos).HasColumnName("gorjeta_centavos");
            e.Property(x => x.Estado).HasColumnName("estado");
            e.Property(x => x.DivisaoId).HasColumnName("divisao_id");
            e.Property(x => x.DivisaoParte).HasColumnName("divisao_parte");
        });
        modelBuilder.Entity<MetodoRow>(e =>
        {
            e.ToTable("metodos_pagamento");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.Chave).HasColumnName("chave");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.Ordem).HasColumnName("ordem");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
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
        var estado = pagamento.Estado == EstadoDoPagamento.Pendente ? "pendente" : "confirmado";
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO pagamentos (id, organizacao_id, unidade_id, comanda_id, turno_id, metodo, recebido_centavos, aplicado_centavos, troco_centavos, operador_id, registrado_em, saldo_apos_centavos, comanda_versao, metodo_id, metodo_nome, gorjeta_centavos, estado, divisao_id, divisao_parte)
            VALUES ({pagamento.Id}, {organizationId}, {pagamento.UnidadeId}, {pagamento.ComandaId}, {pagamento.TurnoId}, {pagamento.Metodo}, {pagamento.RecebidoCentavos}, {pagamento.AplicadoCentavos}, {pagamento.TrocoCentavos}, {pagamento.OperadorId}, {pagamento.RegistradoEm}, {pagamento.SaldoAposCentavos}, {pagamento.ComandaVersao}, {pagamento.MetodoId}, {pagamento.MetodoNome}, {pagamento.GorjetaCentavos}, {estado}, {pagamento.DivisaoId}, {pagamento.DivisaoParte})
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

    public async Task<IReadOnlyDictionary<(Guid DivisaoId, int Parte), long>> SomaAplicadaPorParteAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        var linhas = await context.Pagamentos.AsNoTracking()
            .Where(p => p.ComandaId == comandaId && p.Estado == "confirmado" && p.DivisaoId != null)
            .GroupBy(p => new { p.DivisaoId, p.DivisaoParte })
            .Select(g => new { g.Key.DivisaoId, g.Key.DivisaoParte, Soma = g.Sum(p => p.AplicadoCentavos) })
            .ToListAsync(cancellationToken);
        return linhas.ToDictionary(l => (l.DivisaoId!.Value, l.DivisaoParte!.Value), l => l.Soma);
    }

    public async Task<IReadOnlyList<Pagamento>> ListarDaComandaAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        return await DaComanda(context, comandaId).Select(Mapa).ToListAsync(cancellationToken);
    }

    /// <summary>The applied cents of a comanda, exposed so a test can assert its SQL text (no organization filter: RLS does it).</summary>
    internal static IQueryable<long> Soma(PagamentosDbContext context, Guid comandaId) =>
        context.Pagamentos.AsNoTracking().Where(p => p.ComandaId == comandaId && p.Estado == "confirmado").Select(p => p.AplicadoCentavos);

    /// <summary>The payments of a comanda, oldest first, exposed so a test can assert its SQL text.</summary>
    internal static IQueryable<PagamentoRow> DaComanda(PagamentosDbContext context, Guid comandaId) =>
        context.Pagamentos.AsNoTracking().Where(p => p.ComandaId == comandaId).OrderBy(p => p.RegistradoEm).ThenBy(p => p.Id);

    private static readonly System.Linq.Expressions.Expression<Func<PagamentoRow, Pagamento>> Mapa =
        p => new Pagamento(p.Id, p.UnidadeId, p.ComandaId, p.TurnoId, p.Metodo, p.RecebidoCentavos, p.AplicadoCentavos, p.TrocoCentavos, p.OperadorId, p.RegistradoEm, p.SaldoAposCentavos, p.ComandaVersao, p.MetodoId, p.MetodoNome, p.GorjetaCentavos, p.Estado == "pendente" ? EstadoDoPagamento.Pendente : EstadoDoPagamento.Confirmado, p.DivisaoId, p.DivisaoParte);
}

/// <summary>
/// The payment methods on PostgreSQL. Joins the caller's unit of work; RLS filters by organization. The factory methods are created on first use (idempotent). Writes of names take an advisory lock per
/// organization so two simultaneous renames to the same name cannot abort a transaction on the unique index. No DELETE: the application credential cannot.
/// </summary>
public sealed class PostgresMetodosDePagamentoStore : IMetodosDePagamentoStore
{
    public async Task GarantirDeFabricaAsync(Guid organizationId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        if (await context.Metodos.AsNoTracking().CountAsync(m => m.Chave != null, cancellationToken) >= RegrasDeMetodo.Fabrica.Count)
        {
            return;
        }

        foreach (var f in RegrasDeMetodo.Fabrica)
        {
            var tipo = f.Tipo == TipoDeMetodo.Dinheiro ? "dinheiro" : "outro";
            _ = await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO metodos_pagamento (id, organizacao_id, chave, nome, tipo, ativo, ordem, criado_em)
                VALUES ({EntityId.New().Value}, {organizationId}, {f.Chave}, {f.Nome}, {tipo}, {f.AtivoNoInicio}, {f.Ordem}, {agora})
                ON CONFLICT DO NOTHING
                """,
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MetodoDePagamento>> ListarAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        return (await context.Metodos.AsNoTracking().OrderBy(m => m.Ordem).ThenBy(m => m.Id).ToListAsync(cancellationToken)).Select(Para).ToList();
    }

    public async Task<MetodoDePagamento?> AcharAsync(Guid organizationId, Guid metodoId, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        var row = await context.Metodos.AsNoTracking().Where(m => m.Id == metodoId).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Para(row);
    }

    public async Task<MetodoDePagamento?> AcharDinheiroAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        var row = await context.Metodos.AsNoTracking().Where(m => m.Chave == "dinheiro").FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Para(row);
    }

    public async Task<bool> CriarAsync(Guid organizationId, MetodoDePagamento metodo, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metodo);
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        await TravarAsync(context, organizationId, cancellationToken);
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO metodos_pagamento (id, organizacao_id, chave, nome, tipo, ativo, ordem, criado_em)
            VALUES ({metodo.Id}, {organizationId}, {metodo.Chave}, {metodo.Nome}, {"outro"}, {metodo.Ativo}, {metodo.Ordem}, {agora})
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<(bool Existe, bool NomeLivre)> AlterarAsync(Guid organizationId, Guid metodoId, string nome, bool ativo, CancellationToken cancellationToken)
    {
        await using var context = new PagamentosDbContext(TenantUnitOfWork.Require(organizationId));
        await TravarAsync(context, organizationId, cancellationToken);
        if (!await context.Metodos.AsNoTracking().AnyAsync(m => m.Id == metodoId, cancellationToken))
        {
            return (false, false);
        }

        ArgumentNullException.ThrowIfNull(nome);
        var emUso = await context.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM metodos_pagamento WHERE id <> {metodoId} AND lower(nome) = lower({nome})) AS \"Value\"").SingleAsync(cancellationToken);
        if (emUso)
        {
            return (true, false);
        }

        _ = await context.Database.ExecuteSqlAsync($"UPDATE metodos_pagamento SET nome = {nome}, ativo = {ativo} WHERE id = {metodoId}", cancellationToken);
        return (true, true);
    }

    private static Task<int> TravarAsync(PagamentosDbContext context, Guid organizationId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"metodos-pagamento:" + organizationId.ToString("D")}, 0))", cancellationToken);

    private static MetodoDePagamento Para(MetodoRow m) => new(m.Id, m.Chave, m.Nome, m.Tipo == "dinheiro" ? TipoDeMetodo.Dinheiro : TipoDeMetodo.Outro, m.Ativo, m.Ordem);
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
        services.AddSingleton<IMetodosDePagamentoStore, PostgresMetodosDePagamentoStore>();
        services.AddSingleton<GestaoDeMetodosDePagamento>();
        services.AddSingleton<IEventosDePagamentos, OutboxEventosDePagamentos>();
        services.AddSingleton<IProvedorDePagamento, ProvedorManual>();
        services.AddSingleton<GestaoDePagamentos>();
        return services;
    }
}
