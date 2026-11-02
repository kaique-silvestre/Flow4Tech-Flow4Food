using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Outbox.Tests;

/// <summary>Criteria 4 and 5: a background job carries the organization and restores it before touching data; the queue is our interface.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class BackgroundJobsIntegrationTests(PostgresFixture postgres)
{
    private static async Task<List<string>> VisibleItemsAsync(TenantDatabase tenant, OrganizationContext org)
    {
        await using var tx = await tenant.BeginAsync(org);
        return await Names(tx, "amostra_itens");
    }

    [Fact]
    public async Task A_job_handler_that_restores_the_context_sees_only_that_organization_and_the_next_job_does_not_inherit_it()
    {
        await using var env = await Env.CreateAsync(postgres);
        var jobs = new InProcessBackgroundJobs();
        var seen = new Dictionary<Guid, List<string>>();
        await jobs.StartAsync(async (job, _) => seen[job.Organization.OrganizationId] = await VisibleItemsAsync(env.Tenant, job.Organization));

        await jobs.EnqueueAsync(new Job(Ctx(OrgA), "x", ""));
        await jobs.EnqueueAsync(new Job(Ctx(OrgB), "x", ""));
        await jobs.StopAsync();

        Assert.Equal(["A-Chopp"], seen[OrgA]);
        Assert.Equal(["B-Chopp"], seen[OrgB]);
    }

    [Fact]
    public async Task A_job_handler_that_forgets_the_context_gets_zero_rows_and_refused_writes()
    {
        await using var env = await Env.CreateAsync(postgres);
        var jobs = new InProcessBackgroundJobs();
        long? rows = null;
        string? sqlState = null;
        await jobs.StartAsync(async (_, ct) =>
        {
            // The mistake the rule forbids: a raw connection from the pool with no BeginAsync.
            await using var connection = await env.App.OpenConnectionAsync(ct);
            await using var read = new NpgsqlCommand("SELECT count(*) FROM amostra_itens", connection);
            rows = (long)(await read.ExecuteScalarAsync(ct))!;
            await using var write = new NpgsqlCommand("INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@i, @o, @u, 'x', 1)", connection);
            write.Parameters.AddWithValue("i", Guid.NewGuid());
            write.Parameters.AddWithValue("o", OrgA);
            write.Parameters.AddWithValue("u", UnitA);
            try
            {
                await write.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex)
            {
                sqlState = ex.SqlState;
            }
        });

        await jobs.EnqueueAsync(new Job(Ctx(OrgA), "x", ""));
        await jobs.StopAsync();

        Assert.Equal(0L, rows);
        Assert.Equal("42501", sqlState);
    }

    [Fact]
    public async Task Writing_an_event_then_enqueueing_a_dispatch_job_delivers_it_end_to_end_with_the_jobs_organization()
    {
        await using var env = await Env.CreateAsync(postgres);
        await env.CreateItemAsync(Ctx(OrgA), "a1");
        await env.CreateItemAsync(Ctx(OrgB), "b1");
        var consumer = new RecordingConsumer();
        var dispatcher = new OutboxDispatcher(env.Tenant, [consumer], env.Clock);
        var jobs = new InProcessBackgroundJobs();
        await jobs.StartAsync(dispatcher.HandleJobAsync);

        await jobs.EnqueueAsync(new Job(Ctx(OrgA), OutboxDispatcher.JobType, ""));
        await jobs.StopAsync();

        Assert.Single(consumer.Deliveries);
        Assert.Equal(0, await env.PendingEventsAsync(Ctx(OrgA)));
        Assert.Equal(1, await env.PendingEventsAsync(Ctx(OrgB)));
    }
}
