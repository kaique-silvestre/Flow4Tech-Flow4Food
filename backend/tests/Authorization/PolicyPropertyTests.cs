using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Domain;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Flow4Tech.Authorization.Tests;

/// <summary>A random policy, a random subject, a random scope and a random decision context, small enough that every case is meaningful.</summary>
public readonly record struct Cenario(PolicySet Policy, Subject Who, Scope Where, DecisionContext Context, string Permission);

public readonly record struct Proposta(RegraDeAcesso Regra);

public static class PolicyGeradores
{
    public static readonly Guid Org = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    public static readonly Guid U1 = Guid.Parse("0197a000-0000-7000-8000-0000000000b1");
    public static readonly Guid U2 = Guid.Parse("0197a000-0000-7000-8000-0000000000b2");

    public static readonly string[] Perms = ["relatorio:ver", "caixa:fechar", "desconto:dar"];
    public static readonly string[] Profiles = ["p1", "p2", "p3"];

    private static Gen<Guid?> UnitGen() => Gen.Elements<Guid?>(null, U1, U2);

    private static Gen<decimal?> PercentLimit() => Gen.Elements<decimal?>(null, 5m, 10m);

    private static Gen<long?> CentsLimit() => Gen.Elements<long?>(null, 5L, 10L);

    private static Gen<Rule> RuleGen() =>
        from deny in Gen.Frequency((1, Gen.Constant(true)), (3, Gen.Constant(false)))
        from profile in Gen.Elements(Profiles)
        from perm in Gen.Elements(Perms)
        from unit in UnitGen()
        from percent in PercentLimit()
        from max in CentsLimit()
        from cap in CentsLimit()
        select deny
            ? new Rule(RuleEffect.Deny, profile, perm, unit)
            : new Rule(RuleEffect.Allow, profile, perm, unit, percent, max, cap);

    private static Gen<Subject> SubjectGen() =>
        from n in Gen.Choose(0, 3)
        from picked in Gen.Shuffle(Profiles)
        select new Subject(Guid.Parse("0197a000-0000-7000-8000-0000000000a1"), [.. picked.Take(n)]);

    private static Gen<DecisionContext> ContextGen() =>
        from value in Gen.Elements<Money?>(null, new Money(0, "BRL"), new Money(5, "BRL"), new Money(10, "BRL"), new Money(11, "BRL"), new Money(-1, "BRL"), new Money(5, "USD"))
        from percent in Gen.Elements<decimal?>(null, 0m, 5m, 10m, 11m, -1m)
        select new DecisionContext(value, percent);

    public static Arbitrary<Cenario> Cenario() =>
        (from n in Gen.Choose(0, 6)
         from rules in Gen.ListOf(RuleGen(), n)
         from who in SubjectGen()
         from unit in UnitGen()
         from ctx in ContextGen()
         from perm in Gen.Elements(Perms)
         select new Cenario(new PolicySet(Perms, rules), who, new Scope(Org, unit), ctx, perm)).ToArbitrary();

    public static Arbitrary<Proposta> Proposta() =>
        (from allow in Gen.Frequency((1, Gen.Constant(false)), (4, Gen.Constant(true)))
         from perm in Gen.Elements(Perms)
         from unit in UnitGen()
         from percent in PercentLimit()
         from max in CentsLimit()
         from cap in CentsLimit()
         select new Proposta(allow
             ? new RegraDeAcesso(RuleEffect.Allow, perm, unit, percent, max, cap)
             : new RegraDeAcesso(RuleEffect.Deny, perm, unit))).ToArbitrary();
}

/// <summary>
/// Property tests (F4-0105) of the pure authorization core: they hold for EVERY policy, subject, scope and context the generators build,
/// not only for the profile x permission matrix of the contract tests. The central one is the anti-escalation closure of the guard.
/// </summary>
public sealed class PolicyPropertyTests
{
    private static Effect Decide(PolicySet policy, Subject who, string permission, Scope where, DecisionContext context) =>
        PolicyEvaluator.Evaluate(policy, who, permission, where, context).Effect;

