using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0067: the pure pre-bill and receipt (data and plain document). No I/O.</summary>
public sealed class ComprovanteRulesTests
{
    private static readonly Guid ComandaId = Guid.NewGuid();
    private static readonly DateTimeOffset Agora = new(2026, 11, 13, 22, 0, 0, TimeSpan.Zero);

    private static Comanda Comanda(EstadoDaComanda estado = EstadoDaComanda.Aberta, DateTimeOffset? pedida = null) =>
        new(ComandaId, Guid.NewGuid(), new DateOnly(2026, 11, 13), 12, "Mesa 4", Guid.NewGuid(), "salao", Guid.NewGuid(), null, estado, Agora.AddHours(-2), estado == EstadoDaComanda.Aberta ? null : Agora, 10m, false, 3, ContaPedidaEm: pedida);

    private static ItemDaComanda Item(string nome, long preco, int quantidade = 1, EstadoDoItem estado = EstadoDoItem.Lancado, TipoDoItem tipo = TipoDoItem.Normal, string? obs = null, params OpcaoDoItem[] opcoes) =>
        new(Guid.NewGuid(), ComandaId, Guid.NewGuid(), 1, Guid.NewGuid(), nome, preco, quantidade, obs, estado, estado == EstadoDoItem.Cancelado ? "x" : null, estado == EstadoDoItem.Cancelado ? Guid.NewGuid() : null, estado == EstadoDoItem.Cancelado ? Agora : null, opcoes, tipo);

    private static Desconto De(Guid? item, long aplicado, string motivo = "cliente antigo") =>
        new(Guid.NewGuid(), ComandaId, item, FormaDoDesconto.Valor, null, aplicado, aplicado, motivo, Guid.NewGuid(), Agora);

    [Fact]
    public void The_pre_bill_lists_launched_items_at_the_frozen_price_with_options_and_leaves_cancelled_ones_out()
    {
        var chopp = Item("Chopp", 1250, 2, obs: "sem espuma", opcoes: new OpcaoDoItem(Guid.NewGuid(), Guid.NewGuid(), "Limão", 100));
        var cancelado = Item("Porção", 4000, estado: EstadoDoItem.Cancelado);

        var c = RegrasDeComprovante.Montar(TipoDeComprovante.PreConta, Comanda(), [chopp, cancelado], [], [], null, null, Agora);

        var linha = Assert.Single(c.Itens);
        Assert.Equal(("Chopp", 2, 1350L, 2700L, "normal", "sem espuma"), (linha.Descricao, linha.Quantidade, linha.UnitarioCentavos, linha.TotalCentavos, linha.Tipo, linha.Observacao));
        Assert.Equal(["Limão"], linha.Opcoes);
        Assert.Equal((2700L, 270L, 2970L, 2970L), (c.Conta.ConsumoCentavos, c.Conta.TaxaServicoCentavos, c.Conta.TotalCentavos, c.Conta.SaldoCentavos));
        Assert.False(c.SegundaVia);
        Assert.Null(c.Partes);
        Assert.Empty(c.Pagamentos);
    }

    [Fact]
    public void Discounts_and_courtesies_follow_the_fixed_order_and_the_fee_is_charged_on_what_is_left()
    {
        var a = Item("Chopp", 1000, 2);
        var brinde = Item("Sobremesa", 1500, tipo: TipoDoItem.Brinde);

        var c = RegrasDeComprovante.Montar(TipoDeComprovante.PreConta, Comanda(), [a, brinde], [De(a.Id, 500), De(null, 300, "gerente")], [], null, null, Agora);

        Assert.Equal(["normal", "brinde"], c.Itens.Select(i => i.Tipo));
        Assert.Equal([2000L, 0L], c.Itens.Select(i => i.TotalCentavos));
        Assert.Equal([("Chopp", 500L), ("Comanda", 300L)], c.Descontos.Select(d => (d.Alvo, d.AplicadoCentavos)));
        Assert.Equal((2000L, 800L, 120L, 1320L), (c.Conta.ConsumoCentavos, c.Conta.DescontoCentavos, c.Conta.TaxaServicoCentavos, c.Conta.TotalCentavos));
    }

