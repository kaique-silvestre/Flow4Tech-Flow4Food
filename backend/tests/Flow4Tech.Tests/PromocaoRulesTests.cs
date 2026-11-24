using Flow4Tech.Comandas.Domain;
using FsCheck.Xunit;

namespace Flow4Tech.Tests;

/// <summary>F4-0151: pure rules of promotions and combos (ADR-020): the window, the evaluation of a round and the order with the manual discounts of ADR-018. No I/O.</summary>
public sealed class PromocaoRulesTests
{
    private static readonly Guid Chopp = Guid.Parse("0197c000-0000-7000-8000-000000000001");
    private static readonly Guid Porcao = Guid.Parse("0197c000-0000-7000-8000-000000000002");
    private static readonly Guid Suco = Guid.Parse("0197c000-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset Agora = new(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);

    // 2026-11-20 is a Friday; 2026-11-21 a Saturday.
    private static readonly DateTime SextaAs2130 = new(2026, 11, 20, 21, 30, 0);
    private static readonly DateTime SabadoAs0100 = new(2026, 11, 21, 1, 0, 0);

    private static Promocao Regra(
        TipoDePromocao tipo = TipoDePromocao.Percentual,
        decimal? percentual = 10m,
        long? valor = null,
        bool ativo = true,
        DateTimeOffset? de = null,
        DateTimeOffset? ate = null,
        int dias = RegrasDePromocao.TodosOsDias,
        TimeOnly? inicio = null,
        TimeOnly? fim = null,
        Guid? id = null,
        params ProdutoDaPromocao[] produtos) =>
        new(id ?? Guid.NewGuid(), null, "Regra", tipo, tipo == TipoDePromocao.Percentual ? percentual : null, valor, ativo, de, ate, dias, inicio, fim, 1, produtos.Length == 0 ? [new ProdutoDaPromocao(Chopp, 1)] : produtos, Guid.NewGuid(), Agora, Guid.NewGuid(), Agora);

    private static LinhaParaPromocao Linha(int indice, Guid produto, long preco, int quantidade, long opcoes = 0) => new(indice, produto, preco, preco + opcoes, quantidade);

    [Fact]
    public void A_turned_off_rule_or_one_outside_its_validity_is_never_in_force()
    {
        Assert.True(RegrasDePromocao.VigenteEm(Regra(), Agora, SextaAs2130));
        Assert.False(RegrasDePromocao.VigenteEm(Regra(ativo: false), Agora, SextaAs2130));
        Assert.False(RegrasDePromocao.VigenteEm(Regra(de: Agora.AddSeconds(1)), Agora, SextaAs2130));
        Assert.True(RegrasDePromocao.VigenteEm(Regra(de: Agora), Agora, SextaAs2130));
        Assert.False(RegrasDePromocao.VigenteEm(Regra(ate: Agora), Agora, SextaAs2130));
        Assert.True(RegrasDePromocao.VigenteEm(Regra(ate: Agora.AddSeconds(1)), Agora, SextaAs2130));
    }

    [Fact]
    public void The_days_of_the_week_are_judged_on_the_local_calendar_day()
    {
        var soSexta = 1 << (int)DayOfWeek.Friday;
        Assert.True(RegrasDePromocao.VigenteEm(Regra(dias: soSexta), Agora, SextaAs2130));
        Assert.False(RegrasDePromocao.VigenteEm(Regra(dias: soSexta), Agora, SabadoAs0100));
        Assert.False(RegrasDePromocao.VigenteEm(Regra(dias: 1 << (int)DayOfWeek.Monday), Agora, SextaAs2130));
    }

    [Theory]
    [InlineData("18:00", "22:00", 21, 30, true)]
    [InlineData("18:00", "22:00", 22, 0, false)] // end is exclusive
    [InlineData("18:00", "22:00", 18, 0, true)] // start is inclusive
    [InlineData("18:00", "22:00", 17, 59, false)]
    [InlineData("22:00", "02:00", 23, 0, true)] // crosses midnight, same evening
    [InlineData("22:00", "02:00", 21, 59, false)]
    public void A_time_window_is_start_inclusive_end_exclusive_in_local_time(string inicio, string fim, int hora, int minuto, bool esperado)
    {
        Assert.True(RegrasDePromocao.TentarHora(inicio, out var i));
        Assert.True(RegrasDePromocao.TentarHora(fim, out var f));
        var local = new DateTime(2026, 11, 20, hora, minuto, 0);
        Assert.Equal(esperado, RegrasDePromocao.VigenteEm(Regra(inicio: i, fim: f), Agora, local));
    }

    [Fact]
    public void A_window_that_crosses_midnight_belongs_to_the_day_it_started()
    {
        var inicio = new TimeOnly(22, 0);
        var fim = new TimeOnly(2, 0);
        var soSexta = 1 << (int)DayOfWeek.Friday;

        Assert.True(RegrasDePromocao.VigenteEm(Regra(dias: soSexta, inicio: inicio, fim: fim), Agora, SextaAs2130.AddMinutes(60)));
        Assert.True(RegrasDePromocao.VigenteEm(Regra(dias: soSexta, inicio: inicio, fim: fim), Agora, SabadoAs0100));
        Assert.False(RegrasDePromocao.VigenteEm(Regra(dias: soSexta, inicio: inicio, fim: fim), Agora, SabadoAs0100.AddHours(1)));
        Assert.False(RegrasDePromocao.VigenteEm(Regra(dias: 1 << (int)DayOfWeek.Saturday, inicio: inicio, fim: fim), Agora, SabadoAs0100));
    }

    [Fact]
    public void No_list_of_days_means_every_day() => Assert.Equal(127, RegrasDePromocao.MascaraDeDias(null));

    [Fact]
    public void Days_must_be_distinct_and_between_zero_and_six()
    {
        Assert.Equal(0b0100010, RegrasDePromocao.MascaraDeDias([1, 5]));
        Assert.Equal([1, 5], RegrasDePromocao.DiasDaMascara(0b0100010));
        Assert.Null(RegrasDePromocao.MascaraDeDias([]));
        Assert.Null(RegrasDePromocao.MascaraDeDias([1, 1]));
        Assert.Null(RegrasDePromocao.MascaraDeDias([7]));
        Assert.Null(RegrasDePromocao.MascaraDeDias([-1]));
    }

    [Theory]
    [InlineData("Happy hour", "Happy hour")]
    [InlineData("  Combo 2  ", "Combo 2")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void The_name_is_trimmed_and_required(string? nome, string? esperado) => Assert.Equal(esperado, RegrasDePromocao.NomeValido(nome));

    [Fact]
    public void The_name_is_limited_and_has_no_control_characters()
    {
        Assert.NotNull(RegrasDePromocao.NomeValido(new string('x', RegrasDePromocao.NomeMaximo)));
        Assert.Null(RegrasDePromocao.NomeValido(new string('x', RegrasDePromocao.NomeMaximo + 1)));
        Assert.Null(RegrasDePromocao.NomeValido("a\nb"));
    }

    [Fact]
    public void A_percent_rule_takes_the_percent_of_the_whole_line_with_the_rounding_of_ADR_018()
    {
        var regra = Regra(percentual: 10m);

        var aplicadas = RegrasDePromocao.Avaliar([Linha(0, Chopp, 1005, 1), Linha(1, Porcao, 5000, 1), Linha(2, Chopp, 1000, 2, opcoes: 250)], [regra]);

        Assert.Equal([(0, 101L), (2, 250L)], aplicadas.Select(a => (a.Indice, a.DescontoCentavos)));
        Assert.All(aplicadas, a => Assert.Equal(regra.Id, a.PromocaoId));
    }

    [Fact]
    public void A_fixed_amount_rule_takes_it_from_each_unit_and_never_more_than_the_line()
    {
        var regra = Regra(TipoDePromocao.Valor, valor: 300);

        var aplicadas = RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 3), Linha(1, Chopp, 200, 2)], [regra]);

