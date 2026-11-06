using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>Dominio do salao, ticket 06: the pure rules of rounds and items (no I/O, no database; the clock is a plain argument).</summary>
public sealed class RodadaRulesTests
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    [InlineData(3600, false)]
    public void The_undo_window_counts_whole_seconds_from_the_send(int secondsLater, bool allowed) =>
        Assert.Equal(allowed, RegrasDeRodada.DentroDaJanela(Sent, Sent.AddSeconds(secondsLater), 8));

    [Fact]
    public void The_undo_window_follows_the_setting_and_refuses_a_clock_that_went_back()
    {
        Assert.True(RegrasDeRodada.DentroDaJanela(Sent, Sent.AddSeconds(20), 30));
        Assert.False(RegrasDeRodada.DentroDaJanela(Sent, Sent.AddSeconds(20), 8));
        Assert.False(RegrasDeRodada.DentroDaJanela(Sent, Sent.AddSeconds(-1), 8));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    [InlineData(-3, false)]
    public void The_quantity_is_a_positive_whole_number_up_to_a_ceiling(int quantity, bool valid) =>
        Assert.Equal(valid, RegrasDeRodada.QuantidadeValida(quantity));

    [Fact]
    public void A_note_is_trimmed_optional_and_without_control_characters()
    {
        Assert.True(RegrasDeRodada.TentarObservacao("  sem gelo ", out var note));
        Assert.Equal("sem gelo", note);
        Assert.True(RegrasDeRodada.TentarObservacao("   ", out var blank));
        Assert.Null(blank);
        Assert.True(RegrasDeRodada.TentarObservacao(null, out var none));
        Assert.Null(none);
        Assert.False(RegrasDeRodada.TentarObservacao("linha\nquebrada", out _));
        Assert.False(RegrasDeRodada.TentarObservacao(new string('a', 201), out _));
    }

    [Theory]
    [InlineData("cliente-desistiu", null, "cliente desistiu")]
    [InlineData("erro-de-lancamento", "ignorado", "erro de lançamento")]
    [InlineData("item-indisponivel", null, "item indisponível")]
    [InlineData("outro", " acabou o gelo ", "acabou o gelo")]
    [InlineData("outro", null, null)]
    [InlineData("outro", "  ", null)]
    [InlineData("Outro", "texto", null)]
    [InlineData("qualquer", "texto", null)]
    [InlineData(null, "texto", null)]
    public void A_cancel_reason_is_one_of_the_list_and_outro_needs_free_text(string? code, string? text, string? stored) =>
        Assert.Equal(stored, RegrasDeRodada.MotivoDeItem(code, text));

    [Fact]
    public void An_instant_is_cut_to_microseconds_the_precision_the_database_keeps()
    {
        var instant = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.FromHours(-3)).AddTicks(1234567);

        var cut = RegrasDeRodada.Microssegundos(instant);

        Assert.Equal(instant.AddTicks(-7), cut);
        Assert.Equal(instant.Offset, cut.Offset);
        Assert.Equal(cut, RegrasDeRodada.Microssegundos(cut));
    }

    [Fact]
    public void The_undo_reason_is_fixed()
    {
        Assert.Equal("desfeito pelo garçom", RegrasDeRodada.MotivoDesfeito);
    }

    [Fact]
    public void The_consumption_sums_price_times_quantity_of_launched_items_only()
    {
        var rodada = Guid.NewGuid();
        var comanda = Guid.NewGuid();
        var itens = new[]
        {
            Item(comanda, rodada, 1, 1250, 3, EstadoDoItem.Lancado),
            Item(comanda, rodada, 2, 3490, 1, EstadoDoItem.Lancado),
            Item(comanda, rodada, 3, 9999, 5, EstadoDoItem.Cancelado),
        };

        Assert.Equal(1250L * 3 + 3490L, RegrasDeRodada.Consumo(itens));
        Assert.Equal(0L, RegrasDeRodada.Consumo([]));
    }

    [Fact]
    public void An_item_state_round_trips_through_its_stored_word()
    {
        Assert.Equal(EstadoDoItem.Lancado, RegrasDeRodada.EstadoDe(RegrasDeRodada.Palavra(EstadoDoItem.Lancado)));
        Assert.Equal(EstadoDoItem.Cancelado, RegrasDeRodada.EstadoDe(RegrasDeRodada.Palavra(EstadoDoItem.Cancelado)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegrasDeRodada.EstadoDe("fechado"));
    }

    private static ItemDaComanda Item(Guid comanda, Guid rodada, int posicao, long preco, int quantidade, EstadoDoItem estado) =>
        new(Guid.NewGuid(), comanda, rodada, posicao, Guid.NewGuid(), "Produto", preco, quantidade, null, estado, estado == EstadoDoItem.Cancelado ? "cliente desistiu" : null, estado == EstadoDoItem.Cancelado ? Guid.NewGuid() : null, null);
}