    private static PolicySet With(PolicySet policy, params Rule[] extra) => new(policy.KnownPermissions, [.. policy.Rules, .. extra]);

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool An_unknown_permission_is_always_denied(Cenario c) =>
        Decide(c.Policy, c.Who, "coisa:inexistente", c.Where, c.Context) == Effect.Deny;

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool Without_profiles_nothing_is_allowed(Cenario c) =>
        Decide(c.Policy, new Subject(c.Who.UserId, []), c.Permission, c.Where, c.Context) == Effect.Deny;

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool A_matching_deny_beats_every_allow(Cenario c)
    {
        if (c.Who.Profiles.Count == 0)
        {
            return true;
        }

        var deny = new Rule(RuleEffect.Deny, c.Who.Profiles[0], c.Permission, c.Where.UnitId);
        return Decide(With(c.Policy, deny), c.Who, c.Permission, c.Where, c.Context) == Effect.Deny;
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool Adding_an_allow_rule_never_takes_access_away(Cenario c, Proposta extra)
    {
        if (extra.Regra.Effect != RuleEffect.Allow || c.Who.Profiles.Count == 0)
        {
            return true;
        }

        var before = Decide(c.Policy, c.Who, c.Permission, c.Where, c.Context);
        var after = Decide(With(c.Policy, extra.Regra.ToRule(c.Who.Profiles[0])), c.Who, c.Permission, c.Where, c.Context);
        return before == Effect.Deny || after == Effect.Allow;
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool Removing_a_deny_rule_never_takes_access_away(Cenario c)
    {
        var sem = new PolicySet(c.Policy.KnownPermissions, c.Policy.Rules.Where(r => r.Effect != RuleEffect.Deny));
        var before = Decide(c.Policy, c.Who, c.Permission, c.Where, c.Context);
        return before == Effect.Deny || Decide(sem, c.Who, c.Permission, c.Where, c.Context) == Effect.Allow;
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool The_order_of_the_rules_does_not_change_the_decision(Cenario c)
    {
        var invertida = new PolicySet(c.Policy.KnownPermissions, c.Policy.Rules.Reverse());
        return Decide(c.Policy, c.Who, c.Permission, c.Where, c.Context) == Decide(invertida, c.Who, c.Permission, c.Where, c.Context);
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool A_value_limit_allows_exactly_non_negative_brl_up_to_the_limit(Cenario c)
    {
        var rule = new Rule(RuleEffect.Allow, "solo", c.Permission, null, null, 10);
        var policy = new PolicySet(PolicyGeradores.Perms, [rule]);
        var decision = Decide(policy, new Subject(c.Who.UserId, ["solo"]), c.Permission, new Scope(PolicyGeradores.Org, c.Where.UnitId), c.Context);
        var expected = c.Context.Value is { Currency: "BRL", Cents: >= 0 and <= 10 };
        return (decision == Effect.Allow) == expected;
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool A_percent_limit_allows_exactly_the_percent_inside_the_limit_and_fails_closed_without_it(Cenario c)
    {
        var rule = new Rule(RuleEffect.Allow, "solo", c.Permission, null, 10);
        var policy = new PolicySet(PolicyGeradores.Perms, [rule]);
        var decision = Decide(policy, new Subject(c.Who.UserId, ["solo"]), c.Permission, new Scope(PolicyGeradores.Org, c.Where.UnitId), c.Context);
        var expected = c.Context.Percent is >= 0 and <= 10;
        return (decision == Effect.Allow) == expected;
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool An_allow_scoped_to_a_unit_never_works_in_another_unit_or_without_unit(Cenario c)
    {
        var rule = new Rule(RuleEffect.Allow, "solo", c.Permission, PolicyGeradores.U1);
        var policy = new PolicySet(PolicyGeradores.Perms, [rule]);
        var decision = Decide(policy, new Subject(c.Who.UserId, ["solo"]), c.Permission, c.Where, new DecisionContext());
        return (decision == Effect.Allow) == (c.Where.UnitId == PolicyGeradores.U1);
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool An_empty_organization_is_always_denied(Cenario c) =>
        Decide(c.Policy, c.Who, c.Permission, new Scope(Guid.Empty, c.Where.UnitId), c.Context) == Effect.Deny;

    // ---- anti-escalation (spec 5.4.5) ----

    [Property(Arbitrary = [typeof(PolicyGeradores)], MaxTest = 2000)]
    public bool Whatever_the_guard_lets_an_actor_hand_on_gives_nothing_the_actor_could_not_do(Cenario c, Proposta proposta)
    {
        if (!GrantGuard.CanGrant(c.Policy, c.Who, proposta.Regra))
        {
            return true;
        }

        if (proposta.Regra.Effect == RuleEffect.Deny)
        {
            return true; // removing access is always safe
        }

        // A fresh profile that carries ONLY the proposed rule may allow, at ANY unit the request can name, only what the actor could already do.
        var recipient = new Subject(Guid.NewGuid(), ["receptor"]);
        var granted = With(c.Policy, proposta.Regra.ToRule("receptor"));
        foreach (var unit in new Guid?[] { null, PolicyGeradores.U1, PolicyGeradores.U2 })
        {
            var where = new Scope(PolicyGeradores.Org, unit);
            var gotByGrant = Decide(granted, recipient, proposta.Regra.Permission, where, c.Context);
            var actorCan = Decide(c.Policy, c.Who, proposta.Regra.Permission, where, c.Context);
            if (gotByGrant == Effect.Allow && actorCan == Effect.Deny)
            {
                return false;
            }
        }

        return true;
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool A_deny_proposal_for_a_known_permission_is_always_grantable_and_an_unknown_one_never(Cenario c, Proposta proposta)
    {
        var deny = new RegraDeAcesso(RuleEffect.Deny, proposta.Regra.Permission, proposta.Regra.UnitId);
        var unknown = deny with { Permission = "coisa:inexistente" };
        return GrantGuard.CanGrant(c.Policy, c.Who, deny) && !GrantGuard.CanGrant(c.Policy, c.Who, unknown);
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool An_actor_cannot_grant_an_allow_without_holding_a_rule_for_it(Cenario c, Proposta proposta)
    {
        if (proposta.Regra.Effect != RuleEffect.Allow)
        {
            return true;
        }

        var held = PolicyEvaluator.RulesHeldBy(c.Policy, c.Who, proposta.Regra.Permission);
        return held.Count > 0 || !GrantGuard.CanGrant(c.Policy, c.Who, proposta.Regra);
    }

    [Property(Arbitrary = [typeof(PolicyGeradores)])]
    public bool A_tighter_proposal_is_never_harder_to_grant_than_a_looser_one(Cenario c, Proposta proposta)
    {
        if (proposta.Regra.Effect != RuleEffect.Allow)
        {
            return true;
        }

        var tighter = proposta.Regra with
        {
            MaxPercent = proposta.Regra.MaxPercent is { } p ? p / 2 : 5m,
            MaxValueCents = proposta.Regra.MaxValueCents is { } v ? v / 2 : 5,
            DailyCapCents = proposta.Regra.DailyCapCents is { } d ? d / 2 : 5,
        };
        return !GrantGuard.CanGrant(c.Policy, c.Who, proposta.Regra) || GrantGuard.CanGrant(c.Policy, c.Who, tighter);
    }
}