        Assert.Equal([(0, 900L), (1, 400L)], aplicadas.Select(a => (a.Indice, a.DescontoCentavos)));
    }

    [Fact]
    public void A_line_takes_only_the_best_of_the_rules_that_list_its_product_and_a_tie_goes_to_the_lowest_id()
    {
        var dez = Regra(percentual: 10m, id: Guid.Parse("0197c000-0000-7000-8000-0000000000a2"));
        var quinze = Regra(percentual: 15m, id: Guid.Parse("0197c000-0000-7000-8000-0000000000a1"));
        var valor150 = Regra(TipoDePromocao.Valor, valor: 150, id: Guid.Parse("0197c000-0000-7000-8000-0000000000a3"));
        var empate = Regra(TipoDePromocao.Valor, valor: 150, id: Guid.Parse("0197c000-0000-7000-8000-0000000000a0"));

        var melhor = Assert.Single(RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 1)], [dez, quinze, valor150]));
        var desempate = Assert.Single(RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 1)], [valor150, empate]));

        Assert.Equal((quinze.Id, 150L), (melhor.PromocaoId, melhor.DescontoCentavos));
        Assert.Equal(empate.Id, desempate.PromocaoId);
    }

    [Fact]
    public void A_rule_that_lists_other_products_takes_nothing()
    {
        Assert.Empty(RegrasDePromocao.Avaliar([Linha(0, Suco, 800, 4)], [Regra(), Regra(TipoDePromocao.Valor, valor: 100)]));
    }

    [Fact]
    public void A_combo_prices_each_full_set_and_shares_the_saving_among_its_lines_exactly()
    {
        // 1 chopp (1000) + 1 porcao (3000) = 4000 for 3300: saving 700 per set; 2 sets in the round.
        var combo = Regra(TipoDePromocao.Combo, valor: 3300, produtos: [new ProdutoDaPromocao(Chopp, 1), new ProdutoDaPromocao(Porcao, 1)]);

        var aplicadas = RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 2), Linha(1, Porcao, 3000, 2)], [combo]);

        Assert.Equal(1400L, aplicadas.Sum(a => a.DescontoCentavos));
        Assert.Equal([(0, 350L), (1, 1050L)], aplicadas.OrderBy(a => a.Indice).Select(a => (a.Indice, a.DescontoCentavos)));
    }

    [Fact]
    public void A_combo_missing_one_product_or_one_unit_takes_nothing_and_extra_units_stay_at_table_price()
    {
        var combo = Regra(TipoDePromocao.Combo, valor: 3300, produtos: [new ProdutoDaPromocao(Chopp, 2), new ProdutoDaPromocao(Porcao, 1)]);

        Assert.Empty(RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 3)], [combo]));
        Assert.Empty(RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 1), Linha(1, Porcao, 3000, 1)], [combo]));

        // 3 chopps and 1 porcao: one set (2 chopps + 1 porcao = 5000 for 3300, saving 1700); the third chopp is outside the set.
        var aplicadas = RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 3), Linha(1, Porcao, 3000, 1)], [combo]);
        Assert.Equal(1700L, aplicadas.Sum(a => a.DescontoCentavos));
    }

    [Fact]
    public void A_combo_that_does_not_save_anything_is_ignored_and_a_line_a_combo_used_takes_no_other_promotion()
    {
        var caro = Regra(TipoDePromocao.Combo, valor: 9000, produtos: [new ProdutoDaPromocao(Chopp, 1), new ProdutoDaPromocao(Porcao, 1)]);
        Assert.Empty(RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 1), Linha(1, Porcao, 3000, 1)], [caro]));

        var combo = Regra(TipoDePromocao.Combo, valor: 3000, produtos: [new ProdutoDaPromocao(Chopp, 1), new ProdutoDaPromocao(Porcao, 1)]);
        var cemPorCento = Regra(percentual: 100m);
        var aplicadas = RegrasDePromocao.Avaliar([Linha(0, Chopp, 1000, 1), Linha(1, Porcao, 3000, 1)], [combo, cemPorCento]);

        Assert.Equal(1000L, aplicadas.Sum(a => a.DescontoCentavos));
        Assert.All(aplicadas, a => Assert.Equal(combo.Id, a.PromocaoId));
    }

    [Fact]
    public void The_promotion_is_a_discount_of_the_item_given_before_any_manual_discount_and_the_manual_one_is_clamped_to_what_is_left()
    {
        var item = new ItemDaComanda(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Chopp, "Chopp", 1000, 1, null, EstadoDoItem.Lancado, null, null, null);
        var promocao = new Desconto(Guid.NewGuid(), item.ComandaId, item.Id, FormaDoDesconto.Valor, null, 300, 300, "Promoção: x", Guid.NewGuid(), Agora, Guid.NewGuid());
        var manual = new Desconto(Guid.NewGuid(), item.ComandaId, item.Id, FormaDoDesconto.Valor, null, 900, 900, "manual", Guid.NewGuid(), Agora.AddMinutes(1));

        Assert.Equal(700, RegrasDeDesconto.BaseDoItem(item, [promocao]));
        Assert.Equal(0, RegrasDeDesconto.BaseDoItem(item, [promocao, manual]));
        Assert.Equal(1000L, RegrasDeDesconto.Calcular([item], [promocao, manual]).DeItensCentavos);
    }
}

