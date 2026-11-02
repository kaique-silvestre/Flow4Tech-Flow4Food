using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Infrastructure.Audit;

public enum AuditViolationKind
{
    /// <summary>The record's content does not match its hash (changed field, or its hash/link/sequence was rewritten).</summary>
    Altered,

    /// <summary>A gap in the sequence: one or more records are missing before this one.</summary>
    Removed,

    /// <summary>The record is intact but does not follow the stored hash of the record before it (moved or re-linked).</summary>
    Reordered,

    /// <summary>The newest records the caller knew about (the anchor) are gone or different.</summary>
    Truncated,
}

public sealed record AuditViolation(long Sequence, AuditViolationKind Kind);

/// <summary>Last record of a chain: what a caller can keep to detect later truncation of the tail.</summary>
public sealed record AuditHead(long Sequence, string HashHex);

public sealed record AuditVerification(bool IsValid, long Records, IReadOnlyList<AuditViolation> Violations, AuditHead? Head);

/// <summary>
/// Walks the audit chain of ONE organization, in sequence order, and reports every record that was altered, removed or reordered (spec 5.6.5).
/// Per record: the sequence must be the next one (gap = <see cref="AuditViolationKind.Removed"/>), the content must hash to the stored hash
/// (<see cref="AuditViolationKind.Altered"/>), and <c>hash_anterior</c> must equal the stored hash of the record before it
/// (<see cref="AuditViolationKind.Reordered"/>). A violation raises the outbox event <see cref="AuditIntegrityViolated"/> (no consumer yet).
/// LIMITS, stated because they are the spec's declared debt (5.6.5, open question 3): the chain alone cannot prove its own TAIL, so removing the newest
/// records is detected only when the caller passes the <paramref name="anchor"/> it saw earlier (a previous <see cref="AuditVerification.Head"/>), and an
/// attacker who can rewrite the whole table and recompute every hash is detected only against a copy kept outside this database.
/// </summary>
public sealed class AuditChainVerifier(TenantDatabase tenant, OutboxWriter outbox, IClock clock)
{
    private const int BatchSize = 500;
    private const int MaxReported = 100;

    public async Task<AuditVerification> VerifyAsync(OrganizationContext organization, AuditHead? anchor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        var result = await ScanAsync(organization, anchor, cancellationToken);
        if (!result.IsValid)
        {
            var first = result.Violations.OrderBy(v => v.Sequence).First();
            await using var transaction = await tenant.BeginAsync(organization, cancellationToken);
            await outbox.AddAsync(
                transaction,
                organization,
                new AuditIntegrityViolated(EntityId.New().Value, organization.OrganizationId, first.Sequence, first.Kind.ToString(), clock.UtcNow),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    private async Task<AuditVerification> ScanAsync(OrganizationContext organization, AuditHead? anchor, CancellationToken cancellationToken)
    {
        await using var transaction = await tenant.BeginAsync(organization, cancellationToken);
        await using var context = new AuditContext(transaction);

        var violations = new List<AuditViolation>();
        void Report(long sequence, AuditViolationKind kind)
        {
            if (violations.Count < MaxReported)
            {
                violations.Add(new AuditViolation(sequence, kind));
            }
        }

        long cursor = 0;
        long expected = 1;
        long count = 0;
        byte[]? previousStored = null;
        AuditHead? head = null;
        var anchorSeen = anchor is null;

        while (true)
        {
            var batch = await context.Registros.AsNoTracking()
                .Where(r => r.Sequencia > cursor)
                .OrderBy(r => r.Sequencia)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                count++;
                var gap = row.Sequencia != expected;
                if (gap)
                {
                    Report(row.Sequencia, AuditViolationKind.Removed);
                }

                var altered = !AuditHash.Compute(ToData(row)).AsSpan().SequenceEqual(row.Hash);
                if (altered)
                {
                    Report(row.Sequencia, AuditViolationKind.Altered);
                }

                if (!gap && !altered && !LinkMatches(row.HashAnterior, previousStored))
                {
                    Report(row.Sequencia, AuditViolationKind.Reordered);
                }

                if (anchor is not null && row.Sequencia == anchor.Sequence)
                {
                    anchorSeen = true;
                    if (!string.Equals(Convert.ToHexStringLower(row.Hash), anchor.HashHex, StringComparison.OrdinalIgnoreCase))
                    {
                        Report(row.Sequencia, AuditViolationKind.Altered);
                    }
                }

                previousStored = row.Hash;
                expected = row.Sequencia + 1;
                cursor = row.Sequencia;
                head = new AuditHead(row.Sequencia, Convert.ToHexStringLower(row.Hash));
            }
        }

        if (anchor is not null && (!anchorSeen || head is null || head.Sequence < anchor.Sequence))
        {
            Report(anchor.Sequence, AuditViolationKind.Truncated);
        }

        return new AuditVerification(violations.Count == 0, count, violations, head);
    }

    private static bool LinkMatches(byte[]? recorded, byte[]? actual) =>
        recorded is null ? actual is null : actual is not null && recorded.AsSpan().SequenceEqual(actual);

    private static AuditRecordData ToData(AuditRow row) => new(
        row.Id,
        row.OrganizacaoId,
        row.UnidadeId,
        row.UsuarioId,
        row.SessaoId,
        row.Acao,
        row.ObjetoTipo,
        row.ObjetoId,
        AuditHash.CanonicalJson(row.Antes),
        AuditHash.CanonicalJson(row.Depois),
        row.Resultado,
        row.CorrelationId,
        row.OcorridoEm,
        row.Sequencia,
        row.HashAnterior);
}
