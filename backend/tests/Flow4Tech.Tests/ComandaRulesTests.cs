using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>Dominio do salao, ticket 05: pure rules of the comanda (state, numbering text, input, version). No I/O.</summary>
public sealed class ComandaRulesTests
{
    [Theory]
    [InlineData(EstadoDaComanda.Aberta, EstadoDaComanda.Fechada, true)]
    [InlineData(EstadoDaComanda.Aberta, EstadoDaComanda.Cancelada, true)]
    [InlineData(EstadoDaComanda.Fechada, EstadoDaComanda.Aberta, false)]
    [InlineData(EstadoDaComanda.Fechada, EstadoDaComanda.Cancelada, false)]
    [InlineData(EstadoDaComanda.Cancelada, EstadoDaComanda.Aberta, false)]
    [InlineData(EstadoDaComanda.Cancelada, EstadoDaComanda.Fechada, false)]
    [InlineData(EstadoDaComanda.Aberta, EstadoDaComanda.Aberta, false)]
    public void Only_an_open_comanda_can_move_and_only_to_closed_or_cancelled(EstadoDaComanda from, EstadoDaComanda to, bool allowed) =>
        Assert.Equal(allowed, RegrasDeComanda.PodeTransitar(from, to));

    [Theory]
    [InlineData(EstadoDaComanda.Aberta, "aberta")]
    [InlineData(EstadoDaComanda.Fechada, "fechada")]
    [InlineData(EstadoDaComanda.Cancelada, "cancelada")]
    public void A_state_has_one_stored_word_and_back(EstadoDaComanda state, string word)
    {
        Assert.Equal(word, RegrasDeComanda.Palavra(state));
        Assert.Equal(state, RegrasDeComanda.Estado(word));
    }

    [Fact]
    public void An_unknown_stored_state_is_not_guessed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RegrasDeComanda.Estado("reaberta"));

    [Theory]
    [InlineData(1, "#1", "Comanda 1")]
    [InlineData(12, "#12", "Comanda 12")]
    [InlineData(1000, "#1000", "Comanda 1000")]
    public void The_display_number_and_the_default_name_are_built_from_the_number(int number, string shown, string name)
    {
        Assert.Equal(shown, RegrasDeComanda.NumeroDeExibicao(number));
        Assert.Equal(name, RegrasDeComanda.IdentificacaoPadrao(number));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(41, 42)]
    public void The_next_number_of_a_day_follows_the_last_and_starts_at_one(int? last, int expected) =>
        Assert.Equal(expected, RegrasDeComanda.ProximoNumero(last));

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(999, true)]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    [InlineData(1000, false)]
    public void The_head_count_is_optional_and_between_one_and_the_maximum(int? people, bool valid) =>
        Assert.Equal(valid, RegrasDeComanda.PessoasValidas(people));

    [Theory]
    [InlineData("12", "12")]
    [InlineData("  Aniversario  da Ana ", "Aniversario da Ana")]
    public void A_free_name_follows_the_table_identification_rules(string input, string expected) =>
        Assert.Equal(expected, RegrasDeComanda.Identificacao(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    public void A_blank_or_control_name_is_refused(string input) =>
        Assert.Null(RegrasDeComanda.Identificacao(input));

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(2, 3, false)]
    [InlineData(4, 3, false)]
    public void A_write_is_accepted_only_against_the_current_version(int sent, int current, bool accepted) =>
        Assert.Equal(accepted, RegrasDeComanda.VersaoConfere(sent, current));

    [Theory]
    [InlineData(0, StatusDaMesa.Livre)]
    [InlineData(1, StatusDaMesa.Ocupada)]
    [InlineData(3, StatusDaMesa.Ocupada)]
    public void A_table_is_occupied_while_it_has_an_open_comanda_and_free_otherwise(int open, StatusDaMesa status) =>
        Assert.Equal(status, RegrasDeMesa.Status(open));

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(20, true)]
    [InlineData(20.01, false)]
    [InlineData(-0.5, false)]
    public void The_service_fee_percentage_is_between_zero_and_twenty(double percent, bool valid) =>
        Assert.Equal(valid, RegrasDeComanda.TaxaValida((decimal)percent));

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("   ", true, null)]
    [InlineData("  sem cebola  ", true, "sem cebola")]
    [InlineData("linha\ncontrole", false, null)]
    public void The_general_note_is_optional_trimmed_and_free_of_control_characters(string? raw, bool valid, string? stored)
    {
        Assert.Equal(valid, RegrasDeComanda.TentarObservacao(raw, out var note));
        if (valid)
        {
            Assert.Equal(stored, note);
        }
    }

    [Fact]
    public void The_general_note_has_at_most_200_characters()
    {
        Assert.True(RegrasDeComanda.TentarObservacao(new string('x', 200), out _));
        Assert.False(RegrasDeComanda.TentarObservacao(new string('x', 201), out _));
    }

    [Theory]
    [InlineData("cliente-desistiu", null, "cliente desistiu")]
    [InlineData("erro-de-lancamento", "ignorado", "erro de lançamento")]
    [InlineData("outro", "  mesa errada ", "mesa errada")]
    [InlineData("outro", null, null)]
    [InlineData("outro", "   ", null)]
    [InlineData("inexistente", "x", null)]
    [InlineData(null, "x", null)]
    public void Cancelling_a_comanda_uses_the_standard_reason_list_and_outro_needs_free_text(string? code, string? text, string? stored) =>
        Assert.Equal(stored, RegrasDeRodada.MotivoDeItem(code, text));

    [Fact]
    public void An_edited_item_total_follows_the_new_quantity_and_keeps_the_frozen_price_and_options()
    {
        var item = new ItemDaComanda(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), "Chopp", 1000, 2, null, EstadoDoItem.Lancado, null, null, null, [new OpcaoDoItem(Guid.NewGuid(), Guid.NewGuid(), "Gelo", 100)]);

        var editado = item with { Quantidade = 5, Observacao = "sem espuma" };

        Assert.Equal((1000L, 1100L, 5500L), (editado.PrecoCentavos, editado.PrecoUnitarioCentavos, editado.TotalCentavos));
        Assert.Equal(5500L, RegrasDeRodada.Consumo([editado]));
    }
}
