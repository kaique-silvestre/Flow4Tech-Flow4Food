using System.Collections.Concurrent;
using System.Diagnostics;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Spikes.Queue;

/// <summary>
/// Same scenarios for each candidate behind IBackgroundJobs, against real PostgreSQL (RLS, app credential):
/// 1) org context restored by the handler (A and B jobs, each sees only its own rows), 2) a handler failure is retried (at least once),
/// 3) pickup latency (paced, one job at a time) and burst throughput. Numbers go to SPIKE_QUEUE_OUT when set.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class QueueComparisonTests(PostgresFixture postgres)
{
    public static TheoryData<string> Candidates => ["wolverine", "hangfire", "quartz"];

    private static IBackgroundJobs Create(string name, string ownerConnection) => name switch
    {
        "wolverine" => new WolverineJobs(ownerConnection),
        "hangfire" => new HangfireJobs(ownerConnection),
        _ => new QuartzJobs(),
    };

    private sealed record Arrange(IBackgroundJobs Jobs, TenantDatabase Tenant, NpgsqlDataSource App, TestDatabase Db);

    private async Task<Arrange> StartAsync(string name, JobHandler handler)
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        var app = NpgsqlDataSource.Create(db.AppConnectionString);
        var jobs = Create(name, db.OwnerConnectionString);
        await jobs.StartAsync(handler);
        return new Arrange(jobs, new TenantDatabase(app), app, db);
    }

    private static async Task InsertAsync(TenantDatabase tenant, OrganizationContext org, string nome)
    {
        await using var tx = await tenant.BeginAsync(org);
        await Exec(tx, "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@i, @o, @u, @n, 1)", ("i", Guid.NewGuid()), ("o", org.OrganizationId), ("u", org.UnitId), ("n", nome));
        await tx.CommitAsync();
    }

    private static async Task<List<string>> VisibleAsync(TenantDatabase tenant, OrganizationContext org)
    {
        await using var tx = await tenant.BeginAsync(org);
        return await Names(tx, "amostra_itens");
    }

    private static async Task WaitAsync(Func<bool> done, int seconds = 30)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed.TotalSeconds > seconds)
            {
                throw new TimeoutException("jobs not processed in time");
            }

            await Task.Delay(10);
        }
    }

    private static void Record(string line)
    {
        Console.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("SPIKE_QUEUE_OUT");
        if (path is not null)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Handler_restores_the_organization_and_sees_only_its_own_rows(string name)
    {
        TenantDatabase? tenant = null;
        var done = 0;
        var a = Ctx(OrgA);
        var b = Ctx(OrgB);
        var ctx = await StartAsync(name, async (job, _) =>
        {
            await InsertAsync(tenant!, job.Organization, "job-" + job.Payload);
            Interlocked.Increment(ref done);
        });
        tenant = ctx.Tenant;
        for (var i = 0; i < 5; i++)
        {
            await ctx.Jobs.EnqueueAsync(new Job(a, "t", "a" + i));
            await ctx.Jobs.EnqueueAsync(new Job(b, "t", "b" + i));
        }

        await WaitAsync(() => Volatile.Read(ref done) == 10);
        await ctx.Jobs.StopAsync();

        var seenA = await VisibleAsync(ctx.Tenant, a);
        var seenB = await VisibleAsync(ctx.Tenant, b);
        Assert.Equal(5, seenA.Count(n => n.StartsWith("job-a", StringComparison.Ordinal)));
        Assert.DoesNotContain(seenA, n => n.StartsWith("job-b", StringComparison.Ordinal) || n.StartsWith("B-", StringComparison.Ordinal));
        Assert.Equal(5, seenB.Count(n => n.StartsWith("job-b", StringComparison.Ordinal)));
        await ctx.App.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task A_failing_handler_is_retried(string name)
    {
        var attempts = new ConcurrentDictionary<string, int>();
        var ctx = await StartAsync(name, (job, _) =>
        {
            var n = attempts.AddOrUpdate(job.Payload, 1, (_, c) => c + 1);
            return n == 1 ? throw new InvalidOperationException("first attempt fails") : Task.CompletedTask;
        });

        await ctx.Jobs.EnqueueAsync(new Job(Ctx(OrgA), "t", "retry-me"));
        await WaitAsync(() => attempts.GetValueOrDefault("retry-me") >= 2);
        await ctx.Jobs.StopAsync();

        Assert.True(attempts["retry-me"] >= 2);
        await ctx.App.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public async Task Pickup_latency_and_burst_throughput(string name)
    {
        TenantDatabase? tenant = null;
        var handled = new ConcurrentDictionary<string, long>();
        var ctx = await StartAsync(name, async (job, _) =>
        {
            await InsertAsync(tenant!, job.Organization, "m");
            handled[job.Payload] = Stopwatch.GetTimestamp();
        });
        tenant = ctx.Tenant;
        var org = Ctx(OrgA);

        // warm-up (first call pays JIT/model/connection costs)
        for (var i = 0; i < 10; i++)
        {
            await ctx.Jobs.EnqueueAsync(new Job(org, "t", "w" + i));
            await WaitAsync(() => handled.ContainsKey("w" + i));
        }

        var latencies = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await ctx.Jobs.EnqueueAsync(new Job(org, "t", "p" + i));
            await WaitAsync(() => handled.ContainsKey("p" + i));
            latencies.Add(Stopwatch.GetElapsedTime(start, handled["p" + i]).TotalMilliseconds);
        }

        latencies.Sort();
        var burstStart = Stopwatch.GetTimestamp();
        for (var i = 0; i < 300; i++)
        {
            await ctx.Jobs.EnqueueAsync(new Job(org, "t", "b" + i));
        }

        await WaitAsync(() => handled.ContainsKey("b299") && Enumerable.Range(0, 300).All(i => handled.ContainsKey("b" + i)), 120);
        var burst = Stopwatch.GetElapsedTime(burstStart).TotalSeconds;
        await ctx.Jobs.StopAsync();

        Record($"| {name} | {latencies[50]:F1} ms | {latencies[94]:F1} ms | {300 / burst:F0} jobs/s |");
        await ctx.App.DisposeAsync();
    }
}
