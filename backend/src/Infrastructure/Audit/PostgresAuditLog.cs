using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Infrastructure.Audit;

/// <summary>
/// <see cref="IAuditLog"/> on PostgreSQL (spec 5.6). Append-only (the application role has no UPDATE/DELETE on the table, migration 0005) and
/// hash-chained per organization (<see cref="AuditHash"/>).
/// SERIALIZATION: appending reads the last record and links to it, so two concurrent writers of the SAME organization must not both read the
/// same last record. Each append first takes a transaction-scoped advisory lock keyed by the organization
/// (<c>pg_advisory_xact_lock</c>): the second writer waits until the first commits or rolls back, then reads the new last record. Different
/// organizations use different keys and do not wait for each other (a 64-bit hash collision would only add harmless waiting). The lock is held until the
/// whole unit of work ends, so keep units that audit short. <c>UNIQUE (organizacao_id, sequencia)</c> is the backstop: a fork cannot be stored.
/// Two doors: <see cref="RecordAsync"/> joins the unit of work of the action (rollback removes the record); <see cref="RecordDetachedAsync"/>
/// is for deliberate failure records and runs on its own transaction, after the unit of work ended.
/// </summary>
public sealed class PostgresAuditLog(TenantDatabase tenant, IClock clock) : IAuditLog
{
    public async Task RecordAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(entry);
        AuditEntryRules.EnsureValid(entry);
        await AppendAsync(TenantUnitOfWork.Require(organization.OrganizationId), organization, entry, cancellationToken);
    }

    public async Task RecordDetachedAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(entry);
        AuditEntryRules.EnsureValid(entry);
        if (TenantUnitOfWork.InProgress(organization.OrganizationId))
        {
            throw new InvalidOperationException("A detached audit record cannot be written inside a unit of work: call it after the unit of work ended (it would contend for the chain lock held by the unit's own records).");
        }

        await using var transaction = await tenant.BeginAsync(organization, cancellationToken);
        await AppendAsync(transaction, organization, entry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string ResultText(AuditResult result) => result switch
    {
        AuditResult.Sucesso => "sucesso",
        AuditResult.Negado => "negado",
        AuditResult.Falha => "falha",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private async Task AppendAsync(TenantTransaction transaction, OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var context = new AuditContext(transaction);
        var lockKey = "auditoria:" + organization.OrganizationId.ToString("D");
        await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        // RLS scopes this to the organization of the transaction; never filter on organizacao_id here.
        var last = await context.Registros.AsNoTracking()
            .OrderByDescending(r => r.Sequencia)
            .Select(r => new { r.Sequencia, r.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        var data = new AuditRecordData(
            EntityId.New().Value,
            organization.OrganizationId,
            entry.UnitId,
            entry.UserId,
            entry.SessionId,
            entry.Action,
            entry.ObjectType,
            entry.ObjectId,
            AuditHash.CanonicalJson(entry.Before?.ToJsonString()),
            AuditHash.CanonicalJson(entry.After?.ToJsonString()),
            ResultText(entry.Result),
            Correlation.Current,
            AuditHash.ToMicroseconds(clock.UtcNow),
            (last?.Sequencia ?? 0) + 1,
            last?.Hash);

        context.Registros.Add(new AuditRow
        {
            Id = data.Id,
            OrganizacaoId = data.OrganizationId,
            UnidadeId = data.UnitId,
            UsuarioId = data.UserId,
            SessaoId = data.SessionId,
            Acao = data.Action,
            ObjetoTipo = data.ObjectType,
            ObjetoId = data.ObjectId,
            Antes = data.Before,
            Depois = data.After,
            Resultado = data.Result,
            CorrelationId = data.CorrelationId,
            OcorridoEm = data.OccurredAt,
            Sequencia = data.Sequence,
            HashAnterior = data.PreviousHash,
            Hash = AuditHash.Compute(data),
        });
        await context.SaveChangesAsync(cancellationToken);
    }
}
