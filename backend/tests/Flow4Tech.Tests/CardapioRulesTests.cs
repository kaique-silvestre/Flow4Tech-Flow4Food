using Flow4Tech.Cardapio.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0048: pure price rules of the complete menu (unit override, promotion window, launchable). No I/O; the clock is a plain instant.</summary>
public sealed class CardapioRulesTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Fim = new(2026, 11, 8, 22, 0, 0, TimeSpan.Zero);
    private static readonly Promocao Happy = new(800, Inicio, Fim);

    [Fact]
    public void Without_promotion_or_override_the_base_price_is_charged() =>
        Assert.Equal(1000, RegrasDePreco.Efetivo(1000, null, null, Inicio));

    [Theory]
    [InlineData(-1, 1000)]
    [InlineData(0, 800)]
    [InlineData(1, 800)]
    [InlineData(3_600_000 * 4 - 1, 800)]
    [InlineData(3_600_000 * 4, 1000)]
    [InlineData(3_600_000 * 4 + 1, 1000)]
    public void The_promotion_is_valid_from_its_start_inclusive_to_its_end_exclusive(long millisFromStart, long expected) =>
        Assert.Equal(expected, RegrasDePreco.Efetivo(1000, Happy, null, Inicio.AddMilliseconds(millisFromStart)));

    [Fact]
    public void The_window_is_compared_as_an_instant_not_as_a_local_clock_reading()
    {
        var local = new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.FromHours(-3)); // 18:00 UTC
        Assert.Equal(800, RegrasDePreco.Efetivo(1000, Happy, null, local));
        Assert.Equal(1000, RegrasDePreco.Efetivo(1000, Happy, null, local.AddTicks(-1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    public void A_unit_override_beats_the_base_price_and_a_valid_promotion(long overrideCents)
    {
        Assert.Equal(overrideCents, RegrasDePreco.Efetivo(1000, null, overrideCents, Inicio));
        Assert.Equal(overrideCents, RegrasDePreco.Efetivo(1000, Happy, overrideCents, Inicio.AddHours(1)));
        Assert.Equal(overrideCents, RegrasDePreco.Efetivo(1000, Happy, overrideCents, Fim.AddHours(1)));
    }

    [Theory]
    [InlineData(799, true)]
    [InlineData(0, true)]
    [InlineData(1000, false)]
    [InlineData(1001, false)]
    [InlineData(-1, false)]
    public void A_promotion_must_be_a_valid_price_strictly_below_the_base(long promo, bool ok) =>
        Assert.Equal(ok, RegrasDePreco.PromocaoValida(new Promocao(promo, Inicio, Fim), 1000));

    [Fact]
    public void A_promotion_needs_a_non_empty_window_and_no_promotion_is_always_fine()
    {
        Assert.True(RegrasDePreco.PromocaoValida(null, 1000));
        Assert.False(RegrasDePreco.PromocaoValida(new Promocao(500, Inicio, Inicio), 1000));
        Assert.False(RegrasDePreco.PromocaoValida(new Promocao(500, Fim, Inicio), 1000));
        Assert.True(RegrasDePreco.PromocaoValida(new Promocao(500, Inicio, Inicio.AddTicks(1)), 1000));
        Assert.False(RegrasDePreco.PromocaoValida(new Promocao(RegrasDoCardapio.PrecoMaximoCentavos + 1, Inicio, Fim), RegrasDoCardapio.PrecoMaximoCentavos + 2));
    }

    [Theory]
    [InlineData(1000L, 1000L, 1000L, 0)]
    [InlineData(1000L, 1050L, 1000L, 5)]
    [InlineData(1000L, 900L, 1000L, 10)]
    [InlineData(1000L, 2000L, 1000L, 100)]
    [InlineData(0L, 100L, 0L, 100)]
    [InlineData(200L, 250L, 1000L, 25)]
    public void The_percent_of_a_change_is_measured_against_the_previous_price(long before, long after, long reference, int percent) =>
        Assert.Equal((decimal)percent, RegrasDePreco.PercentualDaMudanca(before, after, reference));

    [Fact]
    public void An_absent_previous_price_is_compared_with_the_reference_and_an_absent_new_one_returns_to_it()
    {
        Assert.Equal(20m, RegrasDePreco.PercentualDaMudanca(null, 800, 1000));
        Assert.Equal(25m, RegrasDePreco.PercentualDaMudanca(800, null, 1000));
        Assert.Equal(0m, RegrasDePreco.PercentualDaMudanca(null, null, 1000));
        Assert.Equal(0m, RegrasDePreco.PercentualDaMudanca(null, 1000, 1000));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void A_product_can_be_launched_only_when_active_and_not_acabou(bool ativo, bool indisponivel, bool launchable)
    {
        var produto = new Produto(Guid.NewGuid(), Guid.NewGuid(), "X", "X", 1000, "BRL", ativo, 1, indisponivel);
        Assert.Equal(launchable, produto.Lancavel);
    }

    [Fact]
    public void A_product_charges_the_effective_price_of_the_unit_it_was_read_for()
    {
        var produto = new Produto(Guid.NewGuid(), Guid.NewGuid(), "X", "X", 1000, "BRL", true, 1, false, Happy);
        Assert.Equal(800, produto.PrecoEfetivo(Inicio));
        Assert.Equal(1000, produto.PrecoEfetivo(Fim));
        Assert.Equal(650, (produto with { PrecoDaUnidadeCentavos = 650 }).PrecoEfetivo(Fim));
    }
}
