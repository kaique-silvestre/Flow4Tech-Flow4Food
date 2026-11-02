using System.Diagnostics;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Outbox.Tests;

/// <summary>Runs only with OUTBOX_BENCH=1 (it is a measurement, not a gate). Result lines go to OUTBOX_BENCH_OUT when set.</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OUTBOX_BENCH") != "1")
        {
            Skip = "set OUTBOX_BENCH=1 to measure";
        }
    }
}

[Collection(PostgresDefinition.Name)]
public sealed class LatencyBenchmarkIntegrationTests(PostgresFixture postgres)
{
    private const int Warmup = 200;
    private const int Iterations = 1500;

    private static (double Median, double P95) Stats(List<double> ms)
    {
        ms.Sort();
        return (ms[ms.Count / 2], ms[(int)(ms.Count * 0.95)]);
    }

    [BenchFact]
    public async Task Typical_transaction_latency()
    {
        await using var env = await Env.CreateAsync(postgres);
        var org = Ctx(OrgA);

        async Task<double> TimeAsync(bool withOutbox)
        {
            var start = Stopwatch.GetTimestamp();
            await using var tx = await env.Tenant.BeginAsync(org); // begin + set_config
            await Exec(tx, "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@i, @o, @u, 'b', 1)", ("i", Guid.NewGuid()), ("o", OrgA), ("u", UnitA));
            if (withOutbox)
            {
                await env.Writer.AddAsync(tx, org, new ItemCriado(EntityId.New().Value, Guid.NewGuid(), "b"));
            }

            await tx.CommitAsync();
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var baseline = new List<double>();
        var typical = new List<double>();
        for (var i = 0; i < Warmup; i++)
        {
            await TimeAsync(false);
            await TimeAsync(true);
        }

        for (var i = 0; i < Iterations; i++)
        {
            baseline.Add(await TimeAsync(false));
            typical.Add(await TimeAsync(true));
        }

        // Dispatch cost per delivered event (claim + consumer-less delivery + mark + commit), pending backlog = what the loop above wrote.
        var dispatcher = new OutboxDispatcher(env.Tenant, [], env.Clock);
        var sw = Stopwatch.StartNew();
        var delivered = await dispatcher.DispatchAsync(org);
        var perEvent = sw.Elapsed.TotalMilliseconds / delivered;

        var b = Stats(baseline);
        var t = Stats(typical);
        Record($"| begin + set_config + insert + commit (no outbox) | {b.Median:F2} ms | {b.P95:F2} ms |");
        Record($"| begin + set_config + insert + outbox write + commit | {t.Median:F2} ms | {t.P95:F2} ms |");
        Record($"| dispatch one event (claim, mark, commit; no consumers), {delivered} events | {perEvent:F2} ms mean | |");
    }

    private static void Record(string line)
    {
        Console.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("OUTBOX_BENCH_OUT");
        if (path is not null)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
