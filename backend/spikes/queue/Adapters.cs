using System.Collections.Concurrent;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;

namespace Flow4Tech.Spikes.Queue;

/// <summary>Each library behind OUR interface (IBackgroundJobs). The Job carries the organization; the handler restores it.</summary>
public static class Handlers
{
    // Shared by the adapters' DI-less glue (spike only).
    public static JobHandler? Current { get; set; }
}

// ---------------------------------------------------------------- Wolverine
public sealed record WolverineJob(Guid OrganizationId, Guid UnitId, string Type, string Payload);

public static class WolverineJobHandler
{
    public static Task Handle(WolverineJob message, CancellationToken ct) =>
        Handlers.Current!(new Job(new OrganizationContext(message.OrganizationId, message.UnitId), message.Type, message.Payload), ct);
}

public sealed class WolverineJobs(string ownerConnectionString) : IBackgroundJobs
{
    private IHost? _host;

    public async Task StartAsync(JobHandler handler, CancellationToken cancellationToken = default)
    {
        Handlers.Current = handler;
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(o =>
            {
                o.Discovery.DisableConventionalDiscovery().IncludeType(typeof(WolverineJobHandler));
                o.PersistMessagesWithPostgresql(ownerConnectionString, "wolverine");
                o.Policies.UseDurableLocalQueues();
                o.Policies.OnException<InvalidOperationException>().RetryTimes(3);
            })
            .StartAsync(cancellationToken);
    }

    public async Task EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        using var scope = _host!.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(new WolverineJob(job.Organization.OrganizationId, job.Organization.UnitId, job.Type, job.Payload));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_host is not null)
        {
            await _host.StopAsync(cancellationToken);
            _host.Dispose();
        }
    }
}

// ---------------------------------------------------------------- Hangfire
public sealed class HangfireRunner
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [0])]
    public Task RunAsync(Guid organizationId, Guid unitId, string type, string payload) =>
        Handlers.Current!(new Job(new OrganizationContext(organizationId, unitId), type, payload), CancellationToken.None);
}

public sealed class HangfireJobs(string ownerConnectionString) : IBackgroundJobs
{
    private BackgroundJobServer? _server;
    private BackgroundJobClient? _client;

    public Task StartAsync(JobHandler handler, CancellationToken cancellationToken = default)
    {
        Handlers.Current = handler;
        var storage = new PostgreSqlStorage(
            new NpgsqlConnectionFactory(ownerConnectionString, new PostgreSqlStorageOptions { QueuePollInterval = TimeSpan.FromMilliseconds(100) }),
            new PostgreSqlStorageOptions { QueuePollInterval = TimeSpan.FromMilliseconds(100) });
        _client = new BackgroundJobClient(storage);
        _server = new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 4, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }, storage);
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        _client!.Enqueue<HangfireRunner>(r => r.RunAsync(job.Organization.OrganizationId, job.Organization.UnitId, job.Type, job.Payload));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            _server.SendStop();
            await _server.WaitForShutdownAsync(cancellationToken);
            _server.Dispose();
        }
    }
}

// ---------------------------------------------------------------- Quartz (RAMJobStore: its durable ADO store needs its own DDL, see README)
public sealed class QuartzRunner : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var d = context.MergedJobDataMap;
        var job = new Job(new OrganizationContext(Guid.Parse(d.GetString("org")!), Guid.Parse(d.GetString("unit")!)), d.GetString("type")!, d.GetString("payload")!);
        try
        {
            await Handlers.Current!(job, cancellationToken);
        }
        catch (InvalidOperationException ex) when (context.RefireCount < 3)
        {
            // Quartz has no retry policy: the job itself must ask to be refired.
            throw new JobExecutionException(ex) { RefireImmediately = true };
        }
    }
}

public sealed class QuartzJobs : IBackgroundJobs
{
    private IScheduler? _scheduler;

    public async Task StartAsync(JobHandler handler, CancellationToken cancellationToken = default)
    {
        Handlers.Current = handler;
        _scheduler = await QuartzSchedulerBuilder.Create(_ => { }).BuildScheduler(cancellationToken);
        await _scheduler.Start(cancellationToken);
    }

    public async Task EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        var detail = JobBuilder.Create<QuartzRunner>()
            .UsingJobData("org", job.Organization.OrganizationId.ToString())
            .UsingJobData("unit", job.Organization.UnitId.ToString())
            .UsingJobData("type", job.Type)
            .UsingJobData("payload", job.Payload)
            .Build();
        await _scheduler!.ScheduleJob(detail, TriggerBuilder.Create().StartNow().Build());
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_scheduler is not null)
        {
            await _scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
        }
    }
}
