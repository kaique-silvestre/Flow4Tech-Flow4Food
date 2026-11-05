using Flow4Tech.Caixa.Domain;

namespace Flow4Tech.Tests;

/// <summary>Dominio do salao, ticket 03: pure rules of the cash shift (no I/O).</summary>
public sealed class CaixaRulesTests
{
    [Theory]
    [InlineData(0L, true)]
    [InlineData(10_000L, true)]
    [InlineData(RegrasDoCaixa.ValorMaximoCentavos, true)]
    [InlineData(RegrasDoCaixa.ValorMaximoCentavos + 1, false)]
    [InlineData(-1L, false)]
    public void A_float_or_a_count_is_zero_or_more_up_to_the_typo_guard(long cents, bool valid) =>
        Assert.Equal(valid, RegrasDoCaixa.ValorValido(cents));

    [Fact]
    public void Expected_is_the_float_plus_cash_received_and_the_float_alone_when_nothing_was_received()
    {
        Assert.Equal(20_000L, RegrasDoCaixa.Esperado(20_000, 0));
        Assert.Equal(35_050L, RegrasDoCaixa.Esperado(20_000, 15_050));
    }

    [Theory]
    [InlineData(20_000L, 20_000L, 0L, "Confere")]
    [InlineData(19_500L, 20_000L, -500L, "Falta R$ 5,00")]
    [InlineData(20_001L, 20_000L, 1L, "Sobra R$ 0,01")]
    [InlineData(0L, 123_456L, -123_456L, "Falta R$ 1.234,56")]
    [InlineData(2_500_000L, 0L, 2_500_000L, "Sobra R$ 25.000,00")]
    public void The_difference_is_counted_minus_expected_and_told_in_words(long counted, long expected, long difference, string words)
    {
        Assert.Equal(difference, RegrasDoCaixa.Diferenca(counted, expected));
        Assert.Equal(words, RegrasDoCaixa.Palavra(difference));
    }
}
