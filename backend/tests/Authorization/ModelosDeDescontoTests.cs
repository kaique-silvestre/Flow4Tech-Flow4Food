using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Authorization.Tests;

/// <summary>
/// F4-0118: the model profiles carry discount, courtesy and gift with default limits, decided by the engine from rules that are plain data (nothing reads a profile name). The manager is capped
/// (discount 20% and R$ 150,00; courtesy and gift R$ 80,00, always measured at 100% so they carry no percent cap); the waiter and the cashier do not hold them. The owner is the factory profile
/// that holds the whole catalog without a cap, covered by <see cref="PoliticaDeFabricaTests"/>.
/// </summary>
public sealed class ModelosDeDescontoTests
{
    private static readonly string[] Limited = ["comanda:desconto", "comanda:cortesia", "comanda:brinde"];

    private static readonly Guid Org = Guid.Parse("0197a000-0000-7000-8000-00000000000a");

    private static RulesAuthorizationEngine EngineWith(ModeloDePerfil model, string profile)
    {
        var rules = ModelosDePerfil.Regras(model).Select(r => r.ToRule(profile)).ToList();
        return new RulesAuthorizationEngine(new PolicySet(ModelosDePerfil.Todos.SelectMany(m => m.Permissoes).Distinct().ToList(), rules), new InMemoryCumulativeLimitStore(), new ConfiguredOperatingDayResolver(new FixedClock(Fixtures.Now)));
    }

    private static Task<Decision> AskAsync(string chave, string permission, decimal percent, long cents)
    {
        const string Profile = "modelo";
        var model = ModelosDePerfil.Todos.Single(m => m.Chave == chave);
        return EngineWith(model, Profile).DecidirAsync(new Subject(Fixtures.User, [Profile]), permission, new Scope(Org), new DecisionContext(new Money(cents, "BRL"), percent));
    }

    [Theory]
    [InlineData("comanda:desconto", 20, 15000, true)]
    [InlineData("comanda:desconto", 20.01, 100, false)]
    [InlineData("comanda:desconto", 5, 15001, false)]
    [InlineData("comanda:cortesia", 100, 8000, true)]
    [InlineData("comanda:cortesia", 100, 8001, false)]
    [InlineData("comanda:brinde", 100, 8000, true)]
    [InlineData("comanda:brinde", 100, 8001, false)]
    public async Task The_manager_model_is_capped_by_percent_and_by_value(string permission, double percent, long cents, bool allowed)
    {
        Assert.Equal(allowed, (await AskAsync("gerente", permission, (decimal)percent, cents)).IsAllowed);
    }

    [Theory]
    [InlineData("garcom")]
    [InlineData("caixa")]
    public async Task The_waiter_and_cashier_models_are_denied_all_three(string chave)
    {
        foreach (var permission in Limited)
        {
            Assert.False((await AskAsync(chave, permission, 1m, 1)).IsAllowed);
        }
    }

    [Fact]
    public void Only_the_manager_model_holds_the_three_and_each_limit_names_a_permission_of_its_model()
    {
        Assert.Equal(["gerente"], ModelosDePerfil.Todos.Where(m => Limited.All(m.Permissoes.Contains)).Select(m => m.Chave));
        Assert.All(ModelosDePerfil.Todos, m => Assert.All(m.Limites, l => Assert.Contains(l.Permission, m.Permissoes)));
        Assert.All(ModelosDePerfil.Todos.Where(m => m.Chave != "gerente"), m => Assert.Empty(m.Permissoes.Intersect(Limited)));
    }
}
