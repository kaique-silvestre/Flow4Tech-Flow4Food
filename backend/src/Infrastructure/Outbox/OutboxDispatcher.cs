using System.Diagnostics.CodeAnalysis;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Infrastructure.Outbox;

public sealed record OutboxOptions
{
    /// <summary>After this many failed deliveries an event is parked (<c>falhou_em</c>) instead of retried forever.</summary>
    public int MaxAttempts { get; init; } = 5;
}

/// <summary>
/// Delivers pending events of ONE organization at a time, at least once. Per event: open <see cref="TenantDatabase.BeginAsync"/> with the
/// event's organization (there is no ambient context in background work), lock the oldest pending row (<c>FOR UPDATE SKIP LOCKED</c>, so
/// concurrent dispatchers never take the same row), run the consumers inside that transaction, mark it processed, commit. Any failure rolls
/// the whole transaction back: the event stays pending and is redelivered, never lost; consumers see a repeat and must be idempotent.
/// No global ordering: concurrent dispatchers and retries can reorder events.
/// </summary>
public sealed class OutboxDispatcher(TenantDatabase tenant, IEnumerable<IOutboxConsumer> consumers, IClock clock, OutboxOptions? options = null)
{
    /// <summary>Job type for <see cref="IBackgroundJobs"/>: "dispatch the pending events of the job's organization".</summary>
    public const string JobType = "outbox.dispatch";

    private readonly IOutboxConsumer[] _consumers = [.. consumers];
    private readonly OutboxOptions _options = options ?? new OutboxOptions();

    /// <summary>Test seam: runs after all consumers delivered and before the processed mark (simulates a crash in between).</summary>
    internal Func<OutboxMessage, Task>? BeforeMark { get; set; }

    /// <summary>The <see cref="JobHandler"/> for <see cref="JobType"/>: the job carries the organization, the handler restores it.</summary>
    public async Task HandleJobAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Type == JobType)
        {
            await DispatchAsync(job.Organization, cancellationToken);
        }
    }

    /// <summary>Delivers everything pending for the organization; returns how many events were delivered.</summary>
    public async Task<int> DispatchAsync(OrganizationContext organization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        var delivered = 0;
        var failedThisRun = new List<Guid>();
        while (true)
        {
            var step = await DeliverNextAsync(organization, [.. failedThisRun], cancellationToken);
            if (step.Message is null)
            {
                return delivered;
            }

            if (step.Failure is null)
            {
                delivered++;
                continue;
            }

            failedThisRun.Add(step.Message.Id);
            await RecordFailureAsync(organization, step.Message.Id, step.Failure, cancellationToken);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A consumer failure must not stop the dispatcher: the delivery transaction is rolled back, the failure is recorded and the event retried.")]
    private async Task<(OutboxMessage? Message, string? Failure)> DeliverNextAsync(OrganizationContext organization, Guid[] skip, CancellationToken cancellationToken)
    {
        OutboxMessage? message = null;
        try
        {
            await using var tx = await tenant.BeginAsync(organization, cancellationToken);
            await using var context = new OutboxContext(tx);
            var row = (await context.Eventos
                .FromSql($"SELECT * FROM outbox_eventos WHERE processado_em IS NULL AND falhou_em IS NULL AND id <> ALL({skip}) ORDER BY ocorrido_em, id LIMIT 1 FOR UPDATE SKIP LOCKED")
                .AsNoTracking()
                .ToListAsync(cancellationToken)).SingleOrDefault();
            if (row is null)
            {
                return (null, null);
            }

            message = new OutboxMessage(row.Id, organization, row.Tipo, row.Payload, row.OcorridoEm, row.Tentativas);
            foreach (var consumer in _consumers.Where(c => c.EventType == message.EventType))
            {
                await consumer.HandleAsync(message, tx, cancellationToken);
            }

            if (BeforeMark is not null)
            {
                await BeforeMark(message);
            }

            var now = clock.UtcNow;
            await context.Database.ExecuteSqlAsync($"UPDATE outbox_eventos SET processado_em = {now} WHERE id = {row.Id}", cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return (message, null);
        }
        catch (Exception ex) when (message is not null && ex is not OperationCanceledException)
        {
            return (message, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message);
        }
    }

    private async Task RecordFailureAsync(OrganizationContext organization, Guid id, string error, CancellationToken cancellationToken)
    {
        await using var tx = await tenant.BeginAsync(organization, cancellationToken);
        await using var context = new OutboxContext(tx);
        var now = clock.UtcNow;
        var max = _options.MaxAttempts;
        await context.Database.ExecuteSqlAsync(
            $"UPDATE outbox_eventos SET tentativas = tentativas + 1, ultimo_erro = {error}, falhou_em = CASE WHEN tentativas + 1 >= {max} THEN {now} ELSE NULL END WHERE id = {id}",
            cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
