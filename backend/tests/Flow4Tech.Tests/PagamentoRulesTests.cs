using Flow4Tech.Comandas.Domain;
using Flow4Tech.Pagamentos.Domain;

namespace Flow4Tech.Tests;

/// <summary>Dominio do salao, ticket 07: the pure rules of the bill, the service fee, change and the close rule (no I/O, no database).</summary>
public sealed class PagamentoRulesTests
{
    [Theory]
    [InlineData(0L, 10, 0L)]
    [InlineData(1000L, 10, 100L)]
    [InlineData(1005L, 10, 101L)]
    [InlineData(1004L, 10, 100L)]
    [InlineData(1006L, 10, 101L)]
    [InlineData(5L, 10, 1L)]
    [InlineData(4L, 10, 0L)]
    [InlineData(2500L, 12.5, 313L)]
    [InlineData(1000L, 0, 0L)]
    [InlineData(1000L, 20, 200L)]
    [InlineData(-300L, 10, 0L)]
    public void The_fee_is_a_percentage_of_the_consumption_rounded_half_up_in_cents(long consumo, double percentual, long taxa) =>
        Assert.Equal(taxa, RegrasDeComanda.TaxaDeServico(consumo, (decimal)percentual));

    [Fact]
    public void The_bill_adds_the_fee_unless_it_was_removed_and_the_balance_is_the_total_minus_what_was_paid()
    {
        var cheia = RegrasDeComanda.Conta(2500, 10m, false, 1000);
        var sem = RegrasDeComanda.Conta(2500, 10m, true, 1000);

        Assert.Equal((2500L, 250L, 2750L, 1000L, 1750L, false), (cheia.ConsumoCentavos, cheia.TaxaServicoCentavos, cheia.TotalCentavos, cheia.PagoCentavos, cheia.SaldoCentavos, cheia.TaxaServicoRemovida));
        Assert.Equal((0L, 2500L, 1500L, true), (sem.TaxaServicoCentavos, sem.TotalCentavos, sem.SaldoCentavos, sem.TaxaServicoRemovida));
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(1L, false)]
    [InlineData(-1L, false)]
    public void A_comanda_closes_only_at_balance_zero(long saldo, bool pode) =>
        Assert.Equal(pode, RegrasDeComanda.PodeFechar(new ContaDaComanda(0, 0, 0, 0, saldo, false, 10m)));

    [Theory]
    [InlineData(1000L, 1750L, 1000L, 0L)]
    [InlineData(2000L, 1750L, 1750L, 250L)]
    [InlineData(1750L, 1750L, 1750L, 0L)]
    [InlineData(500L, 0L, 0L, 500L)]
    [InlineData(500L, -100L, 0L, 500L)]
    public void The_applied_cash_never_exceeds_the_balance_and_the_change_is_the_rest(long recebido, long saldo, long aplicado, long troco)
    {
        var a = RegrasDePagamento.Aplicado(recebido, saldo);

        Assert.Equal(aplicado, a);
        Assert.Equal(troco, RegrasDePagamento.Troco(recebido, a));
        Assert.Equal(recebido, a + RegrasDePagamento.Troco(recebido, a));
    }

    [Fact]
    public void The_change_is_never_negative()
    {
        Assert.Equal(0L, RegrasDePagamento.Troco(100, 500));
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(1L, true)]
    [InlineData(100_000_000L, true)]
    [InlineData(100_000_001L, false)]
    public void The_cash_received_is_at_least_one_cent_up_to_the_typo_guard(long recebido, bool valido) =>
        Assert.Equal(valido, RegrasDePagamento.RecebidoValido(recebido));
}
