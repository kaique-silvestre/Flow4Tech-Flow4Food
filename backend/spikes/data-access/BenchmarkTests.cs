using System.Diagnostics;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace DataAccess.Spike;

/// <summary>
/// Opt-in micro-benchmark (SPIKE_BENCH=1, SPIKE_IMPL=raw|dapper|ef, optional SPIKE_BENCH_OUT=file). One implementation per process so
/// cold-start numbers are not warmed by the others. Real PostgreSQL 17 in Docker, app credential, RLS forced. Not a statistical benchmark.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class BenchmarkTests(PostgresFixture postgres)
{
    private const int Warmup = 200;
    private const int Iterations = 1500;
    private const int Rows = 100;
    private const int Workers = 16;
    private const int ParallelOpsPerWorker = 150;

    [Fact]
    public async Task Benchmark()
    {
        if (Environment.GetEnvironmentVariable("SPIKE_BENCH") != "1")
        {
            return;
        }

        var impl = Environment.GetEnvironmentVariable("SPIKE_IMPL") ?? "dapper";
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        await using var source = NpgsqlDataSource.Create(db.AppConnectionString + ";Maximum Pool Size=20;Minimum Pool Size=20");
        var tenant = new TenantDatabase(source);
        var store = Stores.Create(impl, tenant);
        var raw = new RawStore(tenant);
        foreach (var org in new[] { OrgA, OrgB })
        {
            var unit = org == OrgA ? UnitA : UnitB;
            for (var i = 0; i < Rows - 1; i++) // + the seeded row = 100 per tenant; the insert benchmark only grows tenant A
            {
                await raw.AddAsync(Ctx(org), new Item(Guid.NewGuid(), org, unit, $"seed-{i:D3}", i));
            }
        }

        var lines = new List<string>();
        var cold = Stopwatch.StartNew();
        await store.ListAsync(Ctx(OrgB));
        lines.Add($"cold first list (ms) {cold.Elapsed.TotalMilliseconds:F1}");
        cold.Restart();
        await store.AddAsync(Ctx(OrgA), NewItem());
        lines.Add($"cold first insert (ms) {cold.Elapsed.TotalMilliseconds:F1}");

        lines.Add(await Measure("insert 1 row (begin+set_config+insert+commit)", () => store.AddAsync(Ctx(OrgA), NewItem())));
        lines.Add(await Measure($"list {Rows} rows, tenant B (begin+set_config+select+commit)", () => store.ListAsync(Ctx(OrgB))));

        if (impl == "raw")
        {
            lines.Add(await Measure("baseline: begin+SELECT 1+commit, NO set_config", async () =>
            {
                await using var c = await source.OpenConnectionAsync();
                await using var t = await c.BeginTransactionAsync();
                await using var cmd = new NpgsqlCommand("SELECT 1", c, t);
                await cmd.ExecuteScalarAsync();
                await t.CommitAsync();
            }));
            lines.Add(await Measure("with context: BeginAsync(set_config)+SELECT 1+commit", async () =>
            {
                await using var tx = await tenant.BeginAsync(Ctx(OrgA));
                await using var cmd = new NpgsqlCommand("SELECT 1", tx.Connection, tx.Transaction);
                await cmd.ExecuteScalarAsync();
                await tx.CommitAsync();
            }));
        }

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(async _ =>
        {
            for (var i = 0; i < ParallelOpsPerWorker; i++)
            {
                await store.ListAsync(Ctx(OrgB));
            }
        }));
        lines.Add($"parallel list (100 rows) x{Workers} workers: {Workers * ParallelOpsPerWorker / sw.Elapsed.TotalSeconds:F0} ops/s");

        var text = $"== {impl} ==" + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine;
        Console.WriteLine(text);
        var outFile = Environment.GetEnvironmentVariable("SPIKE_BENCH_OUT");
        if (outFile is not null)
        {
            await File.AppendAllTextAsync(outFile, text);
        }
    }

    private static Item NewItem() => new(Guid.NewGuid(), OrgA, UnitA, "bench", 1);

    private static async Task<string> Measure(string name, Func<Task> op)
    {
        for (var i = 0; i < Warmup; i++)
        {
            await op();
        }

        var times = new double[Iterations];
        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < Iterations; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await op();
            times[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }

        var alloc = (GC.GetTotalAllocatedBytes(precise: true) - allocBefore) / Iterations;
        Array.Sort(times);
        return $"{name}: median {times[Iterations / 2]:F2} ms, p95 {times[(int)(Iterations * 0.95)]:F2} ms, mean {times.Average():F2} ms, alloc {alloc / 1024.0:F1} KiB/op";
    }
}
