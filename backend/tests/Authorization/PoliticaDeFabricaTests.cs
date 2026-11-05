using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;

namespace Flow4Tech.Authorization.Tests;

/// <summary>
/// Ticket 05 (spec 5.4): until profiles are persisted (ticket 06) the host asks the engine with the factory profiles. The profile of an account is data
/// (<see cref="IPerfisDoUsuario"/>), the rules are data (<see cref="PoliticaDeFabrica"/>): no code branches on a profile name.
/// </summary>
public sealed class PoliticaDeFabricaTests
{
    private static readonly string[] Catalog =
    [
        "organizacao:ver-unidades",
        "organizacao:trocar-unidade",
        "organizacao:configurar",
        "sessao:trocar-senha",
        "sessao:ver-proprias",
        "sessao:revogar-propria",
        "conta:gerir-google",
        "usuario:convidar",
        "usuario:redefinir-acesso",
        "sessao:ver-todas",
        "sessao:revogar",
        "sistema:ver-versao",
        "financeiro:exportar",
    ];

    private static readonly Guid Org = Guid.Parse("0197a000-0000-7000-8000-00000000000a");

    private static RulesAuthorizationEngine Engine() =>
        new(PoliticaDeFabrica.Criar(Catalog), new InMemoryCumulativeLimitStore(), new ConfiguredOperatingDayResolver(new FixedClock(Fixtures.Now)));

    private static Task<Decision> AskAsync(string permission, params string[] profiles) =>
        Engine().DecidirAsync(new Subject(Fixtures.User, profiles), permission, new Scope(Org), new DecisionContext());

    [Theory]
    [InlineData("organizacao:ver-unidades")]
    [InlineData("organizacao:trocar-unidade")]
    [InlineData("sessao:trocar-senha")]
    [InlineData("sessao:ver-proprias")]
    [InlineData("sessao:revogar-propria")]
    [InlineData("conta:gerir-google")]
    public async Task Every_account_can_do_what_concerns_only_itself(string permission)
    {
        Assert.True((await AskAsync(permission, PerfisDeFabrica.Conta)).IsAllowed);
        Assert.True((await AskAsync(permission, PerfisDeFabrica.Dono)).IsAllowed);
    }

    [Theory]
    [InlineData("sessao:ver-todas")]
    [InlineData("sessao:revogar")]
    [InlineData("usuario:convidar")]
    [InlineData("usuario:redefinir-acesso")]
    [InlineData("organizacao:configurar")]
    [InlineData("financeiro:exportar")]
    public async Task An_ordinary_account_gets_nothing_administrative_or_financial_by_default(string permission)
    {
        Assert.False((await AskAsync(permission, PerfisDeFabrica.Conta)).IsAllowed);
    }

    [Fact]
    public async Task The_owner_profile_is_allowed_everything_in_the_catalog()
    {
        foreach (var permission in Catalog)
        {
            Assert.True((await AskAsync(permission, PerfisDeFabrica.Dono)).IsAllowed, permission);
        }
    }

    [Fact]
    public async Task No_profile_and_an_unknown_profile_are_denied_everything()
    {
        foreach (var permission in Catalog)
        {
            Assert.False((await AskAsync(permission)).IsAllowed, permission);
            Assert.False((await AskAsync(permission, "perfil-que-nao-existe")).IsAllowed, permission);
        }
    }

    [Fact]
    public async Task An_unknown_permission_is_denied_even_to_the_owner()
    {
        Assert.False((await AskAsync("coisa:inexistente", PerfisDeFabrica.Dono)).IsAllowed);
    }

    [Fact]
    public void The_factory_policy_refuses_a_catalog_that_lacks_a_permission_it_grants()
    {
        // A renamed permission must stop the host at startup instead of silently granting nothing.
        Assert.Throws<ArgumentException>(() => PoliticaDeFabrica.Criar(["sistema:ver-versao"]));
    }
}
