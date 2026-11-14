using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Authorization.Tests;

/// <summary>
/// F4-0065: the model profiles carry the complete cash register with default limits, decided by the engine from rules that are plain data (nothing reads a profile name). The cashier moves money
/// in and out up to a cap and closes blind; the manager also approves a difference up to a cap; the waiter holds none of it. The owner is the factory profile (uncapped), covered by
/// <see cref="PoliticaDeFabricaTests"/>.
/// </summary>
public sealed class ModelosDeCaixaTests
{
    private static readonly Guid Org = Guid.Parse("0197a000-0000-7000-8000-00000000000a");

    private static readonly string[] Novas = ["caixa:suprimento", "caixa:sangria", "caixa:fechar-cego", "caixa:aprovar-diferenca"];

    private static Task<Decision> AskAsync(string chave, string permission, long cents)
    {
        const string Profile = "modelo";
        var model = ModelosDePerfil.Todos.Single(m => m.Chave == chave);
        var rules = ModelosDePerfil.Regras(model).Select(r => r.ToRule(Profile)).ToList();
        var engine = new RulesAuthorizationEngine(new PolicySet(ModelosDePerfil.Todos.SelectMany(m => m.Permissoes).Distinct().ToList(), rules), new InMemoryCumulativeLimitStore(), new ConfiguredOperatingDayResolver(new FixedClock(Fixtures.Now)));
        return engine.DecidirAsync(new Subject(Fixtures.User, [Profile]), permission, new Scope(Org), new DecisionContext(new Money(cents, "BRL")));
    }

    [Theory]
    [InlineData("caixa", "caixa:suprimento", 50_000, true)]
    [InlineData("caixa", "caixa:suprimento", 50_001, false)]
    [InlineData("caixa", "caixa:sangria", 100_000, true)]
    [InlineData("caixa", "caixa:sangria", 100_001, false)]
    [InlineData("gerente", "caixa:suprimento", 500_000, true)]
    [InlineData("gerente", "caixa:suprimento", 500_001, false)]
    [InlineData("gerente", "caixa:sangria", 500_000, true)]
    [InlineData("gerente", "caixa:sangria", 500_001, false)]
    [InlineData("gerente", "caixa:aprovar-diferenca", 20_000, true)]
    [InlineData("gerente", "caixa:aprovar-diferenca", 20_001, false)]
    public async Task The_cashier_and_manager_models_are_capped_by_amount(string chave, string permission, long cents, bool allowed) =>
        Assert.Equal(allowed, (await AskAsync(chave, permission, cents)).IsAllowed);

    [Fact]
    public async Task The_waiter_model_holds_none_of_the_new_permissions_and_the_cashier_does_not_approve_its_own_difference()
    {
        foreach (var permission in Novas)
        {
            Assert.False((await AskAsync("garcom", permission, 1)).IsAllowed);
        }

        Assert.False((await AskAsync("caixa", "caixa:aprovar-diferenca", 1)).IsAllowed);
        Assert.True((await AskAsync("caixa", "caixa:fechar-cego", 1)).IsAllowed);
        Assert.True((await AskAsync("gerente", "caixa:fechar-cego", 1)).IsAllowed);
    }

    [Fact]
    public void Separation_of_duties_is_in_the_data_the_model_that_closes_does_not_approve_and_the_one_that_approves_sees_every_shift()
    {
        var caixa = ModelosDePerfil.Todos.Single(m => m.Chave == "caixa").Permissoes;
        var gerente = ModelosDePerfil.Todos.Single(m => m.Chave == "gerente").Permissoes;

        Assert.Contains("caixa:fechar", caixa);
        Assert.DoesNotContain("caixa:aprovar-diferenca", caixa);
        Assert.DoesNotContain("caixa:ver-todos", caixa);
        Assert.Contains("caixa:aprovar-diferenca", gerente);
        Assert.Contains("caixa:ver-todos", gerente);
        Assert.All(ModelosDePerfil.Todos, m => Assert.All(m.Limites, l => Assert.Contains(l.Permission, m.Permissoes)));
    }
}
