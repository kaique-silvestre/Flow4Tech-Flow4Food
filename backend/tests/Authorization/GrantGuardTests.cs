using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Domain;
using static Flow4Tech.Authorization.Tests.Fixtures;

namespace Flow4Tech.Authorization.Tests;

/// <summary>Anti-escalation (spec 5.4.5), pure: nobody hands on what they do not hold, or hold less generously.</summary>
public sealed class GrantGuardTests
{
    private static PolicySet Policy(params Rule[] rules) => new(Catalog, rules);

    private static bool Can(PolicySet policy, RegraDeAcesso proposed, params string[] actorProfiles) =>
        GrantGuard.CanGrant(policy, new Subject(User, actorProfiles), proposed);

    [Fact]
    public void An_actor_can_grant_what_they_hold()
    {
        var policy = Policy(new Rule(RuleEffect.Allow, Gerente, Relatorio));

        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Relatorio), Gerente));
    }

    [Fact]
    public void An_actor_cannot_grant_what_they_do_not_hold()
    {
        var policy = Policy(new Rule(RuleEffect.Allow, Gerente, Relatorio));

        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, FecharCaixa), Gerente));
        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Relatorio), Caixa));
        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Relatorio)));
    }

    [Fact]
    public void An_unknown_permission_is_never_grantable()
    {
        var policy = Policy(new Rule(RuleEffect.Allow, Gerente, Relatorio));

        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, "coisa:inexistente"), Gerente));
        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Deny, "coisa:inexistente"), Gerente));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(5, true)]
    [InlineData(10.01, false)]
    [InlineData(30, false)]
    public void A_percent_limit_cannot_be_raised(double percent, bool allowed)
    {
        var policy = Policy(new Rule(RuleEffect.Allow, Garcom, Desconto, MaxPercent: 10m));

        Assert.Equal(allowed, Can(policy, new RegraDeAcesso(RuleEffect.Allow, Desconto, MaxPercent: (decimal)percent), Garcom));
    }

    [Fact]
    public void A_limit_the_actor_has_cannot_be_dropped()
    {
        var policy = Policy(new Rule(RuleEffect.Allow, Garcom, Desconto, MaxPercent: 10m), new Rule(RuleEffect.Allow, Garcom, Cortesia, DailyCapCents: 5_000));

        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Desconto), Garcom)); // no limit = more than 10%
        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Cortesia, MaxValueCents: 100), Garcom)); // a per-operation limit does not replace the daily cap
        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Cortesia, DailyCapCents: 5_000), Garcom));
        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Cortesia, MaxValueCents: 100, DailyCapCents: 4_000), Garcom));
    }

    [Fact]
    public void An_uncapped_actor_can_grant_any_limit()
    {
        var policy = Policy(new Rule(RuleEffect.Allow, Gerente, Cortesia));

        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Cortesia, DailyCapCents: 99_999_999), Gerente));
        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Cortesia), Gerente));
    }

    [Fact]
    public void A_unit_scoped_actor_grants_only_inside_their_unit()
    {
        var policy = Policy(new Rule(RuleEffect.Allow, Gerente, AjustarEstoque, UnitId: Unit2));

        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Allow, AjustarEstoque, UnitId: Unit2), Gerente));
        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, AjustarEstoque, UnitId: Unit1), Gerente));
        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, AjustarEstoque), Gerente)); // everywhere is wider than unit 2
    }

    [Fact]
    public void An_actor_deny_blocks_the_grant_where_it_reaches()
    {
        var policy = Policy(
            new Rule(RuleEffect.Allow, Gerente, Relatorio),
            new Rule(RuleEffect.Deny, Gerente, Relatorio, UnitId: Unit1));

        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Relatorio, UnitId: Unit1), Gerente));
        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Relatorio), Gerente)); // everywhere includes the denied unit
        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Relatorio, UnitId: Unit2), Gerente));
    }

    [Fact]
    public void Granting_a_deny_only_narrows_so_it_needs_nothing_beyond_a_known_permission()
    {
        var policy = Policy(new Rule(RuleEffect.Allow, Gerente, Relatorio));

        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Deny, FecharCaixa), Gerente));
    }

    [Fact]
    public void Several_profiles_are_read_together_but_each_allow_must_cover_the_whole_proposal()
    {
        var policy = Policy(
            new Rule(RuleEffect.Allow, Garcom, Desconto, MaxPercent: 10m),
            new Rule(RuleEffect.Allow, Gerente, Desconto, MaxPercent: 30m));

        Assert.True(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Desconto, MaxPercent: 25m), Garcom, Gerente));
        Assert.False(Can(policy, new RegraDeAcesso(RuleEffect.Allow, Desconto, MaxPercent: 31m), Garcom, Gerente));
    }
}
