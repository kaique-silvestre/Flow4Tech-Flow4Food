using System.Diagnostics;
using System.Runtime.InteropServices;
using Flow4Tech.Authorization.Tests;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Infrastructure;
using Npgsql;
using static Flow4Tech.Authorization.Tests.Fixtures;

namespace Authz.Spike;

/// <summary>Opt-in (SPIKE_BENCH=1): decision latency per engine, in-process, plus the PostgreSQL-backed cumulative case. Output: SPIKE_BENCH_OUT or stdout.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class DecisionBenchmark(PostgresFixture postgres)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SPIKE_BENCH") == "1";

    private static double[] Measure(Func<Task<Decision>> decide, int warmup, int samples)
    {
        for (var i = 0; i < warmup; i++)
        {
            decide().GetAwaiter().GetResult();
        }

        var ticks = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var t = Stopwatch.GetTimestamp();
            decide().GetAwaiter().GetResult();
            ticks[i] = (Stopwatch.GetTimestamp() - t) * 1_000_000_000.0 / Stopwatch.Frequency;
        }

        Array.Sort(ticks);
        return ticks;
    }

    private static string Row(string label, double[] ns) =>
        $"{label,-46} mean {ns.Average() / 1000,9:F2} us   p50 {ns[ns.Length / 2] / 1000,9:F2} us   p99 {ns[(int)(ns.Length * 0.99)] / 1000,9:F2} us   (~{1e9 / ns.Average(),10:N0} decisions/s/thread)";

    [Fact]
    public async Task Benchmark()
    {
        if (!Enabled)
        {
            return;
        }

        var lines = new List<string>
        {
            $"hardware: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical cores; {RuntimeInformation.FrameworkDescription}; Release={!Debugger.IsAttached}",
            "method: single thread, 2k warmup then 20k timed decisions each, Stopwatch per call, in-memory (no I/O) store, standard 10-rule policy",
        };
        var store = new InMemoryCumulativeLimitStore();
        var engines = new (string, IAuthorizationEngine)[]
        {
            ("rules-as-data (custom)", new RulesAuthorizationEngine(Standard(), store, new FixedClock(Now))),
            ("casbin.net 2.21.3", new CasbinAuthorizationEngine(Standard(), store, new FixedClock(Now))),
            ("cedar (MonoCloud.Cedar 0.1.0, native)", new CedarAuthorizationEngine(Standard(), store, new FixedClock(Now))),
        };
        foreach (var (name, engine) in Environment.GetEnvironmentVariable("SPIKE_BENCH_PG_ONLY") == "1" ? [] : engines)
        {
            var cases = new (string, Func<Task<Decision>>)[]
            {
                ("case1 discount allow", () => engine.DecidirAsync(Who(Garcom), Desconto, At(Unit1), Ctx(percent: 5m))),
                ("case1 discount deny (over limit)", () => engine.DecidirAsync(Who(Garcom), Desconto, At(Unit1), Ctx(percent: 50m))),
                ("case3 unit allow/deny mix", () => engine.DecidirAsync(Who(Gerente), AjustarEstoque, At(Unit2), Ctx())),
                ("explicit deny, 3 profiles", () => engine.DecidirAsync(Who(Garcom, Gerente, Bloqueado), Relatorio, At(Unit1), Ctx())),
            };
            foreach (var (label, decide) in cases)
            {
                lines.Add(Row($"{name} / {label}", Measure(decide, 2_000, 20_000)));
            }
        }

        var path = Environment.GetEnvironmentVariable("SPIKE_BENCH_OUT");
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines));
        }

        try
        {
            lines.Add(await PostgresCumulativeAsync());
        }
        catch (Exception e)
        {
            lines.Add("postgres part failed: " + e);
        }

        var text = string.Join(Environment.NewLine, lines);
        if (path is null)
        {
            Console.WriteLine(text);
        }
        else
        {
            await File.WriteAllTextAsync(path, text);
        }
    }

    private async Task<string> PostgresCumulativeAsync()
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        await using var source = NpgsqlDataSource.Create(db.AppConnectionString + ";Maximum Pool Size=40");
        var tenant = new TenantDatabase(source);
        var engine = new RulesAuthorizationEngine(
            new Flow4Tech.Permissoes.Domain.PolicySet(Catalog, [new Flow4Tech.Permissoes.Domain.Rule(Flow4Tech.Permissoes.Domain.RuleEffect.Allow, Garcom, Cortesia, DailyCapCents: long.MaxValue / 2)]),
            new PostgresCumulativeLimitStore(tenant), new FixedClock(Now));
        var user = Guid.NewGuid();
        Task<Decision> Ask() => engine.DecidirAsync(new Subject(user, [Garcom]), Cortesia, new Scope(TestDatabase.OrgA, TestDatabase.UnitA), new DecisionContext(new Money(1, "BRL")));

        var sequential = Measure(Ask, 200, 2_000);
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 2_000).Select(_ => Task.Run(Ask)));
        sw.Stop();
        var hot = Stopwatch.StartNew(); // same counter row from 50 concurrent callers
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                await Ask();
            }
        })));
        hot.Stop();
        return Row("rules + PostgreSQL cap (sequential, 1 tx each)", sequential) + Environment.NewLine +
            $"rules + PostgreSQL cap, 2000 concurrent (pool 40)    total {sw.ElapsedMilliseconds} ms = {2000 * 1000 / Math.Max(1, sw.ElapsedMilliseconds)} decisions/s" + Environment.NewLine +
            $"rules + PostgreSQL cap, same row, 50x40 concurrent    total {hot.ElapsedMilliseconds} ms = {2000 * 1000 / Math.Max(1, hot.ElapsedMilliseconds)} decisions/s";
    }
}
