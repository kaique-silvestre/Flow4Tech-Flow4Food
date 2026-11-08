using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0050: pure rules of option groups (min/max, required, membership, inactive) and the item total with options in cents. No I/O.</summary>
public sealed class OpcoesRulesTests
{
    private static readonly Opcao Bacon = new(Guid.CreateVersion7(), "Bacon", 500, true);
    private static readonly Opcao Queijo = new(Guid.CreateVersion7(), "Queijo extra", 300, true);
    private static readonly Opcao SemCebola = new(Guid.CreateVersion7(), "Sem cebola", 0, true);
    private static readonly Opcao Mal = new(Guid.CreateVersion7(), "Mal passado", 0, true);
    private static readonly Opcao Ponto = new(Guid.CreateVersion7(), "Ao ponto", 0, true);
    private static readonly Opcao Acabou = new(Guid.CreateVersion7(), "Cheddar", 400, false);

    private static readonly GrupoDeOpcao Adicionais = new(Guid.CreateVersion7(), "Adicionais", 0, 2, true, 1, [Bacon, Queijo, Acabou]);
    private static readonly GrupoDeOpcao Retiradas = new(Guid.CreateVersion7(), "Retirar", 0, 3, true, 1, [SemCebola]);
    private static readonly GrupoDeOpcao PontoDaCarne = new(Guid.CreateVersion7(), "Ponto", 1, 1, true, 1, [Mal, Ponto]);

    private static readonly IReadOnlyList<GrupoDeOpcao> Todos = [Adicionais, Retiradas, PontoDaCarne];

    [Fact]
    public void A_valid_choice_returns_the_options_with_their_group()
    {
        var r = RegrasDeOpcoes.Escolher(Todos, [Bacon.Id, Ponto.Id, SemCebola.Id]);

        Assert.Equal(RecusaDeEscolha.Nenhuma, r.Recusa);
        Assert.Equal([(Adicionais.Id, Bacon.Id), (PontoDaCarne.Id, Ponto.Id), (Retiradas.Id, SemCebola.Id)], r.Escolhidas.Select(e => (e.GrupoId, e.Opcao.Id)).ToArray());
    }

    [Fact]
    public void A_required_group_without_a_choice_is_refused()
    {
        Assert.Equal(RecusaDeEscolha.AbaixoDoMinimo, RegrasDeOpcoes.Escolher(Todos, [Bacon.Id]).Recusa);
        Assert.Equal(RecusaDeEscolha.AbaixoDoMinimo, RegrasDeOpcoes.Escolher(Todos, []).Recusa);
    }

    [Fact]
    public void More_choices_than_the_group_maximum_are_refused()
    {
        Assert.Equal(RecusaDeEscolha.AcimaDoMaximo, RegrasDeOpcoes.Escolher(Todos, [Mal.Id, Ponto.Id]).Recusa);
        Assert.Equal(RecusaDeEscolha.Nenhuma, RegrasDeOpcoes.Escolher(Todos, [Bacon.Id, Queijo.Id, Ponto.Id]).Recusa);
    }

    [Fact]
    public void An_option_of_another_product_or_unknown_is_refused()
    {
        var estranha = new Opcao(Guid.CreateVersion7(), "Outra", 100, true);
        Assert.Equal(RecusaDeEscolha.OpcaoDesconhecida, RegrasDeOpcoes.Escolher(Todos, [Ponto.Id, estranha.Id]).Recusa);
        Assert.Equal(RecusaDeEscolha.OpcaoDesconhecida, RegrasDeOpcoes.Escolher([PontoDaCarne], [Ponto.Id, Bacon.Id]).Recusa);
    }

    [Fact]
    public void An_inactive_option_is_refused_and_an_option_chosen_twice_too()
    {
        Assert.Equal(RecusaDeEscolha.OpcaoInativa, RegrasDeOpcoes.Escolher(Todos, [Ponto.Id, Acabou.Id]).Recusa);
        Assert.Equal(RecusaDeEscolha.OpcaoRepetida, RegrasDeOpcoes.Escolher(Todos, [Ponto.Id, Bacon.Id, Bacon.Id]).Recusa);
    }

