using Flow4Tech.Comandas.Domain;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Flow4Tech.Tests;

/// <summary>F4-0058: pure rules of courtesy, gift and discounts (ADR-018). No I/O.</summary>
public sealed class DescontoRulesTests
{
    private static readonly Guid Comanda = Guid.NewGuid();
    private static readonly DateTimeOffset Agora = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);

    private static ItemDaComanda Item(long preco, int quantidade, EstadoDoItem estado = EstadoDoItem.Lancado, TipoDoItem tipo = TipoDoItem.Normal, long opcao = 0) =>
        new(Guid.NewGuid(), Comanda, Guid.NewGuid(), 1, Guid.NewGuid(), "Chopp", preco, quantidade, null, estado, estado == EstadoDoItem.Cancelado ? "x" : null, estado == EstadoDoItem.Cancelado ? Guid.NewGuid() : null, estado == EstadoDoItem.Cancelado ? Agora : null, opcao == 0 ? [] : [new OpcaoDoItem(Guid.NewGuid(), Guid.NewGuid(), "Gelo", opcao)], tipo, tipo == TipoDoItem.Normal ? null : "motivo");

    private static Desconto De(Guid? item, long aplicado) =>
        new(Guid.NewGuid(), Comanda, item, FormaDoDesconto.Valor, null, aplicado, aplicado, "motivo", Guid.NewGuid(), Agora);

    [Theory]
    [InlineData(1000, 10, 100)]
    [InlineData(1005, 10, 101)] // 100.5 goes up
    [InlineData(1004, 10, 100)] // 100.4 goes down
    [InlineData(333, 50, 167)] // 166.5 goes up
    [InlineData(1, 50, 1)] // 0.5 goes up
    [InlineData(1, 49.99, 0)]
    [InlineData(2500, 100, 2500)]
    [InlineData(0, 30, 0)]
    public void A_percent_becomes_cents_half_up_in_one_function(long baseCentavos, decimal percentual, long esperado) =>
        Assert.Equal(esperado, RegrasDeDesconto.DoPercentual(baseCentavos, percentual));

    [Theory]
    [InlineData(0.01, true)]
    [InlineData(10, true)]
    [InlineData(100, true)]
    [InlineData(12.5, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(100.01, false)]
    [InlineData(10.005, false)]
    public void A_percent_is_above_zero_up_to_100_with_two_decimals(decimal percentual, bool valido) =>
        Assert.Equal(valido, RegrasDeDesconto.PercentualValido(percentual));

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public void A_value_is_at_least_one_cent(long centavos, bool valido) => Assert.Equal(valido, RegrasDeDesconto.ValorValido(centavos));

    [Fact]
    public void The_share_of_a_value_is_exact_and_zero_without_a_base()
    {
        Assert.Equal(25m, RegrasDeDesconto.PercentualDoValor(250, 1000));
        Assert.Equal(0m, RegrasDeDesconto.PercentualDoValor(250, 0));
        Assert.Equal(100m, RegrasDeDesconto.PercentualDoValor(1000, 1000));
    }

    [Fact]
    public void Item_discounts_come_off_their_line_then_comanda_discounts_off_what_is_left()
    {
        var a = Item(1000, 2); // 2000
        var b = Item(500, 1); // 500
        var descontos = new[] { De(a.Id, 300), De(null, 400), De(b.Id, 100) };

        var r = RegrasDeDesconto.Calcular([a, b], descontos);

        Assert.Equal((400L, 400L, 800L), (r.DeItensCentavos, r.DaComandaCentavos, r.TotalCentavos));
    }

    [Fact]
    public void A_discount_never_takes_more_than_its_line_nor_more_than_what_is_left_of_the_consumption()
    {
        var a = Item(1000, 1);
        var r = RegrasDeDesconto.Calcular([a], [De(a.Id, 700), De(a.Id, 700), De(null, 5000)]);

        Assert.Equal((1000L, 0L), (r.DeItensCentavos, r.DaComandaCentavos));
    }

    [Fact]
    public void Cancelled_items_and_courtesies_take_no_discount_and_leave_the_consumption()
    {
        var cancelado = Item(1000, 1, EstadoDoItem.Cancelado);
        var cortesia = Item(1000, 1, tipo: TipoDoItem.Cortesia);
        var normal = Item(1000, 1);

        var r = RegrasDeDesconto.Calcular([cancelado, cortesia, normal], [De(cancelado.Id, 500), De(cortesia.Id, 500), De(null, 300)]);

        Assert.Equal((0L, 300L), (r.DeItensCentavos, r.DaComandaCentavos));
        Assert.Equal(1000L, RegrasDeRodada.Consumo([cancelado, cortesia, normal]));
    }

    [Fact]
    public void A_courtesy_or_a_gift_charges_zero_but_keeps_the_original_value()
    {
        var item = Item(1000, 3, tipo: TipoDoItem.Brinde, opcao: 100);

        Assert.Equal((0L, 3300L, 1100L), (item.TotalCentavos, item.ValorDeTabelaCentavos, item.PrecoUnitarioCentavos));
    }

    [Fact]
    public void The_base_of_an_item_and_of_the_comanda_shrink_with_the_discounts_already_given()
    {
        var a = Item(1000, 1);
        var b = Item(1000, 1);
        var descontos = new[] { De(a.Id, 250), De(null, 500) };

        Assert.Equal(750L, RegrasDeDesconto.BaseDoItem(a, descontos));
        Assert.Equal(1000L, RegrasDeDesconto.BaseDoItem(b, descontos));
        Assert.Equal(1250L, RegrasDeDesconto.BaseDaComanda([a, b], descontos));
    }

    [Fact]
    public void The_service_fee_is_charged_on_the_discounted_consumption_and_the_order_is_fixed()
    {
        // consumption 10,00; discount 2,00 -> 8,00; fee 10% = 0,80; total 8,80.
        var conta = RegrasDeComanda.Conta(1000, 10m, false, 0, 200);

        Assert.Equal((1000L, 200L, 80L, 880L, 880L), (conta.ConsumoCentavos, conta.DescontoCentavos, conta.TaxaServicoCentavos, conta.TotalCentavos, conta.SaldoCentavos));
    }

    [Fact]
    public void A_discount_larger_than_the_consumption_leaves_a_zero_bill_never_a_negative_one()
    {
        var conta = RegrasDeComanda.Conta(1000, 10m, false, 0, 5000);

        Assert.Equal((0L, 0L, 1000L), (conta.TotalCentavos, conta.TaxaServicoCentavos, conta.DescontoCentavos));
    }

    [Theory]
    [InlineData("normal", TipoDoItem.Normal)]
    [InlineData("cortesia", TipoDoItem.Cortesia)]
    [InlineData("brinde", TipoDoItem.Brinde)]
    public void An_item_type_has_one_stored_word_and_back(string palavra, TipoDoItem tipo)
    {
        Assert.Equal(palavra, RegrasDeDesconto.Palavra(tipo));
        Assert.Equal(tipo, RegrasDeDesconto.Tipo(palavra));
    }

    [Fact]
    public void An_unknown_stored_type_or_form_is_not_guessed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RegrasDeDesconto.Tipo("outro"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegrasDeDesconto.Forma("outro"));
    }
}

