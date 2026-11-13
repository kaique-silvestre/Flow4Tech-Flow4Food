using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0060: pure rules of moving items between comandas, merging and separating. No I/O.</summary>
public sealed class TransferenciaRulesTests
{
    private static readonly Guid Origem = Guid.NewGuid();
    private static readonly DateTimeOffset Agora = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    private static ItemDaComanda Item(long preco, int quantidade = 1, EstadoDoItem estado = EstadoDoItem.Lancado) =>
        new(Guid.NewGuid(), Origem, Guid.NewGuid(), 1, Guid.NewGuid(), "Chopp", preco, quantidade, null, estado, estado == EstadoDoItem.Cancelado ? "x" : null, estado == EstadoDoItem.Cancelado ? Guid.NewGuid() : null, estado == EstadoDoItem.Cancelado ? Agora : null);

    private static Desconto De(Guid? item, long aplicado) =>
        new(Guid.NewGuid(), Origem, item, FormaDoDesconto.Valor, null, aplicado, aplicado, "motivo", Guid.NewGuid(), Agora);

    [Fact]
    public void A_selection_is_one_to_the_limit_of_distinct_real_ids()
    {
        var um = Guid.NewGuid();
        Assert.True(RegrasDeTransferencia.SelecaoValida([um]));
        Assert.False(RegrasDeTransferencia.SelecaoValida(null));
        Assert.False(RegrasDeTransferencia.SelecaoValida([]));
        Assert.False(RegrasDeTransferencia.SelecaoValida([um, um]));
        Assert.False(RegrasDeTransferencia.SelecaoValida([Guid.Empty]));
        Assert.True(RegrasDeTransferencia.SelecaoValida(Enumerable.Range(0, RegrasDeTransferencia.ItensMaximo).Select(_ => Guid.NewGuid()).ToList()));
        Assert.False(RegrasDeTransferencia.SelecaoValida(Enumerable.Range(0, RegrasDeTransferencia.ItensMaximo + 1).Select(_ => Guid.NewGuid()).ToList()));
    }

    [Fact]
    public void Moving_launched_items_without_discount_and_without_payment_is_accepted()
    {
        var a = Item(1000);
        var b = Item(500, 2);
        Assert.Equal(RecusaDeMovimento.Nenhuma, RegrasDeTransferencia.Itens([a.Id], [a, b], [], 10m, false, 0));
        Assert.Equal(RecusaDeMovimento.Nenhuma, RegrasDeTransferencia.Itens([a.Id, b.Id], [a, b], [], 10m, false, 0));
    }

    [Fact]
    public void An_item_that_is_not_in_the_origin_or_is_cancelled_is_refused()
    {
        var vivo = Item(1000);
        var cancelado = Item(1000, 1, EstadoDoItem.Cancelado);
        Assert.Equal(RecusaDeMovimento.ItemNaoEncontrado, RegrasDeTransferencia.Itens([Guid.NewGuid()], [vivo], [], 10m, false, 0));
        Assert.Equal(RecusaDeMovimento.ItemJaCancelado, RegrasDeTransferencia.Itens([cancelado.Id], [vivo, cancelado], [], 10m, false, 0));
    }

    [Fact]
    public void An_item_with_its_own_discount_is_refused_but_a_comanda_discount_stays_behind()
    {
        var a = Item(1000);
        var b = Item(1000);
        Assert.Equal(RecusaDeMovimento.ItemComDesconto, RegrasDeTransferencia.Itens([a.Id], [a, b], [De(a.Id, 100)], 10m, false, 0));
        Assert.Equal(RecusaDeMovimento.Nenhuma, RegrasDeTransferencia.Itens([a.Id], [a, b], [De(b.Id, 100), De(null, 100)], 10m, false, 0));
    }

    [Fact]
    public void Moving_items_is_refused_when_what_was_received_would_exceed_what_is_left_of_the_bill()
    {
        var a = Item(1000);
        var b = Item(1000);
        // Bill 2000 + 10% = 2200. Paid 1500 > the 1100 that stays after moving one item: refused.
        Assert.Equal(RecusaDeMovimento.ValorJaRecebido, RegrasDeTransferencia.Itens([a.Id], [a, b], [], 10m, false, 1500));
        // Paid exactly what stays (1100): the balance is zero, not negative: accepted.
        Assert.Equal(RecusaDeMovimento.Nenhuma, RegrasDeTransferencia.Itens([a.Id], [a, b], [], 10m, false, 1100));
        // Without the fee the line is 1000: 1100 would exceed it.
        Assert.Equal(RecusaDeMovimento.ValorJaRecebido, RegrasDeTransferencia.Itens([a.Id], [a, b], [], 10m, true, 1100));
        // Moving everything leaves a zero bill: any payment exceeds it.
        Assert.Equal(RecusaDeMovimento.ValorJaRecebido, RegrasDeTransferencia.Itens([a.Id, b.Id], [a, b], [], 10m, false, 1));
    }

    [Fact]
    public void A_comanda_discount_that_stays_lowers_the_bill_that_received_money_is_measured_against()
    {
        var a = Item(1000);
        var b = Item(1000);
        // Remaining consumption 1000, discount 400 on the comanda: 600 + 10% = 660. Paid 700 exceeds it.
        Assert.Equal(RecusaDeMovimento.ValorJaRecebido, RegrasDeTransferencia.Itens([a.Id], [a, b], [De(null, 400)], 10m, false, 700));
        Assert.Equal(RecusaDeMovimento.Nenhuma, RegrasDeTransferencia.Itens([a.Id], [a, b], [De(null, 400)], 10m, false, 660));
    }

    [Fact]
    public void Merging_is_accepted_without_payment_and_without_discount_and_an_empty_comanda_merges_too()
    {
        Assert.Equal(RecusaDeMovimento.Nenhuma, RegrasDeTransferencia.Juncao([Item(1000), Item(500, 1, EstadoDoItem.Cancelado)], [], 0));
        Assert.Equal(RecusaDeMovimento.Nenhuma, RegrasDeTransferencia.Juncao([], [], 0));
    }

    [Fact]
    public void Merging_is_refused_when_the_comanda_received_money_or_holds_any_discount()
    {
        var a = Item(1000);
        Assert.Equal(RecusaDeMovimento.ValorJaRecebido, RegrasDeTransferencia.Juncao([a], [], 1));
        Assert.Equal(RecusaDeMovimento.ComDesconto, RegrasDeTransferencia.Juncao([a], [De(null, 100)], 0));
        Assert.Equal(RecusaDeMovimento.ComDesconto, RegrasDeTransferencia.Juncao([a], [De(a.Id, 100)], 0));
    }

    [Fact]
    public void Only_launched_items_are_carried_by_a_merge()
    {
        var vivo = Item(1000);
        var cancelado = Item(1000, 1, EstadoDoItem.Cancelado);
        Assert.Equal([vivo.Id], RegrasDeTransferencia.IdsParaJuntar([cancelado, vivo]));
    }
}
