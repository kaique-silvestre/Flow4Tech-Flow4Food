using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using static Flow4Tech.Authorization.Tests.Fixtures;

namespace Flow4Tech.Authorization.Tests;

/// <summary>
/// The behavioural contract of ANY engine behind <see cref="IAuthorizationEngine"/>. Subclass it with a factory and every test runs against
/// that engine. Engines are built from the same rules-as-data <see cref="PolicySet"/> (and counter store and clock): the contract fixes
/// decisions, not the policy language, so a Cedar or Casbin engine here is an ADAPTER translating that model, as in backend/spikes/authz.
/// It does not prove an engine could be fed native Cedar/Casbin policy files.
/// </summary>
public abstract class AuthorizationEngineContract
{
    protected InMemoryCumulativeLimitStore Limits { get; } = new();

    protected FixedClock Clock { get; } = new(Now);

    protected abstract IAuthorizationEngine Create(PolicySet policies, ICumulativeLimitStore limits, IClock clock);

    protected IAuthorizationEngine Engine() => Create(Standard(), Limits, Clock);

    protected Task<Decision> Ask(Subject who, string permission, Scope where, DecisionContext context) =>
        Engine().DecidirAsync(who, permission, where, context, CancellationToken.None);

    // ---- Case 1: discount up to X% by profile; above it is denied ----

    [Theory]
    [InlineData(Garcom, 5, true)]
    [InlineData(Garcom, 10, true)]
    [InlineData(Garcom, 10.01, false)]
    [InlineData(Garcom, 30, false)]
    [InlineData(Gerente, 30, true)]
    [InlineData(Gerente, 30.5, false)]
    public async Task Case1_discount_is_allowed_up_to_the_profile_limit_and_denied_above(string profile, double percent, bool allowed)
    {
        var decision = await Ask(Who(profile), Desconto, At(Unit1), Ctx(percent: (decimal)percent));

        Assert.Equal(allowed, decision.IsAllowed);
    }

