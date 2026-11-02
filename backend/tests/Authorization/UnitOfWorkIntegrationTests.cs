using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Flow4Tech.Permissoes.Infrastructure;
using Npgsql;
using static Flow4Tech.Authorization.Tests.Fixtures;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Authorization.Tests;

internal sealed record CortesiaConcedida(Guid EventId, Guid ItemId) : IDomainEvent
{
    public string EventType => "cortesia.concedida";
}

/// <summary>
/// ADR-006 owner decision C: the cumulative-limit reservation, the business change and the outbox event share ONE transaction through
/// <see cref="IUnitOfWork"/>. Real PostgreSQL, RLS, application credential. The "business operation" below is what an Application
/// handler does: decide, then change state, then publish.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class UnitOfWorkIntegrationTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Today = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Today.UtcDateTime);

    private sealed class Env : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _source;

        public Env(NpgsqlDataSource source)
        {
            _source = source;
            Tenant = new TenantDatabase(source);
        }

        public TenantDatabase Tenant { get; }

        public TenantUnitOfWork Uow => new(Tenant);

        public static IAuthorizationEngine Engine => CumulativeLimitConcurrencyIntegrationTests.EngineOver(new PostgresCumulativeLimitStore(), Today);

        public OutboxWriter Outbox { get; } = new(new FixedClock(Today));

        public ValueTask DisposeAsync() => _source.DisposeAsync();
    }

    private async Task<Env> ArrangeAsync()
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        return new Env(NpgsqlDataSource.Create(db.AppConnectionString));
    }

    public enum Fault
    {
        None,
        AfterAllow,
        AfterStateChange,
        AfterOutboxWrite,
    }

    /// <summary>The business operation as a handler writes it: decide, change state, publish, all inside the unit of work.</summary>
    private static async Task<Decision> ConcederAsync(Env env, Guid user, long cents, Fault fault)
    {
        var org = Ctx(OrgA);
        return await env.Uow.ExecuteAsync(org, async ct =>
        {
            var decision = await Env.Engine.DecidirAsync(new Subject(user, [Garcom]), Cortesia, new Scope(OrgA, UnitA), new DecisionContext(new Money(cents, "BRL")), ct);
            if (!decision.IsAllowed)
            {
                return decision;
            }

            if (fault == Fault.AfterAllow)
            {
                throw new InvalidOperationException("business failure after Allow");
            }

            var tx = TenantUnitOfWork.Require(OrgA);
            var itemId = EntityId.New().Value;
            await Exec(tx, "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@i, @o, @u, 'cortesia', @v)", ("i", itemId), ("o", OrgA), ("u", UnitA), ("v", cents));
            if (fault == Fault.AfterStateChange)
            {
                throw new InvalidOperationException("business failure after the state change");
            }

            await env.Outbox.AddAsync(tx, org, new CortesiaConcedida(EntityId.New().Value, itemId), ct);
            if (fault == Fault.AfterOutboxWrite)
            {
                throw new InvalidOperationException("business failure after the outbox write");
            }

            return decision;
        });
    }

    private static async Task<(long Counter, long Items, long Events)> StateAsync(Env env, Guid user)
    {
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        async Task<long> Scalar(string sql, params (string, object)[] args)
        {
            await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
            foreach (var (n, v) in args)
            {
                command.Parameters.AddWithValue(n, v);
            }

            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return (
            await Scalar("SELECT COALESCE(sum(consumido_centavos), 0) FROM limites_consumo WHERE usuario_id = @u AND dia = @d", ("u", user), ("d", Day)),
            await Scalar("SELECT count(*) FROM amostra_itens WHERE nome = 'cortesia'"),
            await Scalar("SELECT count(*) FROM outbox_eventos"));
    }

    [Fact]
    public async Task A_business_failure_after_Allow_rolls_the_reservation_back_and_a_retry_does_not_double_count()
    {
        await using var env = await ArrangeAsync();
        var user = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConcederAsync(env, user, 1_000, Fault.AfterAllow));

        Assert.Equal((0, 0, 0), await StateAsync(env, user));

        // The whole cap is still available: the failed attempt left no trace.
        var retry = await ConcederAsync(env, user, 1_000, Fault.None);
        Assert.True(retry.IsAllowed);
        Assert.Equal((1_000, 1, 1), await StateAsync(env, user));
    }

    [Fact]
    public async Task A_successful_operation_persists_reservation_state_change_and_outbox_event_together()
    {
        await using var env = await ArrangeAsync();
        var user = Guid.NewGuid();

        var decision = await ConcederAsync(env, user, 400, Fault.None);

        Assert.True(decision.IsAllowed);
        Assert.Equal((400, 1, 1), await StateAsync(env, user));
    }

    [Theory]
    [InlineData(Fault.AfterStateChange)]
    [InlineData(Fault.AfterOutboxWrite)]
    public async Task A_failure_after_the_state_change_or_the_outbox_write_undoes_all_three(Fault fault)
    {
        await using var env = await ArrangeAsync();
        var user = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConcederAsync(env, user, 300, fault));

        Assert.Equal((0, 0, 0), await StateAsync(env, user));
    }

    [Fact]
    public async Task A_denial_by_the_cap_inside_the_unit_of_work_changes_nothing_and_keeps_earlier_consumption()
    {
        await using var env = await ArrangeAsync();
        var user = Guid.NewGuid();
        await ConcederAsync(env, user, 900, Fault.None);

        var denied = await ConcederAsync(env, user, 200, Fault.None);

        Assert.False(denied.IsAllowed);
        Assert.Equal("daily-cap-exceeded", denied.Reason);
        Assert.Equal((900, 1, 1), await StateAsync(env, user));
    }

    [Fact]
    public async Task The_limit_store_refuses_to_reserve_outside_a_unit_of_work()
    {
        await using var env = await ArrangeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Env.Engine.DecidirAsync(new Subject(Guid.NewGuid(), [Garcom]), Cortesia, new Scope(OrgA, UnitA), new DecisionContext(new Money(100, "BRL"))));
    }

    [Fact]
    public async Task A_nested_unit_of_work_for_the_same_organization_joins_and_another_organization_is_refused()
    {
        await using var env = await ArrangeAsync();
        var user = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Uow.ExecuteAsync(Ctx(OrgA), async ct =>
        {
            await env.Uow.ExecuteAsync(Ctx(OrgA), async inner =>
            {
                var d = await Env.Engine.DecidirAsync(new Subject(user, [Garcom]), Cortesia, new Scope(OrgA, UnitA), new DecisionContext(new Money(100, "BRL")), inner);
                Assert.True(d.IsAllowed);
            }, ct);
            throw new InvalidOperationException("outer fails after the inner scope finished");
        }));

        Assert.Equal((0, 0, 0), await StateAsync(env, user)); // the inner scope joined the outer transaction, so the outer rollback undid it

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Uow.ExecuteAsync(Ctx(OrgA), ct =>
            env.Uow.ExecuteAsync(Ctx(OrgB), _ => Task.CompletedTask, ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_task_that_outlives_its_unit_of_work_cannot_obtain_the_closed_transaction(bool fails)
    {
        await using var env = await ArrangeAsync();
        var gate = new TaskCompletionSource();
        Task<Exception?>? straggler = null;

        try
        {
            await env.Uow.ExecuteAsync(Ctx(OrgA), _ =>
            {
                straggler = Task.Run<Exception?>(async () =>
                {
                    await gate.Task;
                    return Record.Exception(() => TenantUnitOfWork.Require(OrgA));
                });
                return fails ? throw new InvalidOperationException("boom") : Task.CompletedTask;
            });
        }
        catch (InvalidOperationException) when (fails)
        {
        }

        gate.SetResult();
        Assert.IsType<InvalidOperationException>(await straggler!);
    }

    [Fact]
    public async Task A_nested_failure_swallowed_by_the_outer_code_still_fails_the_whole_unit_of_work()
    {
        await using var env = await ArrangeAsync();
        var user = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Uow.ExecuteAsync(Ctx(OrgA), async ct =>
        {
            try
            {
                await env.Uow.ExecuteAsync(Ctx(OrgA), _ => throw new InvalidOperationException("inner failed"), ct);
            }
            catch (InvalidOperationException)
            {
                // swallowed: the outer code carries on and would otherwise commit a half-done operation
            }

            await Env.Engine.DecidirAsync(new Subject(user, [Garcom]), Cortesia, new Scope(OrgA, UnitA), new DecisionContext(new Money(100, "BRL")), ct);
        }));

        Assert.Equal((0, 0, 0), await StateAsync(env, user));
    }

    [Fact]
    public async Task Concurrent_units_of_work_of_different_organizations_never_see_each_others_transaction()
    {
        await using var env = await ArrangeAsync();
        using var bothInside = new Barrier(2);

        async Task<(bool Own, bool Other, TenantTransaction Tx)> Flow(Guid own, Guid other) =>
            await env.Uow.ExecuteAsync(Ctx(own), async _ =>
            {
                await Task.Yield();
                bothInside.SignalAndWait(TimeSpan.FromSeconds(10), _); // both scopes are open at the same time
                var tx = TenantUnitOfWork.Require(own);
                return (true, Record.Exception(() => TenantUnitOfWork.Require(other)) is InvalidOperationException, tx);
            });

        var results = await Task.WhenAll(Task.Run(() => Flow(OrgA, OrgB)), Task.Run(() => Flow(OrgB, OrgA)));

        Assert.All(results, r => Assert.True(r.Own && r.Other));
        Assert.NotSame(results[0].Tx, results[1].Tx);
        Assert.Throws<InvalidOperationException>(() => TenantUnitOfWork.Require(OrgA)); // nothing leaked into the caller
    }

    [Fact]
    public async Task Outside_a_unit_of_work_there_is_no_current_transaction()
    {
        await using var env = await ArrangeAsync();

        Assert.Throws<InvalidOperationException>(() => TenantUnitOfWork.Require(OrgA));
    }
}
