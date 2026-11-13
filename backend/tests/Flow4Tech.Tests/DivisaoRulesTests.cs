using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0061: pure rules of splitting a bill in the five ways. No I/O.</summary>
public sealed class DivisaoRulesTests
{
    private static readonly Guid Comanda = Guid.NewGuid();
    private static readonly DateTimeOffset Agora = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    private static ItemDaComanda Item(long preco, int? assento = null, int quantidade = 1, EstadoDoItem estado = EstadoDoItem.Lancado) =>
        new(Guid.NewGuid(), Comanda, Guid.NewGuid(), 1, Guid.NewGuid(), "Chopp", preco, quantidade, null, estado, estado == EstadoDoItem.Cancelado ? "x" : null, estado == EstadoDoItem.Cancelado ? Guid.NewGuid() : null, estado == EstadoDoItem.Cancelado ? Agora : null, Assento: assento);

    private static ContaDaComanda Conta(IReadOnlyList<ItemDaComanda> itens, decimal taxa = 10m) => RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens), taxa, false, 0);

    [Theory]
    [InlineData(10, 3, new long[] { 4, 3, 3 })]
    [InlineData(3300, 7, new long[] { 472, 472, 472, 471, 471, 471, 471 })]
    [InlineData(0, 3, new long[] { 0, 0, 0 })]
    [InlineData(1, 4, new long[] { 1, 0, 0, 0 })]
    [InlineData(9, 3, new long[] { 3, 3, 3 })]
    public void Equal_parts_give_the_remainder_cents_to_the_first_parts(long total, int partes, long[] esperado) =>
        Assert.Equal(esperado, RegrasDeDivisao.PartesIguais(total, partes));

    [Fact]
    public void Sharing_by_weights_floors_each_share_and_gives_the_cents_left_to_the_first_parts_with_a_weight()
    {
        Assert.Equal([1100L, 1100L, 1100L], RegrasDeDivisao.Distribuir(3300, [3333, 3333, 3334]));
        Assert.Equal([2L, 0L, 1L], RegrasDeDivisao.Distribuir(3, [1, 0, 1]));
        Assert.Equal([2L, 1L], RegrasDeDivisao.Distribuir(3, [0, 0]));
        Assert.Equal([0L, 5L], RegrasDeDivisao.Distribuir(5, [0, 1]));
        Assert.Throws<ArgumentException>(() => RegrasDeDivisao.Distribuir(10, [-1, 2]));
        Assert.Throws<ArgumentException>(() => RegrasDeDivisao.Distribuir(10, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegrasDeDivisao.Distribuir(-1, [1]));
    }

    [Fact]
    public void Equal_mode_splits_the_bill_with_the_fee_and_the_tip_inside_the_sums()
    {
        var itens = new[] { Item(1000), Item(1000), Item(1000) };
        var conta = Conta(itens);

        var r = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: 3, GorjetaCentavos: 100), itens, conta);

        Assert.Equal(RecusaDeDivisao.Nenhuma, r.Recusa);
        Assert.Equal(3300L, r.Partes.Sum(p => p.TotalCentavos));
        Assert.Equal(300L, r.Partes.Sum(p => p.TaxaServicoCentavos));
        Assert.Equal(3000L, r.Partes.Sum(p => p.ConsumoCentavos));
        Assert.Equal([34L, 33L, 33L], r.Partes.Select(p => p.GorjetaCentavos));
        Assert.Equal([1134L, 1133L, 1133L], r.Partes.Select(p => p.APagarCentavos));
        Assert.Equal([1, 2, 3], r.Partes.Select(p => p.Numero));
    }

    [Fact]
    public void Equal_mode_needs_two_to_fifty_parts()
    {
        var itens = new[] { Item(1000) };
        foreach (var n in new int?[] { null, 0, 1, 51 })
        {
            Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: n), itens, Conta(itens)).Recusa);
        }

        Assert.Equal(RecusaDeDivisao.Nenhuma, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: 2), itens, Conta(itens)).Recusa);
        Assert.Equal(RecusaDeDivisao.Nenhuma, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: 50), itens, Conta(itens)).Recusa);
    }

    [Fact]
    public void Seat_mode_makes_one_part_per_seat_and_shares_the_items_without_a_seat_equally()
    {
        var a = Item(1000, 1);
        var b = Item(1000, 2);
        var c = Item(1000);
        var itens = new[] { a, b, c };

        var r = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Assento), itens, Conta(itens));

        Assert.Equal(RecusaDeDivisao.Nenhuma, r.Recusa);
        Assert.Equal([1, 2], r.Partes.Select(p => p.Assento));
        Assert.Equal([[a.Id], [b.Id]], r.Partes.Select(p => p.ItemIds));
        Assert.Equal([1650L, 1650L], r.Partes.Select(p => p.TotalCentavos));
        Assert.Equal(3300L, r.Partes.Sum(p => p.TotalCentavos));
    }

    [Fact]
    public void Seat_mode_ignores_cancelled_items_and_needs_two_seats()
    {
        var a = Item(1000, 1);
        var cancelado = Item(5000, 2, estado: EstadoDoItem.Cancelado);
        var umSo = new[] { a, cancelado, Item(1000, 1) };
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Assento), umSo, Conta(umSo)).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Assento), [Item(1000), Item(1000)], Conta([Item(1000)])).Recusa);
    }

    [Fact]
    public void Item_mode_needs_every_launched_item_in_exactly_one_group()
    {
        var a = Item(1000);
        var b = Item(2000);
        var c = Item(500);
        var cancelado = Item(9999, estado: EstadoDoItem.Cancelado);
        var itens = new[] { a, b, c, cancelado };
        var conta = Conta(itens, 0m);

        var ok = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Item, Grupos: [[a.Id], [b.Id, c.Id]]), itens, conta);
        Assert.Equal(RecusaDeDivisao.Nenhuma, ok.Recusa);
        Assert.Equal([1000L, 2500L], ok.Partes.Select(p => p.TotalCentavos));
        Assert.Equal([[a.Id], [b.Id, c.Id]], ok.Partes.Select(p => p.ItemIds));

        Assert.Equal(RecusaDeDivisao.ItensForaDasPartes, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Item, Grupos: [[a.Id], [b.Id]]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Item, Grupos: [[a.Id], [a.Id, b.Id, c.Id]]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Item, Grupos: [[a.Id, b.Id, c.Id], []]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Item, Grupos: [[a.Id, b.Id, c.Id, Guid.NewGuid()], [cancelado.Id]]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Item, Grupos: [[a.Id, b.Id, c.Id]]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Item), itens, conta).Recusa);
    }

    [Fact]
    public void Free_values_must_add_up_to_the_bill_and_not_be_negative()
    {
        var itens = new[] { Item(1000), Item(1000), Item(1000) };
        var conta = Conta(itens);

        var ok = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Valores, Valores: [3000, 300, 0]), itens, conta);
        Assert.Equal(RecusaDeDivisao.Nenhuma, ok.Recusa);
        Assert.Equal([3000L, 300L, 0L], ok.Partes.Select(p => p.TotalCentavos));
        Assert.Equal(300L, ok.Partes.Sum(p => p.TaxaServicoCentavos));
        Assert.All(ok.Partes, p => Assert.True(p.ConsumoCentavos >= 0 && p.TaxaServicoCentavos <= p.TotalCentavos));

        Assert.Equal(RecusaDeDivisao.SomaDiferente, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Valores, Valores: [1000, 1000]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.SomaDiferente, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Valores, Valores: [2000, 2000]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Valores, Valores: [3400, -100]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Valores, Valores: [3300]), itens, conta).Recusa);
    }

    [Fact]
    public void Percent_mode_must_add_up_to_one_hundred_with_two_decimals_and_resolves_the_cents_by_the_same_rule()
    {
        var itens = new[] { Item(1000), Item(1000), Item(1000) };
        var conta = Conta(itens);

        var ok = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Percentual, Percentuais: [50m, 30m, 20m]), itens, conta);
        Assert.Equal([1650L, 990L, 660L], ok.Partes.Select(p => p.TotalCentavos));

        var tres = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Percentual, Percentuais: [33.33m, 33.33m, 33.34m]), [Item(10)], Conta([Item(10)], 0m));
        Assert.Equal(10L, tres.Partes.Sum(p => p.TotalCentavos));

        Assert.Equal(RecusaDeDivisao.SomaDiferente, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Percentual, Percentuais: [50m, 49.99m]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Percentual, Percentuais: [50.001m, 49.999m]), itens, conta).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Percentual, Percentuais: [101m, -1m]), itens, conta).Recusa);
    }

    [Fact]
    public void A_negative_or_absurd_tip_is_refused_and_a_zero_bill_still_splits()
    {
        var itens = new[] { Item(1000) };
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: 2, GorjetaCentavos: -1), itens, Conta(itens)).Recusa);
        Assert.Equal(RecusaDeDivisao.Invalida, RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: 2, GorjetaCentavos: RegrasDeDivisao.GorjetaMaximaCentavos + 1), itens, Conta(itens)).Recusa);

        var zero = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: 3, GorjetaCentavos: 100), [], Conta([]));
        Assert.Equal(RecusaDeDivisao.Nenhuma, zero.Recusa);
        Assert.Equal([0L, 0L, 0L], zero.Partes.Select(p => p.TotalCentavos));
        Assert.Equal(100L, zero.Partes.Sum(p => p.GorjetaCentavos));
    }

    [Fact]
    public void Discounts_come_off_before_the_fee_and_the_parts_add_up_to_the_discounted_bill()
    {
        var itens = new[] { Item(1000), Item(1000) };
        var desconto = new Desconto(Guid.NewGuid(), Comanda, null, FormaDoDesconto.Valor, null, 500, 500, "m", Guid.NewGuid(), Agora);
        var conta = RegrasDeComanda.Conta(2000, 10m, false, 0, RegrasDeDesconto.Calcular(itens, [desconto]).TotalCentavos);

        var r = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: 2), itens, conta);

        Assert.Equal(1650L, conta.TotalCentavos);
        Assert.Equal([825L, 825L], r.Partes.Select(p => p.TotalCentavos));
        Assert.Equal(150L, r.Partes.Sum(p => p.TaxaServicoCentavos));
    }

    [Fact]
    public void The_fingerprint_changes_with_items_seats_discounts_and_the_fee_rule_and_ignores_order_and_cancelled_items()
    {
        var a = Item(1000, 1);
        var b = Item(500);
        var desconto = new Desconto(Guid.NewGuid(), Comanda, null, FormaDoDesconto.Valor, null, 100, 100, "m", Guid.NewGuid(), Agora);
        var baseAssinatura = RegrasDeDivisao.Assinatura([a, b], [], 10m, false);

        Assert.Equal(baseAssinatura, RegrasDeDivisao.Assinatura([b, a], [], 10m, false));
        Assert.Equal(64, baseAssinatura.Length);
        Assert.Equal(baseAssinatura, RegrasDeDivisao.Assinatura([a, b, Item(9999, estado: EstadoDoItem.Cancelado)], [], 10m, false));
        Assert.NotEqual(baseAssinatura, RegrasDeDivisao.Assinatura([a], [], 10m, false));
        Assert.NotEqual(baseAssinatura, RegrasDeDivisao.Assinatura([a with { Assento = 2 }, b], [], 10m, false));
        Assert.NotEqual(baseAssinatura, RegrasDeDivisao.Assinatura([a with { Quantidade = 2 }, b], [], 10m, false));
        Assert.NotEqual(baseAssinatura, RegrasDeDivisao.Assinatura([a, b], [desconto], 10m, false));
        Assert.NotEqual(baseAssinatura, RegrasDeDivisao.Assinatura([a, b], [], 10m, true));
        Assert.NotEqual(baseAssinatura, RegrasDeDivisao.Assinatura([a, b], [], 5m, false));
    }

    [Fact]
    public void Modes_and_seats_have_words_and_limits()
    {
        foreach (var modo in Enum.GetValues<ModoDeDivisao>())
        {
            Assert.True(RegrasDeDivisao.TentarModo(RegrasDeDivisao.Palavra(modo), out var volta));
            Assert.Equal(modo, volta);
        }

        Assert.False(RegrasDeDivisao.TentarModo("outro", out _));
        Assert.False(RegrasDeDivisao.TentarModo(null, out _));
        Assert.True(RegrasDeDivisao.AssentoValido(null));
        Assert.True(RegrasDeDivisao.AssentoValido(1));
        Assert.True(RegrasDeDivisao.AssentoValido(99));
        Assert.False(RegrasDeDivisao.AssentoValido(0));
        Assert.False(RegrasDeDivisao.AssentoValido(100));
    }
}
