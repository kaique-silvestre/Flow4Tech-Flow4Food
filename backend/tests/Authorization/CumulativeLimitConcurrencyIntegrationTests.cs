using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Flow4Tech.Permissoes.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Flow4Tech.Authorization.Tests.Fixtures;

namespace Flow4Tech.Authorization.Tests;

/// <summary>
/// Case 2 for real: the daily courtesy cap under concurrency, on PostgreSQL with RLS, through the application credential and TenantDatabase.
/// Callers (these tests) only see <see cref="IAuthorizationEngine"/>.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class CumulativeLimitConcurrencyIntegrationTests(PostgresFixture postgres)
{
    private const long Cap = 1_000;
    private static readonly DateTimeOffset Today = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Today.UtcDateTime);

    private async Task<(NpgsqlDataSource Source, TenantDatabase Tenant)> ArrangeAsync()
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        // The pool must be able to hold every concurrent request at once, otherwise they would queue and hide the race.
        var source = NpgsqlDataSource.Create(db.AppConnectionString + ";Maximum Pool Size=100");
        return (source, new TenantDatabase(source));
    }

    private static PolicySet Policy() =>
        new(Catalog, [new Rule(RuleEffect.Allow, Garcom, Cortesia, DailyCapCents: Cap)]);

    // Callers only know the interface; the concrete engine is chosen in this one place.
    public static IAuthorizationEngine EngineOver(ICumulativeLimitStore store, DateTimeOffset? now = null) =>
        new RulesAuthorizationEngine(Policy(), store, new FixedClock(now ?? Today));

    private static async Task<Decision[]> Fire(IAuthorizationEngine engine, Guid org, Guid user, params long[] amounts)
    {
        var start = new TaskCompletionSource();
        var tasks = amounts.Select(cents => Task.Run(async () =>
        {
            await start.Task;
            return await engine.DecidirAsync(
                new Subject(user, [Garcom]),
                Cortesia,
                new Scope(org, TestDatabase.UnitA),
                new DecisionContext(new Money(cents, "BRL")));
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<long> TotalAsync(TenantDatabase tenant, Guid org, Guid user, DateOnly day)
    {
        await using var tx = await tenant.BeginAsync(TestDatabase.Ctx(org));
        await using var context = new PermissoesDbContext(tx);
        return await context.Limites.AsNoTracking()
            .Where(l => l.UsuarioId == user && l.Permissao == Cortesia && l.Dia == day)
            .Select(l => l.ConsumidoCentavos)
            .SingleOrDefaultAsync();
    }

    [Fact]
    public async Task Forty_concurrent_requests_never_jointly_exceed_the_daily_cap()
    {
        var (source, tenant) = await ArrangeAsync();
        await using var _ = source;

        // 40 x R$ 1,00 against a R$ 10,00 cap, three fresh days so the "row does not exist yet" insert race is exercised each time.
        for (var round = 0; round < 3; round++)
        {
            var user = Guid.NewGuid();
            var at = Today.AddDays(round);
            var engine = EngineOver(new PostgresCumulativeLimitStore(tenant), at);
            var decisions = await Fire(engine, TestDatabase.OrgA, user, Enumerable.Repeat(100L, 40).ToArray());

            Assert.Equal(10, decisions.Count(d => d.IsAllowed));
            Assert.All(decisions.Where(d => !d.IsAllowed), d => Assert.Equal("daily-cap-exceeded", d.Reason));
            Assert.Equal(Cap, await TotalAsync(tenant, TestDatabase.OrgA, user, DateOnly.FromDateTime(at.UtcDateTime)));
        }
    }

    [Fact]
    public async Task Mixed_amounts_concurrently_keep_the_persisted_total_within_the_cap_and_equal_to_what_was_allowed()
    {
        var (source, tenant) = await ArrangeAsync();
        await using var _ = source;
        var engine = EngineOver(new PostgresCumulativeLimitStore(tenant));
        var user = Guid.NewGuid();
        long[] amounts = [.. Enumerable.Range(1, 40).Select(i => (long)(i * 17 % 350 + 1))];

        var decisions = await Fire(engine, TestDatabase.OrgA, user, amounts);

        var allowedSum = amounts.Zip(decisions).Where(p => p.Second.IsAllowed).Sum(p => p.First);
        var total = await TotalAsync(tenant, TestDatabase.OrgA, user, Day);
        Assert.True(total <= Cap, $"persisted {total} exceeds cap {Cap}");
        Assert.Equal(allowedSum, total);
    }

    [Fact]
    public async Task The_cap_is_per_organization_and_user_and_one_tenant_never_sees_the_others_counter()
    {
        var (source, tenant) = await ArrangeAsync();
        await using var _ = source;
        var engine = EngineOver(new PostgresCumulativeLimitStore(tenant));
        var sameUser = Guid.NewGuid();

        var inA = await Fire(engine, TestDatabase.OrgA, sameUser, Enumerable.Repeat(100L, 12).ToArray());
        var inB = await Fire(engine, TestDatabase.OrgB, sameUser, Enumerable.Repeat(100L, 12).ToArray());
        var otherUser = await Fire(engine, TestDatabase.OrgA, Guid.NewGuid(), 1_000);

        Assert.Equal(10, inA.Count(d => d.IsAllowed));
        Assert.Equal(10, inB.Count(d => d.IsAllowed)); // org B starts from zero for the same user id
        Assert.True(otherUser[0].IsAllowed);
        await using var tx = await tenant.BeginAsync(TestDatabase.Ctx(TestDatabase.OrgA));
        await using var context = new PermissoesDbContext(tx);
        Assert.Equal(2, await context.Limites.CountAsync()); // RLS: only org A rows (sameUser + otherUser), not org B's
    }

    [Fact]
    public async Task A_decision_without_a_tenant_context_cannot_see_or_write_counters()
    {
        var (source, tenant) = await ArrangeAsync();
        await using var _ = source;
        var engine = EngineOver(new PostgresCumulativeLimitStore(tenant));
        await Fire(engine, TestDatabase.OrgA, Guid.NewGuid(), 100);

        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM limites_consumo", connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task The_ef_model_matches_the_migrated_schema()
    {
        var (source, tenant) = await ArrangeAsync();
        await using var _ = source;
        await using var tx = await tenant.BeginAsync(TestDatabase.Ctx(TestDatabase.OrgA));
        await using var context = new PermissoesDbContext(tx);

        Assert.Empty(await SchemaDrift.FindAsync(context));
    }

    /// <summary>
    /// The same harness against a deliberately wrong counter (read, think, write, no lock): it MUST overshoot. This is what proves the
    /// tests above can fail, i.e. that they are not green by accident.
    /// </summary>
    [Fact]
    public async Task Harness_detects_a_counter_without_locking()
    {
        var (source, tenant) = await ArrangeAsync();
        await using var _ = source;
        var engine = EngineOver(new ReadThenWriteLimitStore(tenant));
        var user = Guid.NewGuid();

        var decisions = await Fire(engine, TestDatabase.OrgA, user, Enumerable.Repeat(100L, 40).ToArray());

        Assert.True(decisions.Count(d => d.IsAllowed) > 10, "the unlocked counter should have let more than the cap through");
    }

    /// <summary>Check-then-update across separate statements, no lock: the classic lost-update race. Test double only.</summary>
    private sealed class ReadThenWriteLimitStore(TenantDatabase tenant) : ICumulativeLimitStore
    {
        public async Task<bool> TryConsumeAsync(Guid organizationId, Guid? unitId, Guid userId, string permission, DateOnly day, long cents, long capCents, CancellationToken cancellationToken)
        {
            await using var tx = await tenant.BeginAsync(new OrganizationContext(organizationId, unitId ?? Guid.NewGuid()), cancellationToken);
            await using var select = new NpgsqlCommand(
                "SELECT consumido_centavos FROM limites_consumo WHERE usuario_id = @u AND permissao = @p AND dia = @d", tx.Connection, tx.Transaction);
            select.Parameters.AddWithValue("u", userId);
            select.Parameters.AddWithValue("p", permission);
            select.Parameters.AddWithValue("d", day);
            var current = (long?)await select.ExecuteScalarAsync(cancellationToken) ?? 0;
            await Task.Delay(50, cancellationToken); // widen the window so the race is deterministic enough
            if (current + cents > capCents)
            {
                return false;
            }

            await using var write = new NpgsqlCommand(
                "INSERT INTO limites_consumo (organizacao_id, usuario_id, permissao, dia, consumido_centavos) VALUES (@o, @u, @p, @d, @v) " +
                "ON CONFLICT (organizacao_id, usuario_id, permissao, dia) DO UPDATE SET consumido_centavos = @v", tx.Connection, tx.Transaction);
            write.Parameters.AddWithValue("o", organizationId);
            write.Parameters.AddWithValue("u", userId);
            write.Parameters.AddWithValue("p", permission);
            write.Parameters.AddWithValue("d", day);
            write.Parameters.AddWithValue("v", current + cents);
            await write.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return true;
        }
    }
}
