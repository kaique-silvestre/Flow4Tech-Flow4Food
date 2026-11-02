using Flow4Tech.Permissoes.Domain;
using static Flow4Tech.Authorization.Tests.Fixtures;

namespace Flow4Tech.Authorization.Tests;

public sealed class PolicySetTests
{
    [Fact]
    public void A_rule_over_a_permission_missing_from_the_catalog_is_a_configuration_error()
    {
        var rules = new[] { new Rule(RuleEffect.Allow, Garcom, "desconto:inventado") };

        Assert.Throws<ArgumentException>(() => new PolicySet(Catalog, rules));
    }

    [Fact]
    public void A_limit_on_a_deny_rule_is_a_configuration_error()
    {
        var rules = new[] { new Rule(RuleEffect.Deny, Garcom, Desconto, MaxPercent: 5m) };

        Assert.Throws<ArgumentException>(() => new PolicySet(Catalog, rules));
    }

    [Fact]
    public void The_standard_policy_is_valid_and_exposes_its_catalog()
    {
        var policies = Standard();

        Assert.Equal(5, policies.KnownPermissions.Count);
        Assert.Equal(10, policies.Rules.Count);
    }
}
