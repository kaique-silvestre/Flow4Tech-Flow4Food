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

    public long? SuprimentosCentavos { get; set; }

    public long? SangriasCentavos { get; set; }

    public string? SituacaoConferencia { get; set; }
}

internal sealed class MovimentoRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public Guid TurnoId { get; set; }

    public string Tipo { get; set; } = "";

    public long ValorCentavos { get; set; }

    public string Motivo { get; set; } = "";

    public string? Destino { get; set; }

    public Guid OperadorId { get; set; }

    public DateTimeOffset CriadoEm { get; set; }
}

internal sealed class AprovacaoRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public Guid UnidadeId { get; set; }

    public Guid TurnoId { get; set; }

    public string Tipo { get; set; } = "";

    public Guid AprovadorId { get; set; }

    public string Motivo { get; set; } = "";

    public long DiferencaAntesCentavos { get; set; }

    public long DiferencaDepoisCentavos { get; set; }

    public DateTimeOffset CriadoEm { get; set; }
}

/// <summary>Shapes of raw SQL answers (columns aliased to these names).</summary>
internal sealed class LinhaDeValor
{
    public Guid Id { get; set; }

    public long Valor { get; set; }
}

internal sealed class LinhaDeMetodo
{
    public Guid Id { get; set; }

    public string Nome { get; set; } = "";

    public string Tipo { get; set; } = "";
}

internal sealed class LinhaDeContagem
{
    public Guid MetodoId { get; set; }

    public string MetodoNome { get; set; } = "";

    public long ContadoCentavos { get; set; }

    public long EsperadoCentavos { get; set; }
}

/// <summary>EF mapping of the shifts (schema owned by <c>0016_caixa.sql</c> and <c>0031_caixa_completo.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class CaixaDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<TurnoRow> Turnos => Set<TurnoRow>();

    public DbSet<MovimentoRow> Movimentos => Set<MovimentoRow>();

    public DbSet<AprovacaoRow> Aprovacoes => Set<AprovacaoRow>();

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
            e.Property(x => x.SuprimentosCentavos).HasColumnName("suprimentos_centavos");
            e.Property(x => x.SangriasCentavos).HasColumnName("sangrias_centavos");
            e.Property(x => x.SituacaoConferencia).HasColumnName("situacao_conferencia");
        });
        modelBuilder.Entity<MovimentoRow>(e =>
        {
            e.ToTable("movimentos_caixa");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.TurnoId).HasColumnName("turno_id");
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.ValorCentavos).HasColumnName("valor_centavos");
            e.Property(x => x.Motivo).HasColumnName("motivo");
            e.Property(x => x.Destino).HasColumnName("destino");
            e.Property(x => x.OperadorId).HasColumnName("operador_id");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
        });
        modelBuilder.Entity<AprovacaoRow>(e =>
        {
            e.ToTable("aprovacoes_diferenca");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.TurnoId).HasColumnName("turno_id");
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.AprovadorId).HasColumnName("aprovador_id");
            e.Property(x => x.Motivo).HasColumnName("motivo");
            e.Property(x => x.DiferencaAntesCentavos).HasColumnName("diferenca_antes_centavos");
            e.Property(x => x.DiferencaDepoisCentavos).HasColumnName("diferenca_depois_centavos");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
        });
    }
}

/// <summary>
/// The shifts on PostgreSQL. Joins the caller's unit of work (<see cref="TenantUnitOfWork.Require"/>); never filters on <c>organizacao_id</c> (RLS does). Opening is one
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> against the partial unique index, so two simultaneous opens cannot abort the transaction: the loser just gets zero rows. Movements and closing serialize on the
/// shift row (<c>FOR UPDATE</c>), and the closing statement sums the movements AFTER that lock, in a fresh statement. The per-method expectation reads <c>pagamentos</c> and <c>metodos_pagamento</c> (schema of Pagamentos)
/// by SQL, read-only, keyed by the shift id the payments already carry.
/// </summary>
public sealed class PostgresCaixaStore : ICaixaStore, IMovimentosDoCaixa
{
    private static readonly Guid Suprimento = new("00000000-0000-0000-0000-000000000001");