/// <summary>F4-0151: property tests of the promotion rule. For EVERY round and set of rules: nothing is taken beyond a line, a line takes at most one promotion, and the bill with a manual discount on top never goes below zero.</summary>
public sealed class PromocaoPropertyTests
{
    private static readonly Guid[] Produtos = [.. Enumerable.Range(1, 4).Select(i => Guid.Parse($"0197c000-0000-7000-8000-00000000010{i}"))];
    private static readonly DateTimeOffset Agora = new(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);

    private static (List<LinhaParaPromocao> Linhas, List<Promocao> Regras) Cenario(int semente)
    {
        var azar = new Sorteio(semente);
        var linhas = Enumerable.Range(0, azar.Next(1, 7))
            .Select(i => { var preco = azar.Next(0, 20_001); return new LinhaParaPromocao(i, Produtos[azar.Next(Produtos.Length)], preco, preco + azar.Next(0, 501), azar.Next(1, 21)); })
            .ToList();
        var regras = Enumerable.Range(0, azar.Next(0, 6)).Select(n =>
        {
            var tipo = (TipoDePromocao)azar.Next(3);
            var itens = Produtos.OrderBy(_ => azar.Next()).Take(tipo == TipoDePromocao.Combo ? azar.Next(1, 4) : azar.Next(1, 3)).Select(p => new ProdutoDaPromocao(p, tipo == TipoDePromocao.Combo ? azar.Next(1, 4) : 1)).ToList();
            if (tipo == TipoDePromocao.Combo && itens.Sum(x => x.Quantidade) < 2)
            {
                itens[0] = itens[0] with { Quantidade = 2 };
            }

            return new Promocao(Guid.NewGuid(), null, "R", tipo, tipo == TipoDePromocao.Percentual ? azar.Next(1, 10_001) / 100m : null, tipo == TipoDePromocao.Percentual ? null : azar.Next(1, 40_001), true, null, null, 127, null, null, 1, itens, Guid.NewGuid(), Agora, Guid.NewGuid(), Agora);
        }).ToList();
        return (linhas, regras);
    }