    [Fact]
    public void A_receipt_is_a_second_copy_with_the_payments_and_the_balance_it_closed_with()
    {
        var a = Item("Chopp", 1250, 2);
        var dinheiro = new PagamentoDoComprovante("Dinheiro", 1000, 500, 0, Agora, null, null);
        var pix = new PagamentoDoComprovante("Pix", 1750, 0, 200, Agora, null, null);

        var c = RegrasDeComprovante.Montar(TipoDeComprovante.Recibo, Comanda(EstadoDaComanda.Fechada), [a], [], [dinheiro, pix], null, null, Agora);

        Assert.True(c.SegundaVia);
        Assert.Equal((2750L, 2750L, 0L), (c.Conta.TotalCentavos, c.Conta.PagoCentavos, c.Conta.SaldoCentavos));
        Assert.Equal(["Dinheiro", "Pix"], c.Pagamentos.Select(p => p.MetodoNome));
        Assert.Equal(Agora, c.FechadaEm);
    }

    [Fact]
    public void A_split_gives_each_part_what_it_owes_and_what_was_applied_to_it()
    {
        var a = Item("Chopp", 1000, 2);
        var divisao = Guid.NewGuid();
        var partes = new[]
        {
            new ParteDaDivisao(1, null, [], 1000, 100, 1100, 50),
            new ParteDaDivisao(2, null, [], 1000, 100, 1100, 50),
        };
        var paga = new PagamentoDoComprovante("Pix", 1100, 0, 50, Agora, divisao, 2);

        var c = RegrasDeComprovante.Montar(TipoDeComprovante.PreConta, Comanda(), [a], [], [paga], partes, divisao, Agora);

        Assert.NotNull(c.Partes);
        Assert.Equal([(1, 1100L, 50L, 0L), (2, 1100L, 50L, 1100L)], c.Partes.Select(p => (p.Numero, p.TotalCentavos, p.GorjetaCentavos, p.PagoCentavos)));
    }

    [Fact]
    public void The_plain_document_says_it_is_not_fiscal_shows_the_bill_and_holds_nothing_else()
    {
        var a = Item("Chopp", 1250, 2, obs: "sem espuma");
        var c = RegrasDeComprovante.Montar(TipoDeComprovante.PreConta, Comanda(pedida: Agora), [a], [De(a.Id, 250)], [], null, null, Agora);

        var texto = RegrasDeComprovante.Texto(c);

        Assert.Contains("PRÉ-CONTA", texto, StringComparison.Ordinal);
        Assert.Contains("Sem valor fiscal", texto, StringComparison.Ordinal);
        Assert.Contains("Comanda #12 - Mesa 4", texto, StringComparison.Ordinal);
        Assert.Contains("2x Chopp", texto, StringComparison.Ordinal);
        Assert.Contains("R$ 25,00", texto, StringComparison.Ordinal);
        Assert.Contains("Desconto", texto, StringComparison.Ordinal);
        Assert.Contains("Taxa de serviço (10%)", texto, StringComparison.Ordinal);
        Assert.Contains("TOTAL", texto, StringComparison.Ordinal);
        Assert.Contains("R$ 24,75", texto, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', texto);

        var recibo = RegrasDeComprovante.Texto(RegrasDeComprovante.Montar(TipoDeComprovante.Recibo, Comanda(EstadoDaComanda.Fechada), [a], [], [new("Dinheiro", 2750, 0, 0, Agora, null, null)], null, null, Agora));
        Assert.Contains("RECIBO - 2ª VIA", recibo, StringComparison.Ordinal);
        Assert.Contains("Dinheiro", recibo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, "R$ 0,00")]
    [InlineData(5L, "R$ 0,05")]
    [InlineData(2750L, "R$ 27,50")]
    [InlineData(123456789L, "R$ 1.234.567,89")]
    public void Cents_are_written_in_reais_with_comma_and_thousands_dots(long centavos, string esperado) =>
        Assert.Equal(esperado, RegrasDeComprovante.Reais(centavos));
}
