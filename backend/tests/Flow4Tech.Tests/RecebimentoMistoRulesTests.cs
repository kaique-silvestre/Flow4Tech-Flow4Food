using Flow4Tech.Pagamentos.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0063: the pure rules of a partial, split or mixed receipt and of the payment methods (no I/O, no database).</summary>
public sealed class RecebimentoMistoRulesTests
{
    private static LinhaDeRecebimento Dinheiro(long recebido, long gorjeta = 0) => new(true, recebido, gorjeta);

    private static LinhaDeRecebimento Cartao(long recebido, long gorjeta = 0) => new(false, recebido, gorjeta);

    [Fact]
    public void One_cash_line_below_the_balance_is_a_partial_payment_without_change()
    {
        var r = RegrasDeRecebimentoMisto.Ratear(1000, [Dinheiro(400)]);

        Assert.Equal(RecusaDeRecebimento.Nenhuma, r.Recusa);
        Assert.Equal(new LinhaRateada(400, 400, 0, 0), r.Linhas.Single());
    }

    [Fact]
    public void Cash_above_the_balance_applies_the_balance_and_gives_the_rest_back_as_change()
    {
        var r = RegrasDeRecebimentoMisto.Ratear(1000, [Dinheiro(1500)]);

        Assert.Equal(new LinhaRateada(1500, 1000, 500, 0), r.Linhas.Single());
    }