public readonly record struct Linha(long Preco, int Quantidade, bool Cancelada, bool Cortesia);

public readonly record struct DescontoGerado(int Alvo, long Valor);

/// <summary>Generators for the discount properties.</summary>
public static class DescontoGeradores
{
    public static Arbitrary<Linha> Linha() =>
        Gen.Choose(0, 50_000).SelectMany(p => Gen.Choose(1, 20).SelectMany(q => Gen.Choose(0, 9).Select(k => new Linha(p, q, k == 0, k == 1)))).ToArbitrary();

    public static Arbitrary<DescontoGerado> Desconto() => Gen.Choose(-1, 5).SelectMany(a => Gen.Choose(1, 2_000_000).Select(v => new DescontoGerado(a, v))).ToArbitrary();
}

/// <summary>Properties of the bill with discounts (F4-0058): whatever is given, the total is never negative and the order is the same.</summary>
public sealed class DescontoPropertyTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);

    private static (List<ItemDaComanda> Itens, List<Desconto> Descontos) Montar(Linha[] linhas, DescontoGerado[] gerados)
    {
        var comanda = Guid.NewGuid();
        var itens = linhas
            .Select(l => new ItemDaComanda(Guid.NewGuid(), comanda, Guid.NewGuid(), 1, Guid.NewGuid(), "P", l.Preco, l.Quantidade, null, l.Cancelada ? EstadoDoItem.Cancelado : EstadoDoItem.Lancado, l.Cancelada ? "x" : null, l.Cancelada ? Guid.NewGuid() : null, l.Cancelada ? Agora : null, null, l.Cortesia ? TipoDoItem.Cortesia : TipoDoItem.Normal, l.Cortesia ? "m" : null))
            .ToList();
        var descontos = gerados
            .Select(g => new Desconto(Guid.NewGuid(), comanda, g.Alvo < 0 || itens.Count == 0 ? null : itens[g.Alvo % itens.Count].Id, FormaDoDesconto.Valor, null, g.Valor, g.Valor, "m", Guid.NewGuid(), Agora))
            .ToList();
        return (itens, descontos);
    }

    [Property(Arbitrary = [typeof(DescontoGeradores), typeof(SalaoGeradores)])]
    public bool Discounts_never_exceed_the_consumption_and_the_total_is_never_negative(Linha[] linhas, DescontoGerado[] gerados, Percentual taxa)
    {
        var (itens, descontos) = Montar(linhas, gerados);
        var consumo = RegrasDeRodada.Consumo(itens);

        var tirado = RegrasDeDesconto.Calcular(itens, descontos);
        var conta = RegrasDeComanda.Conta(consumo, taxa.Value, false, 0, tirado.TotalCentavos);

        return tirado.DeItensCentavos >= 0 && tirado.DaComandaCentavos >= 0 && tirado.TotalCentavos <= consumo && conta.TotalCentavos >= 0 && conta.TaxaServicoCentavos >= 0
            && conta.TotalCentavos == consumo - tirado.TotalCentavos + conta.TaxaServicoCentavos;
    }

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool A_percent_of_a_base_never_exceeds_the_base_and_is_within_half_a_cent(Centavos baseCentavos, Percentual percentual)
    {
        var p = Math.Min(100m, percentual.Value * 5m);
        var tirado = RegrasDeDesconto.DoPercentual(baseCentavos.Value, p);
        var exato = baseCentavos.Value * p / 100m;

        return tirado >= 0 && tirado <= baseCentavos.Value && Math.Abs(tirado - exato) <= 0.5m;
    }

    [Property(Arbitrary = [typeof(DescontoGeradores)])]
    public bool The_base_left_for_a_new_discount_is_never_negative_and_matches_what_the_bill_charges(Linha[] linhas, DescontoGerado[] gerados)
    {
        var (itens, descontos) = Montar(linhas, gerados);

        var porItem = itens.All(i => RegrasDeDesconto.BaseDoItem(i, descontos) >= 0);
        var daComanda = RegrasDeDesconto.BaseDaComanda(itens, descontos);

        return porItem && daComanda >= 0 && daComanda == RegrasDeRodada.Consumo(itens) - RegrasDeDesconto.Calcular(itens, descontos).TotalCentavos;
    }
}
