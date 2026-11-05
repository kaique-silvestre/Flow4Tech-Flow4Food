using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>Dominio do salao, ticket 04: pure rules of the tables (no I/O).</summary>
public sealed class MesasRulesTests
{
    [Theory]
    [InlineData("12", "12")]
    [InlineData("  Varanda 3  ", "Varanda 3")]
    [InlineData("Mesa  1", "Mesa 1")]
    public void An_identification_is_trimmed_and_inner_spaces_collapsed(string input, string expected) =>
        Assert.Equal(expected, RegrasDeMesa.Identificacao(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\tb")]
    public void A_missing_blank_or_control_identification_is_refused(string? input) =>
        Assert.Null(RegrasDeMesa.Identificacao(input));

    [Fact]
    public void An_identification_longer_than_the_limit_is_refused_and_exactly_the_limit_is_kept()
    {
        Assert.Null(RegrasDeMesa.Identificacao(new string('x', RegrasDeMesa.IdentificacaoMaxima + 1)));
        Assert.NotNull(RegrasDeMesa.Identificacao(new string('x', RegrasDeMesa.IdentificacaoMaxima)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(RegrasDeMesa.CapacidadeMaxima, true)]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    [InlineData(RegrasDeMesa.CapacidadeMaxima + 1, false)]
    public void Capacity_is_optional_but_when_given_is_between_one_and_the_limit(int? capacity, bool valid) =>
        Assert.Equal(valid, RegrasDeMesa.CapacidadeValida(capacity));
}
