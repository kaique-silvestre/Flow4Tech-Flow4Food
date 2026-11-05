using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Flow4Tech.Permissoes.Infrastructure;
using Npgsql;
using static Flow4Tech.Authorization.Tests.Fixtures;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Authorization.Tests;

/// <summary>
/// Spec 5.4.7: the whole behavioural contract of the engine (cases 1 to 3, deny precedence, the profile x permission matrix) must hold when the rules are READ FROM THE DATABASE.
/// Each <see cref="AuthorizationEngineContract.Create"/> writes the policy it is given as custom profiles of tenant A (through the real store, application credential, RLS) and returns the
/// production engine over <see cref="PostgresPolicySource"/>. The contract talks in profile names; persisted profiles are keyed by id, so a thin wrapper translates the names the contract uses
/// into the keys the profiles got, and nothing else: every decision is the real engine's. The database is shared by the tests of this class (they never run in parallel with each other).
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class PersistedRulesEngineTests(PostgresFixture postgres) : AuthorizationEngineContract, IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Shared? s_shared;

    // Lives for the whole run (one database shared by every test of the class); the container takes the server down at the end.
    private sealed class Shared : IDisposable
    {
        private readonly NpgsqlDataSource _source;

        public Shared(string connectionString)
        {
            _source = NpgsqlDataSource.Create(connectionString);
            Tenant = new TenantDatabase(_source);
        }

        public TenantDatabase Tenant { get; }

        public void Dispose() => _source.Dispose();

        public string? Signature { get; set; }

        public Dictionary<string, string> Keys { get; } = [];
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (s_shared is null)
            {
                var db = await postgres.CreateDatabaseAsync();
                await db.SeedTwoTenantsAsync();
                await PerfisTestEnv.AddUnit2Async(db);
                s_shared = new Shared(db.AppConnectionString);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    protected override IAuthorizationEngine Create(PolicySet policies, ICumulativeLimitStore limits, IOperatingDayResolver days)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var shared = s_shared ?? throw new InvalidOperationException("The shared database was not created.");
        Gate.Wait();
        try
        {
            var signature = string.Join('|', policies.Rules.Select(r => r.ToString())) + "#" + string.Join(',', policies.KnownPermissions.Order(StringComparer.Ordinal));
            if (shared.Signature != signature)
            {
                Rewrite(shared, policies).GetAwaiter().GetResult();
                shared.Signature = signature;
            }

            var catalogOnly = new PolicySet(policies.KnownPermissions, []);
            var engine = new RulesAuthorizationEngine(new PostgresPolicySource(shared.Tenant, catalogOnly), limits, days);
            return new NameTranslatingEngine(engine, new Dictionary<string, string>(shared.Keys));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task Rewrite(Shared shared, PolicySet policies)
    {
        shared.Keys.Clear();
        var uow = new TenantUnitOfWork(shared.Tenant);
        var store = new PostgresPerfilStore();
        await uow.ExecuteAsync(
            Ctx(OrgA),
            async ct =>
            {
                var tx = TenantUnitOfWork.Require(OrgA);
                await Exec(tx, "DELETE FROM perfis");
                foreach (var group in policies.Rules.GroupBy(r => r.Profile))
                {
                    var id = EntityId.New().Value;
                    var rules = group.Select(r => new RegraDeAcesso(r.Effect, r.Permission, r.UnitId, r.MaxPercent, r.MaxValueCents, r.DailyCapCents)).ToList();
                    Assert.True(await store.CriarAsync(OrgA, new PerfilPersonalizado(id, group.Key, null, false, rules), Now, ct));
                    shared.Keys[group.Key] = ChavesDePerfil.Personalizado(id);
                }
            });
    }

    /// <summary>Renames the profile names of the contract into persisted keys; every decision is the wrapped engine's.</summary>
    private sealed class NameTranslatingEngine(IAuthorizationEngine inner, Dictionary<string, string> keys) : IAuthorizationEngine
    {
        public Task<Decision> DecidirAsync(Subject who, string permission, Scope where, DecisionContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(who);
            var translated = who.Profiles.Select(p => keys.TryGetValue(p, out var key) ? key : p).ToList();
            return inner.DecidirAsync(who with { Profiles = translated }, permission, where, context, cancellationToken);
        }
    }
}

/// <summary>Checklist item 5 of ticket 06: the daily cap holds under concurrency when the cap comes from a persisted profile, not from code.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class CumulativeLimitFromPersistedRulesIntegrationTests(PostgresFixture postgres)
{
    private const long Cap = 1_000;

    [Fact]
    public async Task Forty_concurrent_requests_never_exceed_a_cap_that_was_read_from_the_database()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var waiter = await env.AddAccountAsync(owner: false);
        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Garçom com teto", [new RegraDeAcesso(RuleEffect.Allow, Cortesia, DailyCapCents: Cap)]);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), waiter, profile.Id!.Value);
        var keys = await env.KeysOfAsync(waiter);

        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await env.Uow.ExecuteAsync(
                PerfisTestEnv.OrgContext,
                ct => env.Engine.DecidirAsync(new Subject(waiter, keys), Cortesia, new Scope(OrgA, UnitA), new DecisionContext(new Money(100, "BRL")), ct));
        })).ToArray();
        start.SetResult();
        var decisions = await Task.WhenAll(tasks);

        Assert.Equal(10, decisions.Count(d => d.IsAllowed)); // 10 x R$ 1,00 = the whole cap, never more
        Assert.Equal(Cap, await env.ConsumedAsync(waiter, Cortesia));
        Assert.All(decisions.Where(d => !d.IsAllowed), d => Assert.Equal("daily-cap-exceeded", d.Reason));
    }

    [Fact]
    public async Task Lowering_the_cap_in_the_database_takes_effect_on_the_next_decision()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var waiter = await env.AddAccountAsync(owner: false);
        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Garçom", [new RegraDeAcesso(RuleEffect.Allow, Cortesia, DailyCapCents: 5_000)]);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), waiter, profile.Id!.Value);
        var spend = new DecisionContext(new Money(3_000, "BRL"));
        // A capped decision belongs to a unit of work (ADR-006).
        Task<Decision> DecideInUnit() => env.Uow.ExecuteAsync(PerfisTestEnv.OrgContext, ct => env.DecideAsync(waiter, Cortesia, UnitA, spend));
        Assert.True((await DecideInUnit()).IsAllowed); // R$ 30 under a R$ 50 cap

        await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), profile.Id.Value.ToString("D"), [new RegraDeAcesso(RuleEffect.Allow, Cortesia, DailyCapCents: 2_000)]);

        var decision = await DecideInUnit();
        Assert.False(decision.IsAllowed);
        Assert.Equal("limit-exceeded", decision.Reason);
    }
}
