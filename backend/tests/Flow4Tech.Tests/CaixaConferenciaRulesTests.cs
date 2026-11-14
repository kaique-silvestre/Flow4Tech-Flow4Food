using Flow4Tech.Caixa.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0065: pure rules of supply, withdrawal, the per-method count, the tolerance and the approval (no I/O).</summary>
public sealed class CaixaConferenciaRulesTests
{
    [Fact]
    public void Expected_cash_is_float_plus_cash_received_plus_supplies_minus_withdrawals()
    {
        Assert.Equal(20_000L, RegrasDoCaixa.Esperado(20_000, 0, 0, 0));
        Assert.Equal(35_050L + 1_000 - 2_500, RegrasDoCaixa.Esperado(20_000, 15_050, 1_000, 2_500));
        Assert.Equal(35_050L, RegrasDoCaixa.Esperado(20_000, 15_050));
    }

    [Theory]
    [InlineData(1L, true)]
    [InlineData(RegrasDoCaixa.ValorMaximoCentavos, true)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(RegrasDoCaixa.ValorMaximoCentavos + 1, false)]
    public void A_movement_is_at_least_one_cent_up_to_the_typo_guard(long cents, bool valid) =>
        Assert.Equal(valid, RegrasDoCaixa.MovimentoValido(cents));

    [Theory]
    [InlineData(null, 200, null, false)]
    [InlineData("", 200, null, false)]
    [InlineData("   ", 200, null, false)]
    [InlineData("  troco  ", 200, "troco", true)]
    public void A_reason_is_trimmed_and_required(string? text, int max, string? expected, bool valid)
    {
        Assert.Equal(valid, RegrasDoCaixa.TextoValido(text, max, out var clean));
        Assert.Equal(expected, clean);
    }

    [Fact]
    public void A_reason_has_a_length_cap()
    {
        Assert.True(RegrasDoCaixa.TextoValido(new string('a', 200), 200, out _));
        Assert.False(RegrasDoCaixa.TextoValido(new string('a', 201), 200, out _));
        Assert.True(RegrasDoCaixa.TextoValido(new string('a', 80), RegrasDoCaixa.DestinoMaximo, out _));
        Assert.False(RegrasDoCaixa.TextoValido(new string('a', 81), RegrasDoCaixa.DestinoMaximo, out _));
    }

    [Theory]
    [InlineData(1_000L, 1_000L, true)]
    [InlineData(1_000L, 1_001L, false)]
    [InlineData(0L, 1L, false)]
    public void A_withdrawal_cannot_take_more_than_the_drawer_holds(long balance, long value, bool allowed) =>
        Assert.Equal(allowed, RegrasDoCaixa.PodeSangrar(balance, value));

    [Fact]
    public void The_total_difference_is_the_sum_of_absolute_differences_of_cash_and_methods()
    {
        Assert.Equal(0L, RegrasDoCaixa.DiferencaTotal(0, []));
        Assert.Equal(300L, RegrasDoCaixa.DiferencaTotal(-300, []));
        Assert.Equal(300L + 200 + 50, RegrasDoCaixa.DiferencaTotal(-300, [200, -50]));
    }

    [Theory]
    [InlineData(0L, 500L, SituacaoDaConferencia.Concluida)]
    [InlineData(500L, 500L, SituacaoDaConferencia.Concluida)]
    [InlineData(501L, 500L, SituacaoDaConferencia.Pendente)]
    [InlineData(1L, 0L, SituacaoDaConferencia.Pendente)]
    [InlineData(0L, 0L, SituacaoDaConferencia.Concluida)]
    public void Inside_the_tolerance_the_count_concludes_alone_above_it_waits_for_approval(long total, long tolerance, SituacaoDaConferencia expected) =>
        Assert.Equal(expected, RegrasDoCaixa.Situacao(total, tolerance));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void The_person_who_closed_cannot_approve_unless_self_approval_is_on(bool sameperson, bool selfApproval, bool allowed) =>
        Assert.Equal(allowed, RegrasDoCaixa.PodeAprovar(sameperson, selfApproval));

    [Fact]
    public void A_method_count_difference_is_counted_minus_expected()
    {
        var count = new ContagemDoMetodo(Guid.NewGuid(), "Pix", 900, 1_000);
        Assert.Equal(-100L, count.DiferencaCentavos);
    }
}