    [Fact]
    public async Task Case1_a_limited_permission_without_the_measured_percent_is_denied()
    {
        var decision = await Ask(Who(Garcom), Desconto, At(Unit1), Ctx());

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task Case1_the_most_permissive_of_several_profiles_applies()
    {
        var decision = await Ask(Who(Garcom, Gerente), Desconto, At(Unit1), Ctx(percent: 25m));

        Assert.True(decision.IsAllowed);
    }

    // ---- Case 2: courtesy with a cumulative daily cap (sequential here; concurrency on PostgreSQL in the integration suite) ----

    [Fact]
    public async Task Case2_courtesies_are_allowed_until_the_daily_cap_would_be_exceeded()
    {
        Assert.True((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: 3_000))).IsAllowed); // 30 of 50
        Assert.False((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: 3_000))).IsAllowed); // 60 > 50
        Assert.True((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: 2_000))).IsAllowed); // exactly 50
        Assert.False((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: 1))).IsAllowed); // cap reached
        Assert.Equal(5_000, Limits.Total(OrgId, User, Cortesia, DateOnly.FromDateTime(Now.UtcDateTime)));
    }

    [Fact]
    public async Task Case2_the_cap_day_is_the_server_clock_day()
    {
        Assert.True((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: 5_000))).IsAllowed);
        Assert.False((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: 1))).IsAllowed); // same server day: cap reached

        Clock.UtcNow = Now.AddDays(1);

        Assert.True((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: 5_000))).IsAllowed);
    }

    [Fact]
    public async Task Case2_a_single_operation_above_the_cap_is_denied_and_a_capped_permission_needs_a_value()
    {
        Assert.False((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: 5_001))).IsAllowed);
        Assert.False((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx())).IsAllowed);
        Assert.False((await Ask(Who(Garcom), Cortesia, At(Unit1), Ctx(cents: -100))).IsAllowed);
        Assert.Equal(0, Limits.Consumptions);
    }

    [Fact]
    public async Task Case2_a_profile_without_a_cap_is_not_limited_and_consumes_nothing()
    {
        Assert.True((await Ask(Who(Gerente), Cortesia, At(Unit1), Ctx(cents: 999_999))).IsAllowed);
        Assert.Equal(0, Limits.Consumptions);
    }

    [Fact]
    public async Task Case2_a_denial_never_consumes_the_budget()
    {
        // Explicit deny wins, and must not leave a reservation behind.
        await Ask(Who(Garcom, Bloqueado), Cortesia, At(Unit1), Ctx(cents: 1_000));

        Assert.Equal(0, Limits.Consumptions);
    }

    // ---- Case 3: same permission, allowed only on unit 2 ----

    [Fact]
    public async Task Case3_manager_is_allowed_on_unit2_and_denied_on_unit1()
    {
        Assert.True((await Ask(Who(Gerente), AjustarEstoque, At(Unit2), Ctx())).IsAllowed);
        Assert.False((await Ask(Who(Gerente), AjustarEstoque, At(Unit1), Ctx())).IsAllowed);
        Assert.False((await Ask(Who(Gerente), AjustarEstoque, At(), Ctx())).IsAllowed); // no unit in the request: not unit 2
    }

    [Fact]
    public async Task A_unit_scoped_deny_also_denies_a_request_without_a_unit_but_not_another_unit()
    {
        // Allow everywhere for the waiter, but explicitly deny on unit 1.
        var policies = new PolicySet(Catalog,
        [
            new Rule(RuleEffect.Allow, Garcom, Relatorio),
            new Rule(RuleEffect.Deny, Garcom, Relatorio, UnitId: Unit1),
        ]);
        var engine = Create(policies, Limits, Clock);

        Assert.False((await engine.DecidirAsync(Who(Garcom), Relatorio, At(), Ctx())).IsAllowed); // unit unknown: fail closed
        Assert.False((await engine.DecidirAsync(Who(Garcom), Relatorio, At(Unit1), Ctx())).IsAllowed);
        Assert.True((await engine.DecidirAsync(Who(Garcom), Relatorio, At(Unit2), Ctx())).IsAllowed);
    }

    // ---- Explicit deny > allow > default deny; unknown permission denies ----

    [Fact]
    public async Task Explicit_deny_beats_allow_even_with_another_profile_that_allows()
    {
        Assert.True((await Ask(Who(Gerente), Relatorio, At(Unit1), Ctx())).IsAllowed);

        var decision = await Ask(Who(Gerente, Bloqueado), Relatorio, At(Unit1), Ctx());

        Assert.False(decision.IsAllowed);
        Assert.Equal("explicit-deny", decision.Reason);
    }

    [Fact]
    public async Task Default_is_deny_when_no_rule_allows_a_known_permission()
    {
        var decision = await Ask(Who(Caixa), Relatorio, At(Unit1), Ctx());

        Assert.False(decision.IsAllowed);
        Assert.Equal("default-deny", decision.Reason);
    }

    [Fact]
    public async Task A_user_with_no_profile_is_denied_everything()
    {
        foreach (var permission in Catalog)
        {
            Assert.False((await Ask(Who(), permission, At(Unit1), Ctx(percent: 1m, cents: 1))).IsAllowed);
        }
    }

    [Theory]
    [InlineData("desconto:apagar")]
    [InlineData("naoexiste")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("*")]
    [InlineData("DESCONTO:CONCEDER")]
    public async Task An_unknown_permission_is_denied_for_everyone(string permission)
    {
        var decision = await Ask(Who(Gerente, Garcom, Caixa), permission, At(Unit2), Ctx(percent: 1m, cents: 1));

        Assert.False(decision.IsAllowed);
        Assert.Equal("unknown-permission", decision.Reason);
    }

    [Fact]
    public async Task A_request_without_an_organization_is_denied()
    {
        var decision = await Ask(Who(Gerente), Relatorio, new Scope(Guid.Empty, Unit1), Ctx());

        Assert.False(decision.IsAllowed);
    }

    // ---- Profile x permission matrix ----

    /// <summary>Every profile x every catalog permission, with the unit/percent/value set so that only the PROFILE decides (limits are respected).</summary>
    public static TheoryData<string, string, bool> Matrix()
    {
        var expected = new Dictionary<(string, string), bool>
        {
            [(Garcom, Desconto)] = true,
            [(Garcom, Cortesia)] = true,
            [(Garcom, FecharCaixa)] = false,
            [(Garcom, AjustarEstoque)] = false,
            [(Garcom, Relatorio)] = false,
            [(Gerente, Desconto)] = true,
            [(Gerente, Cortesia)] = true,
            [(Gerente, FecharCaixa)] = false,
            [(Gerente, AjustarEstoque)] = true, // on unit 2, the unit used by the matrix
            [(Gerente, Relatorio)] = true,
            [(Caixa, Desconto)] = false,
            [(Caixa, Cortesia)] = false,
            [(Caixa, FecharCaixa)] = true,
            [(Caixa, AjustarEstoque)] = false,
            [(Caixa, Relatorio)] = false,
            [(Bloqueado, Desconto)] = false,
            [(Bloqueado, Cortesia)] = false,
            [(Bloqueado, FecharCaixa)] = false,
            [(Bloqueado, AjustarEstoque)] = false,
            [(Bloqueado, Relatorio)] = false,
        };
        var data = new TheoryData<string, string, bool>();
        foreach (var ((profile, permission), allowed) in expected)
        {
            data.Add(profile, permission, allowed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Matrix_profile_by_permission(string profile, string permission, bool allowed)
    {
        var decision = await Ask(Who(profile), permission, At(Unit2), Ctx(percent: 5m, cents: 1_000));

        Assert.Equal(allowed, decision.IsAllowed);
    }

    [Fact]
    public void Matrix_covers_every_profile_and_every_catalog_permission()
    {
        var covered = Matrix().Select(row => ((string)row[0], (string)row[1])).ToHashSet();
        var profiles = new[] { Garcom, Gerente, Caixa, Bloqueado };

        foreach (var profile in profiles)
        {
            foreach (var permission in Catalog)
            {
                Assert.Contains((profile, permission), covered);
            }
        }
    }
}

/// <summary>The rules-as-data engine (the one in production code) must satisfy the contract.</summary>
public sealed class RulesAuthorizationEngineTests : AuthorizationEngineContract
{
    protected override IAuthorizationEngine Create(PolicySet policies, ICumulativeLimitStore limits, IClock clock) =>
        new RulesAuthorizationEngine(policies, limits, clock);
}