    [Fact]
    public void A_mixed_receipt_applies_the_other_methods_in_full_and_cash_takes_what_is_left_with_the_change()
    {
        var r = RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(300), Cartao(200), Dinheiro(1000)]);

        Assert.Equal(RecusaDeRecebimento.Nenhuma, r.Recusa);
        Assert.Equal([300L, 200L, 500L], r.Linhas.Select(l => l.AplicadoCentavos));
        Assert.Equal([0L, 0L, 500L], r.Linhas.Select(l => l.TrocoCentavos));
    }

    [Fact]
    public void The_order_of_the_lines_does_not_change_what_each_method_applies()
    {
        var cashFirst = RegrasDeRecebimentoMisto.Ratear(1000, [Dinheiro(1000), Cartao(300)]);
        var cardFirst = RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(300), Dinheiro(1000)]);

        Assert.Equal([700L, 300L], cashFirst.Linhas.Select(l => l.AplicadoCentavos));
        Assert.Equal([300L, 700L], cardFirst.Linhas.Select(l => l.AplicadoCentavos));
        Assert.Equal([300L, 0L], cashFirst.Linhas.Select(l => l.TrocoCentavos));
        Assert.Equal([0L, 300L], cardFirst.Linhas.Select(l => l.TrocoCentavos));
    }

    [Fact]
    public void Change_exists_only_for_cash_so_other_methods_alone_cannot_pass_the_balance()
    {
        Assert.Equal(RecusaDeRecebimento.ExcedeSaldo, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(1001)]).Recusa);
        Assert.Equal(RecusaDeRecebimento.ExcedeSaldo, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(600), Cartao(401)]).Recusa);
        Assert.Equal(RecusaDeRecebimento.Nenhuma, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(600), Cartao(400)]).Recusa);
    }

    [Fact]
    public void A_cash_line_with_nothing_left_to_apply_is_refused_instead_of_being_all_change()
    {
        Assert.Equal(RecusaDeRecebimento.ExcedeSaldo, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(1000), Dinheiro(500)]).Recusa);
        Assert.Equal(RecusaDeRecebimento.ExcedeSaldo, RegrasDeRecebimentoMisto.Ratear(1000, [Dinheiro(1000), Dinheiro(500)]).Recusa);
    }

    [Fact]
    public void The_tip_is_carried_on_the_line_and_never_reduces_the_balance_nor_the_applied_cents()
    {
        var com = RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(400, 90), Dinheiro(700, 10)]);
        var sem = RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(400), Dinheiro(700)]);

        Assert.Equal([90L, 10L], com.Linhas.Select(l => l.GorjetaCentavos));
        Assert.Equal(sem.Linhas.Select(l => (l.AplicadoCentavos, l.TrocoCentavos)), com.Linhas.Select(l => (l.AplicadoCentavos, l.TrocoCentavos)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void No_balance_means_nothing_to_receive(long saldo) => Assert.Equal(RecusaDeRecebimento.Invalido, RegrasDeRecebimentoMisto.Ratear(saldo, [Dinheiro(100)]).Recusa);

    [Fact]
    public void Lines_are_between_one_and_ten_each_with_a_positive_amount_and_a_tip_in_range()
    {
        Assert.Equal(RecusaDeRecebimento.Invalido, RegrasDeRecebimentoMisto.Ratear(1000, []).Recusa);
        Assert.Equal(RecusaDeRecebimento.Nenhuma, RegrasDeRecebimentoMisto.Ratear(1000, Enumerable.Repeat(Cartao(10), RegrasDeRecebimentoMisto.MaximoDeLinhas).ToList()).Recusa);
        Assert.Equal(RecusaDeRecebimento.Invalido, RegrasDeRecebimentoMisto.Ratear(1000, Enumerable.Repeat(Cartao(10), RegrasDeRecebimentoMisto.MaximoDeLinhas + 1).ToList()).Recusa);
        Assert.Equal(RecusaDeRecebimento.Invalido, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(0)]).Recusa);
        Assert.Equal(RecusaDeRecebimento.Invalido, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(-1)]).Recusa);
        Assert.Equal(RecusaDeRecebimento.Invalido, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(RegrasDePagamento.ValorMaximoCentavos + 1)]).Recusa);
        Assert.Equal(RecusaDeRecebimento.Invalido, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(10, -1)]).Recusa);
        Assert.Equal(RecusaDeRecebimento.Invalido, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(10, RegrasDePagamento.ValorMaximoCentavos + 1)]).Recusa);
        Assert.Equal(RecusaDeRecebimento.Nenhuma, RegrasDeRecebimentoMisto.Ratear(1000, [Cartao(10, RegrasDePagamento.ValorMaximoCentavos)]).Recusa);
    }

    [Fact]
    public void The_factory_has_cash_credit_debit_pix_and_meal_voucher_active_and_voucher_and_credit_account_inactive()
    {
        var fabrica = RegrasDeMetodo.Fabrica;

        Assert.Equal(["dinheiro", "credito", "debito", "pix", "vale-refeicao", "vale", "conta-credito"], fabrica.Select(f => f.Chave));
        Assert.Equal([true, true, true, true, true, false, false], fabrica.Select(f => f.AtivoNoInicio));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], fabrica.Select(f => f.Ordem));
        Assert.Equal(["dinheiro"], fabrica.Where(f => f.Tipo == TipoDeMetodo.Dinheiro).Select(f => f.Chave));
    }

    [Theory]
    [InlineData("vale", false)]
    [InlineData("conta-credito", false)]
    [InlineData("pix", true)]
    [InlineData("dinheiro", true)]
    [InlineData(null, true)]
    public void Voucher_and_credit_account_wait_for_phase_2B(string? chave, bool liberado) => Assert.Equal(liberado, RegrasDeMetodo.LiberadoNestaFase(chave));

    [Theory]
    [InlineData("Pix", "Pix")]
    [InlineData("  Cheque  ", "Cheque")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("tab\tname", null)]
    public void A_method_name_is_trimmed_and_not_empty_nor_with_control_characters(string? nome, string? esperado) => Assert.Equal(esperado, RegrasDeMetodo.NomeValido(nome));

    [Fact]
    public void A_method_name_has_at_most_forty_characters()
    {
        Assert.NotNull(RegrasDeMetodo.NomeValido(new string('a', RegrasDeMetodo.NomeMaximo)));
        Assert.Null(RegrasDeMetodo.NomeValido(new string('a', RegrasDeMetodo.NomeMaximo + 1)));
    }

    [Fact]
    public void The_key_stored_on_a_payment_is_the_factory_key_or_personalizado()
    {
        Assert.Equal("pix", RegrasDeMetodo.ChaveDoPagamento("pix"));
        Assert.Equal("personalizado", RegrasDeMetodo.ChaveDoPagamento(null));
    }
}