    private static readonly Guid Sangria = new("00000000-0000-0000-0000-000000000002");

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
        var row = await context.Turnos.AsNoTracking().Where(t => t.UnidadeId == unitId && t.OperadorId == operadorId && t.FechadoEm == null).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Mapa(row);
    }

    public async Task<Turno?> AcharAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var row = await context.Turnos.AsNoTracking().Where(t => t.Id == turnoId).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Mapa(row);
    }

    public async Task<bool> FecharAsync(Guid organizationId, Guid turnoId, DateTimeOffset fechadoEm, long contadoCentavos, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));

        // Statement 1 takes the row lock (a movement in flight finishes first); statement 2 then sums with a snapshot that sees it.
        var travado = await context.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM turnos_caixa WHERE id = {turnoId} AND fechado_em IS NULL FOR UPDATE").ToListAsync(cancellationToken);
        if (travado.Count == 0)
        {
            return false;
        }

        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE turnos_caixa t
            SET fechado_em = {fechadoEm},
                contado_centavos = {contadoCentavos},
                suprimentos_centavos = m.suprimentos,
                sangrias_centavos = m.sangrias,
                esperado_centavos = t.fundo_centavos + t.recebido_dinheiro_centavos + m.suprimentos - m.sangrias,
                diferenca_centavos = {contadoCentavos} - (t.fundo_centavos + t.recebido_dinheiro_centavos + m.suprimentos - m.sangrias)
            FROM (
              SELECT COALESCE(SUM(valor_centavos) FILTER (WHERE tipo = 'suprimento'), 0)::bigint AS suprimentos,
                     COALESCE(SUM(valor_centavos) FILTER (WHERE tipo = 'sangria'), 0)::bigint AS sangrias
              FROM movimentos_caixa WHERE turno_id = {turnoId}
            ) m
            WHERE t.id = {turnoId} AND t.fechado_em IS NULL
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

    public Task<IReadOnlyList<Turno>> ListarAsync(Guid organizationId, Guid unitId, Guid? operadorId, int limite, CancellationToken cancellationToken) =>
        ListarHistoricoAsync(organizationId, unitId, operadorId, null, null, null, limite, cancellationToken);

    public async Task<IReadOnlyList<Turno>> ListarHistoricoAsync(Guid organizationId, Guid unitId, Guid? operadorId, DateTimeOffset? de, DateTimeOffset? ate, SituacaoDaConferencia? situacao, int limite, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var consulta = context.Turnos.AsNoTracking().Where(t => t.UnidadeId == unitId && (operadorId == null || t.OperadorId == operadorId));
        if (de is { } inicio)
        {
            consulta = consulta.Where(t => t.AbertoEm >= inicio);
        }

        if (ate is { } fim)
        {
            consulta = consulta.Where(t => t.AbertoEm < fim);
        }

        consulta = situacao switch
        {
            SituacaoDaConferencia.Pendente => consulta.Where(t => t.SituacaoConferencia == "pendente"),
            SituacaoDaConferencia.Aprovada => consulta.Where(t => t.SituacaoConferencia == "aprovada"),
            SituacaoDaConferencia.Concluida => consulta.Where(t => t.FechadoEm != null && (t.SituacaoConferencia == null || t.SituacaoConferencia == "concluida")),
            _ => consulta,
        };

        var rows = await consulta.OrderByDescending(t => t.AbertoEm).ThenBy(t => t.Id).Take(limite).ToListAsync(cancellationToken);
        return [.. rows.Select(Mapa)];
    }

    public async Task<long?> SaldoDaGavetaTravandoAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var saldos = await context.Database.SqlQuery<long>(
            $"""
            SELECT (t.fundo_centavos + t.recebido_dinheiro_centavos
                    + COALESCE((SELECT SUM(CASE m.tipo WHEN 'suprimento' THEN m.valor_centavos ELSE -m.valor_centavos END) FROM movimentos_caixa m WHERE m.turno_id = t.id), 0))::bigint AS "Value"
            FROM turnos_caixa t WHERE t.id = {turnoId} AND t.fechado_em IS NULL FOR UPDATE OF t
            """).ToListAsync(cancellationToken);
        return saldos.Count == 0 ? null : saldos[0];
    }

    public async Task InserirMovimentoAsync(Guid organizationId, MovimentoDoCaixa movimento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movimento);
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var tipo = movimento.Tipo == TipoDeMovimento.Sangria ? "sangria" : "suprimento";
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO movimentos_caixa (id, organizacao_id, unidade_id, turno_id, tipo, valor_centavos, motivo, destino, operador_id, criado_em)
            VALUES ({movimento.Id}, {organizationId}, {movimento.UnidadeId}, {movimento.TurnoId}, {tipo}, {movimento.ValorCentavos}, {movimento.Motivo}, {movimento.Destino}, {movimento.OperadorId}, {movimento.CriadoEm})
            """,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MovimentoDoCaixa>> ListarMovimentosAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Movimentos.AsNoTracking().Where(m => m.TurnoId == turnoId).OrderBy(m => m.CriadoEm).ThenBy(m => m.Id).ToListAsync(cancellationToken);
        return [.. rows.Select(m => new MovimentoDoCaixa(m.Id, m.TurnoId, m.UnidadeId, m.Tipo == "sangria" ? TipoDeMovimento.Sangria : TipoDeMovimento.Suprimento, m.ValorCentavos, m.Motivo, m.Destino, m.OperadorId, m.CriadoEm))];
    }

    public async Task<(long Suprimentos, long Sangrias)> TotaisDosMovimentosAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var linhas = await context.Database.SqlQuery<LinhaDeValor>(
            $"""
            SELECT CASE tipo WHEN 'suprimento' THEN '00000000-0000-0000-0000-000000000001'::uuid ELSE '00000000-0000-0000-0000-000000000002'::uuid END AS "Id", SUM(valor_centavos)::bigint AS "Valor"
            FROM movimentos_caixa WHERE turno_id = {turnoId} GROUP BY tipo
            """).ToListAsync(cancellationToken);
        return (linhas.Where(l => l.Id == Suprimento).Select(l => l.Valor).FirstOrDefault(), linhas.Where(l => l.Id == Sangria).Select(l => l.Valor).FirstOrDefault());
    }

    public async Task<IReadOnlyDictionary<Guid, MetodoDeContagem>> MetodosAsync(Guid organizationId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, MetodoDeContagem>();
        }

        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var lista = ids.ToArray();
        var linhas = await context.Database.SqlQuery<LinhaDeMetodo>($"SELECT id AS \"Id\", nome AS \"Nome\", tipo AS \"Tipo\" FROM metodos_pagamento WHERE id = ANY({lista})").ToListAsync(cancellationToken);
        return linhas.ToDictionary(l => l.Id, l => new MetodoDeContagem(l.Id, l.Nome, l.Tipo == "dinheiro"));
    }

    public async Task<IReadOnlyDictionary<Guid, long>> RecebidoPorMetodoAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var linhas = await context.Database.SqlQuery<LinhaDeValor>(
            $"""
            SELECT metodo_id AS "Id", SUM(aplicado_centavos + gorjeta_centavos)::bigint AS "Valor"
            FROM pagamentos WHERE turno_id = {turnoId} AND estado = 'confirmado' AND metodo_id IS NOT NULL GROUP BY metodo_id
            """).ToListAsync(cancellationToken);
        return linhas.ToDictionary(l => l.Id, l => l.Valor);
    }

    public async Task InserirContagensAsync(Guid organizationId, Guid turnoId, int sequencia, IReadOnlyList<ContagemDoMetodo> contagens, DateTimeOffset criadoEm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contagens);
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        foreach (var c in contagens)
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO contagens_turno (organizacao_id, turno_id, metodo_id, sequencia, metodo_nome, contado_centavos, esperado_centavos, criado_em)
                VALUES ({organizationId}, {turnoId}, {c.MetodoId}, {sequencia}, {c.MetodoNome}, {c.ContadoCentavos}, {c.EsperadoCentavos}, {criadoEm})
                """,
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ContagemDoMetodo>> ContagensAtuaisAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var linhas = await context.Database.SqlQuery<LinhaDeContagem>(
            $"""
            SELECT DISTINCT ON (metodo_id) metodo_id AS "MetodoId", metodo_nome AS "MetodoNome", contado_centavos AS "ContadoCentavos", esperado_centavos AS "EsperadoCentavos"
            FROM contagens_turno WHERE turno_id = {turnoId} ORDER BY metodo_id, sequencia DESC
            """).ToListAsync(cancellationToken);
        return [.. linhas.OrderBy(l => l.MetodoNome, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.MetodoId).Select(l => new ContagemDoMetodo(l.MetodoId, l.MetodoNome, l.ContadoCentavos, l.EsperadoCentavos))];
    }

    public async Task<int> ProximaSequenciaAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        return (await context.Database.SqlQuery<int>($"SELECT COALESCE(MAX(sequencia), 0) + 1 AS \"Value\" FROM contagens_turno WHERE turno_id = {turnoId}").ToListAsync(cancellationToken))[0];
    }

    public async Task DefinirSituacaoAsync(Guid organizationId, Guid turnoId, SituacaoDaConferencia situacao, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var texto = Texto(situacao);
        await context.Database.ExecuteSqlAsync($"UPDATE turnos_caixa SET situacao_conferencia = {texto} WHERE id = {turnoId} AND fechado_em IS NOT NULL", cancellationToken);
    }

    public async Task<bool> AprovarAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Database.ExecuteSqlAsync($"UPDATE turnos_caixa SET situacao_conferencia = 'aprovada' WHERE id = {turnoId} AND situacao_conferencia = 'pendente'", cancellationToken) == 1;
    }

    public async Task<bool> RecontarDinheiroAsync(Guid organizationId, Guid turnoId, long contadoCentavos, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        return await context.Database.ExecuteSqlAsync(
            $"UPDATE turnos_caixa SET contado_centavos = {contadoCentavos}, diferenca_centavos = {contadoCentavos} - esperado_centavos WHERE id = {turnoId} AND situacao_conferencia = 'pendente'",
            cancellationToken) == 1;
    }

    public async Task InserirAprovacaoAsync(Guid organizationId, Guid unitId, AprovacaoDeDiferenca aprovacao, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aprovacao);
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO aprovacoes_diferenca (id, organizacao_id, unidade_id, turno_id, tipo, aprovador_id, motivo, diferenca_antes_centavos, diferenca_depois_centavos, criado_em)
            VALUES ({aprovacao.Id}, {organizationId}, {unitId}, {aprovacao.TurnoId}, {aprovacao.Tipo}, {aprovacao.AprovadorId}, {aprovacao.Motivo}, {aprovacao.DiferencaAntesCentavos}, {aprovacao.DiferencaDepoisCentavos}, {aprovacao.CriadoEm})
            """,
            cancellationToken);
    }

    public async Task<IReadOnlyList<AprovacaoDeDiferenca>> ListarAprovacoesAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken)
    {
        await using var context = new CaixaDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Aprovacoes.AsNoTracking().Where(a => a.TurnoId == turnoId).OrderBy(a => a.CriadoEm).ThenBy(a => a.Id).ToListAsync(cancellationToken);
        return [.. rows.Select(a => new AprovacaoDeDiferenca(a.Id, a.TurnoId, a.Tipo, a.AprovadorId, a.Motivo, a.DiferencaAntesCentavos, a.DiferencaDepoisCentavos, a.CriadoEm))];
    }

    private static string Texto(SituacaoDaConferencia situacao) => situacao switch
    {
        SituacaoDaConferencia.Pendente => "pendente",
        SituacaoDaConferencia.Aprovada => "aprovada",
        _ => "concluida",
    };

    private static Turno Mapa(TurnoRow t) =>
        new(
            t.Id,
            t.UnidadeId,
            t.OperadorId,
            t.AbertoEm,
            t.FundoCentavos,
            t.FechadoEm,
            t.ContadoCentavos,
            t.EsperadoCentavos,
            t.DiferencaCentavos,
            t.RecebidoDinheiroCentavos,
            t.SuprimentosCentavos,
            t.SangriasCentavos,
            t.FechadoEm is null ? null : t.SituacaoConferencia switch
            {
                "pendente" => SituacaoDaConferencia.Pendente,
                "aprovada" => SituacaoDaConferencia.Aprovada,
                _ => SituacaoDaConferencia.Concluida,
            });
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
    /// <summary>Registers the cash register module. Needs <c>AddTenantData</c>, <c>AddAuditoria</c>, <c>IClock</c>, <c>ISettings</c>, an <see cref="IPermissaoDoOperador"/> and an <see cref="IDecisaoDeCaixa"/> (both adapted in the host).</summary>
    public static IServiceCollection AddCaixa(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OutboxWriter>();
        services.AddSingleton<PostgresCaixaStore>();
        services.AddSingleton<ICaixaStore>(p => p.GetRequiredService<PostgresCaixaStore>());
        services.AddSingleton<IMovimentosDoCaixa>(p => p.GetRequiredService<PostgresCaixaStore>());
        services.AddSingleton<IEventosDoCaixa, OutboxEventosDoCaixa>();
        services.AddSingleton<GestaoDoCaixa>();
        services.AddSingleton<GestaoDeMovimentosDoCaixa>();
        services.AddSingleton<GestaoDaDiferenca>();
        return services;
    }
}