    [Property]
    public bool Nothing_is_taken_beyond_a_line_a_line_takes_at_most_one_promotion_and_the_total_never_passes_the_consumption(int semente)
    {
        var (linhas, regras) = Cenario(semente);

        var aplicadas = RegrasDePromocao.Avaliar(linhas, regras);

        return aplicadas.GroupBy(a => a.Indice).All(g => g.Count() == 1)
            && aplicadas.All(a => a.DescontoCentavos >= 1 && a.DescontoCentavos <= linhas[a.Indice].TotalCentavos)
            && aplicadas.Sum(a => a.DescontoCentavos) <= linhas.Sum(l => l.TotalCentavos);
    }

    [Property]
    public bool A_promotion_never_leaves_the_bill_negative_and_a_manual_discount_given_later_cannot_take_it_back(int semente, int manualBruto)
    {
        var (linhas, regras) = Cenario(semente);
        var aplicadas = RegrasDePromocao.Avaliar(linhas, regras);
        var comanda = Guid.NewGuid();
        var itens = linhas.Select(l => new ItemDaComanda(Guid.NewGuid(), comanda, Guid.NewGuid(), l.Indice + 1, l.ProdutoId, "P", l.PrecoCentavos, l.Quantidade, null, EstadoDoItem.Lancado, null, null, null, l.UnitarioCentavos == l.PrecoCentavos ? [] : [new OpcaoDoItem(Guid.NewGuid(), Guid.NewGuid(), "o", l.UnitarioCentavos - l.PrecoCentavos)])).ToList();
        var daPromocao = aplicadas.Select(a => new Desconto(Guid.NewGuid(), comanda, itens[a.Indice].Id, FormaDoDesconto.Valor, null, a.DescontoCentavos, a.DescontoCentavos, "Promoção", Guid.NewGuid(), Agora, a.PromocaoId)).ToList();
        var r = new Sorteio(manualBruto);
        var manual = itens.Select(i => new Desconto(Guid.NewGuid(), comanda, r.Next(2) == 0 ? i.Id : null, FormaDoDesconto.Valor, null, r.Next(1, 30_000), r.Next(1, 30_000), "manual", Guid.NewGuid(), Agora.AddMinutes(1))).ToList();
        var consumo = RegrasDeRodada.Consumo(itens);

        var soPromocao = RegrasDeDesconto.Calcular(itens, daPromocao);
        var todos = RegrasDeDesconto.Calcular(itens, [.. daPromocao, .. manual]);

        // The order of ADR-018: the promotion comes off its line first and in full; whatever the manual discounts take comes after it, from what is left.
        return soPromocao.TotalCentavos == aplicadas.Sum(a => a.DescontoCentavos)
            && todos.TotalCentavos >= soPromocao.TotalCentavos
            && todos.TotalCentavos <= consumo
            && consumo - todos.TotalCentavos >= 0
            && itens.All(i => RegrasDeDesconto.BaseDoItem(i, [.. daPromocao, .. manual]) >= 0);
    }
}