    [Fact]
    public void Options_of_an_inactive_group_are_refused_and_the_group_requires_nothing()
    {
        var desligado = PontoDaCarne with { Ativo = false };
        Assert.Equal(RecusaDeEscolha.Nenhuma, RegrasDeOpcoes.Escolher([desligado, Adicionais], []).Recusa);
        Assert.Equal(RecusaDeEscolha.OpcaoInativa, RegrasDeOpcoes.Escolher([desligado], [Ponto.Id]).Recusa);
    }

    [Fact]
    public void A_required_minimum_never_exceeds_the_options_still_active()
    {
        var quaseTudoAcabou = new GrupoDeOpcao(Guid.CreateVersion7(), "Molho", 2, 2, true, 1, [Queijo, Acabou]);
        Assert.Equal(RecusaDeEscolha.Nenhuma, RegrasDeOpcoes.Escolher([quaseTudoAcabou], [Queijo.Id]).Recusa);
        Assert.Equal(RecusaDeEscolha.AbaixoDoMinimo, RegrasDeOpcoes.Escolher([quaseTudoAcabou], []).Recusa);
    }

    [Fact]
    public void A_product_without_groups_takes_no_options()
    {
        Assert.Equal(RecusaDeEscolha.Nenhuma, RegrasDeOpcoes.Escolher([], []).Recusa);
        Assert.Equal(RecusaDeEscolha.OpcaoDesconhecida, RegrasDeOpcoes.Escolher([], [Bacon.Id]).Recusa);
    }

    [Theory]
    [InlineData("Adicionais", 0, 2, true)]
    [InlineData("Ponto", 1, 1, true)]
    [InlineData("Ponto", 2, 1, false)]
    [InlineData("Ponto", -1, 1, false)]
    [InlineData("Ponto", 0, 0, false)]
    [InlineData("  ", 0, 1, false)]
    [InlineData("Muitos", 0, 51, false)]
    public void A_group_needs_a_name_and_min_not_above_max(string nome, int minimo, int maximo, bool valido) =>
        Assert.Equal(valido, RegrasDeOpcoes.NomeDoGrupo(nome) is not null && RegrasDeOpcoes.LimitesValidos(minimo, maximo));

    [Theory]
    [InlineData("Bacon", 500, true)]
    [InlineData("Sem cebola", 0, true)]
    [InlineData("Bacon", -1, false)]
    [InlineData("", 100, false)]
    public void An_option_needs_a_name_and_a_price_from_zero(string nome, long preco, bool valido) =>
        Assert.Equal(valido, RegrasDeOpcoes.NomeDaOpcao(nome) is not null && RegrasDoCardapio.PrecoValido(preco));

    [Fact]
    public void The_item_total_is_price_plus_options_times_quantity_in_cents()
    {
        var item = new ItemDaComanda(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, Guid.CreateVersion7(), "X-Burger", 2500, 3, null, EstadoDoItem.Lancado, null, null, null,
            [new OpcaoDoItem(Guid.CreateVersion7(), Guid.CreateVersion7(), "Bacon", 500), new OpcaoDoItem(Guid.CreateVersion7(), Guid.CreateVersion7(), "Sem cebola", 0)]);

        Assert.Equal(3000, item.PrecoUnitarioCentavos);
        Assert.Equal(9000, item.TotalCentavos);
        Assert.Equal(9000, RegrasDeRodada.Consumo([item]));
        Assert.Equal(0, RegrasDeRodada.Consumo([item with { Estado = EstadoDoItem.Cancelado }]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void A_group_is_required_from_a_minimum_of_one(int minimo, bool obrigatorio) =>
        Assert.Equal(obrigatorio, (Adicionais with { Minimo = minimo }).Obrigatorio);

    [Theory]
    [InlineData(1, 50, true)]
    [InlineData(1, 51, false)]
    [InlineData(50, 50, true)]
    public void The_maximum_of_choices_is_capped_at_fifty(int minimo, int maximo, bool valido) =>
        Assert.Equal(valido, RegrasDeOpcoes.LimitesValidos(minimo, maximo));

    [Fact]
    public void Names_take_up_to_eighty_characters_and_no_control_characters()
    {
        Assert.Equal(new string('a', 80), RegrasDeOpcoes.NomeDoGrupo(new string('a', 80)));
        Assert.Null(RegrasDeOpcoes.NomeDoGrupo(new string('a', 81)));
        Assert.Null(RegrasDeOpcoes.NomeDaOpcao("Bacon\tcrocante"));
        Assert.Equal("Bacon", RegrasDeOpcoes.NomeDaOpcao("  Bacon "));
    }
}
