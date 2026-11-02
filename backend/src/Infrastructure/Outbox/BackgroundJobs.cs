using System.Threading.Channels;
using Flow4Tech.Kernel;

namespace Flow4Tech.Infrastructure.Outbox;

/// <summary>
/// Work for the background worker. It carries the organization explicitly: the queue transports it, the handler restores it with
/// <c>TenantDatabase.BeginAsync(job.Organization)</c>. There is no ambient organization in background work.
/// </summary>
public sealed record Job(OrganizationContext Organization, string Type, string Payload);

public delegate Task JobHandler(Job job, CancellationToken cancellationToken);

/// <summary>
/// Our own seam over the queue/worker technology (ADR-003: Wolverine, Hangfire or Quartz sit behind it; SQS or Kafka may replace it later).
/// Delivery is at least once: a handler may see the same job again and must be idempotent.
/// </summary>
public interface IBackgroundJobs
{
    Task EnqueueAsync(Job job, CancellationToken cancellationToken = default);

    /// <summary>Starts the worker (inside the API process, 04-arquitetura A5). Jobs are passed to <paramref name="handler"/>.</summary>
    Task StartAsync(JobHandler handler, CancellationToken cancellationToken = default);

    /// <summary>Stops taking new jobs and waits for the one in flight.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>In-memory implementation: used by tests and as the simplest worker. Not durable; the outbox table is what survives a restart.</summary>
public sealed class InProcessBackgroundJobs : IBackgroundJobs
{
    private readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>();
    private Task? _worker;
    private CancellationTokenSource? _stop;

    public async Task EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _channel.Writer.WriteAsync(job, cancellationToken);
    }

    public Task StartAsync(JobHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stop.Token;
        _worker = Task.Run(
            async () =>
            {
                await foreach (var job in _channel.Reader.ReadAllAsync(token))
                {
                    await handler(job, token);
                }
            },
            token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryComplete();
        if (_worker is not null)
        {
            await _worker.WaitAsync(cancellationToken);
        }

        _stop?.Dispose();
    }
}
