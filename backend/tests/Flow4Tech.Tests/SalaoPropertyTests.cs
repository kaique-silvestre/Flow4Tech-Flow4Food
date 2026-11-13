using Flow4Tech.Caixa.Domain;
using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Pagamentos.Domain;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Flow4Tech.Tests;

/// <summary>Amount in cents inside the range the product accepts (0 to the maximum of the rules).</summary>
public readonly record struct Centavos(long Value);

/// <summary>Service fee percentage with one decimal, inside what the product accepts (0 to 20).</summary>
public readonly record struct Percentual(decimal Value);

/// <summary>A signed difference of cents, as the cash register difference can be.</summary>
public readonly record struct DiferencaEmCentavos(long Value);

public readonly record struct Preco(long Value);

public readonly record struct Qtd(int Value);

/// <summary>Generators of the property tests of the salão rules (F4-0105).</summary>
public static class SalaoGeradores
{
    public static Arbitrary<Centavos> Centavos() => Gen.Choose(0, (int)RegrasDePagamento.ValorMaximoCentavos).Select(v => new Centavos(v)).ToArbitrary();

    public static Arbitrary<Percentual> Percentual() => Gen.Choose(0, 200).Select(v => new Percentual(v / 10m)).ToArbitrary();

    public static Arbitrary<DiferencaEmCentavos> Diferenca() =>
        Gen.Choose(-(int)RegrasDoCaixa.ValorMaximoCentavos, (int)RegrasDoCaixa.ValorMaximoCentavos).Select(v => new DiferencaEmCentavos(v)).ToArbitrary();

    public static Arbitrary<Preco> Preco() => Gen.Choose(0, 1_000_000).Select(v => new Preco(v)).ToArbitrary();

    public static Arbitrary<Qtd> Quantidade() => Gen.Choose(1, RegrasDeRodada.QuantidadeMaxima).Select(v => new Qtd(v)).ToArbitrary();
}

/// <summary>
/// Property tests (F4-0105): rules that must hold for EVERY input, not only the examples picked in the example tests. No I/O, no database (runs as a unit test).
/// </summary>
public sealed class SalaoPropertyTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    // ---- service fee and the bill ----

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool The_fee_is_within_half_a_cent_of_the_exact_percentage(Centavos consumo, Percentual percentual)
    {
        var exact = consumo.Value * percentual.Value / 100m;
        var taxa = RegrasDeComanda.TaxaDeServico(consumo.Value, percentual.Value);
        return Math.Abs(taxa - exact) <= 0.5m;
    }

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool The_fee_never_grows_when_the_consumption_shrinks(Centavos a, Centavos b, Percentual percentual)
    {
        var (menor, maior) = a.Value <= b.Value ? (a.Value, b.Value) : (b.Value, a.Value);
        return RegrasDeComanda.TaxaDeServico(menor, percentual.Value) <= RegrasDeComanda.TaxaDeServico(maior, percentual.Value);
    }

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool Without_consumption_or_percentage_there_is_no_fee(Centavos consumo, Percentual percentual) =>
        RegrasDeComanda.TaxaDeServico(0, percentual.Value) == 0
        && RegrasDeComanda.TaxaDeServico(-consumo.Value, percentual.Value) == 0
        && RegrasDeComanda.TaxaDeServico(consumo.Value, 0m) == 0;

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool The_fee_is_never_more_than_the_maximum_rate_allows(Centavos consumo, Percentual percentual) =>
        RegrasDeComanda.TaxaDeServico(consumo.Value, percentual.Value) <= (consumo.Value * RegrasDeComanda.TaxaMaxima / 100m) + 0.5m;

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool The_bill_adds_up_and_the_removed_fee_is_zero(Centavos consumo, Percentual percentual, Centavos pago)
    {
        var cheia = RegrasDeComanda.Conta(consumo.Value, percentual.Value, false, pago.Value);
        var sem = RegrasDeComanda.Conta(consumo.Value, percentual.Value, true, pago.Value);
        return cheia.TotalCentavos == cheia.ConsumoCentavos + cheia.TaxaServicoCentavos
            && cheia.SaldoCentavos == cheia.TotalCentavos - pago.Value
            && sem.TaxaServicoCentavos == 0
            && sem.TotalCentavos == consumo.Value
            && sem.SaldoCentavos == consumo.Value - pago.Value
            && cheia.TotalCentavos >= sem.TotalCentavos;
    }

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool A_comanda_can_close_exactly_when_the_balance_is_zero(Centavos consumo, Percentual percentual, bool taxaRemovida)
    {
        var total = RegrasDeComanda.Conta(consumo.Value, percentual.Value, taxaRemovida, 0).TotalCentavos;
        var quitada = RegrasDeComanda.Conta(consumo.Value, percentual.Value, taxaRemovida, total);
        var faltando = RegrasDeComanda.Conta(consumo.Value, percentual.Value, taxaRemovida, total - 1);
        return RegrasDeComanda.PodeFechar(quitada) && (total == 0 || !RegrasDeComanda.PodeFechar(faltando));
    }

    // ---- payment, change and the register ----

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool Applied_plus_change_is_always_what_was_received(Centavos recebido, Centavos saldo)
    {
        var received = Math.Max(1, recebido.Value);
        var aplicado = RegrasDePagamento.Aplicado(received, saldo.Value);
        var troco = RegrasDePagamento.Troco(received, aplicado);
        return aplicado >= 0 && troco >= 0 && aplicado + troco == received;
    }

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool A_payment_never_applies_more_than_the_balance_or_than_was_received(Centavos recebido, Centavos saldo)
    {
        var received = Math.Max(1, recebido.Value);
        var aplicado = RegrasDePagamento.Aplicado(received, saldo.Value);
        return aplicado <= received && aplicado <= saldo.Value && saldo.Value - aplicado >= 0;
    }

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool Change_exists_only_when_the_whole_balance_was_paid(Centavos recebido, Centavos saldo)
    {
        var received = Math.Max(1, recebido.Value);
        var aplicado = RegrasDePagamento.Aplicado(received, saldo.Value);
        var troco = RegrasDePagamento.Troco(received, aplicado);
        return troco == 0 || aplicado == saldo.Value;
    }

    [Property]
    public bool A_received_amount_is_valid_exactly_inside_the_limits(long valor) =>
        RegrasDePagamento.RecebidoValido(valor) == (valor >= 1 && valor <= RegrasDePagamento.ValorMaximoCentavos);

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool The_register_difference_cancels_out(Centavos contado, Centavos esperado) =>
        RegrasDoCaixa.Diferenca(contado.Value, esperado.Value) + esperado.Value == contado.Value;

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool The_expected_cash_is_the_fund_plus_the_cash_received(Centavos fundo, Centavos recebido) =>
        RegrasDoCaixa.Esperado(fundo.Value, recebido.Value) == RegrasDoCaixa.Esperado(recebido.Value, fundo.Value)
        && RegrasDoCaixa.Esperado(fundo.Value, 0) == fundo.Value;

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool The_difference_sentence_says_the_right_side_and_the_exact_amount(DiferencaEmCentavos diferenca)
    {
        var frase = RegrasDoCaixa.Palavra(diferenca.Value);
        if (diferenca.Value == 0)
        {
            return frase == "Confere";
        }

        var prefixo = diferenca.Value < 0 ? "Falta R$ " : "Sobra R$ ";
        if (!frase.StartsWith(prefixo, StringComparison.Ordinal))
        {
            return false;
        }

        var numero = frase[prefixo.Length..].Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return decimal.Parse(numero, System.Globalization.CultureInfo.InvariantCulture) * 100m == Math.Abs(diferenca.Value);
    }

    // ---- states ----

    [Fact]
    public void Only_an_open_comanda_can_move_and_only_to_closed_cancelled_or_merged()
    {
        var todos = Enum.GetValues<EstadoDaComanda>();
        foreach (var de in todos)
        {
            foreach (var para in todos)
            {
                var esperado = de == EstadoDaComanda.Aberta && para is EstadoDaComanda.Fechada or EstadoDaComanda.Cancelada or EstadoDaComanda.Juntada;
                Assert.Equal(esperado, RegrasDeComanda.PodeTransitar(de, para));
            }
        }
    }

    [Fact]
    public void Every_state_word_round_trips()
    {
        foreach (var estado in Enum.GetValues<EstadoDaComanda>())
        {
            Assert.Equal(estado, RegrasDeComanda.Estado(RegrasDeComanda.Palavra(estado)));
        }

        foreach (var estado in Enum.GetValues<EstadoDoItem>())
        {
            Assert.Equal(estado, RegrasDeRodada.EstadoDe(RegrasDeRodada.Palavra(estado)));
        }
    }

    [Property]
    public bool The_next_number_is_always_greater_than_the_last_and_never_below_one(int? ultimo) =>
        RegrasDeComanda.ProximoNumero(ultimo) > (ultimo ?? 0) && RegrasDeComanda.ProximoNumero(ultimo) >= 1;

    // ---- the round ----

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool Consumption_counts_only_launched_items_and_does_not_depend_on_order(Preco[] precos, Qtd[] quantidades, bool[] canceladas)
    {
        ArgumentNullException.ThrowIfNull(precos);
        ArgumentNullException.ThrowIfNull(quantidades);
        ArgumentNullException.ThrowIfNull(canceladas);
        var n = new[] { precos.Length, quantidades.Length, canceladas.Length }.Min();
        var itens = Enumerable.Range(0, n)
            .Select(i => Item(precos[i].Value, quantidades[i].Value, canceladas[i] ? EstadoDoItem.Cancelado : EstadoDoItem.Lancado))
            .ToList();
        var esperado = itens.Where(i => i.Estado == EstadoDoItem.Lancado).Sum(i => i.PrecoCentavos * i.Quantidade);
        var invertido = RegrasDeRodada.Consumo(Enumerable.Reverse(itens));
        return RegrasDeRodada.Consumo(itens) == esperado && invertido == esperado;
    }

    [Property(Arbitrary = [typeof(SalaoGeradores)])]
    public bool Cancelling_an_item_removes_exactly_its_value_from_the_consumption(Preco preco, Qtd qtd, Preco outro)
    {
        var lancado = Item(preco.Value, qtd.Value, EstadoDoItem.Lancado);
        var intacto = Item(outro.Value, 1, EstadoDoItem.Lancado);
        var antes = RegrasDeRodada.Consumo([lancado, intacto]);
        var depois = RegrasDeRodada.Consumo([lancado with { Estado = EstadoDoItem.Cancelado }, intacto]);
        return antes - depois == preco.Value * qtd.Value;
    }

    [Property]
    public bool Truncating_to_microseconds_is_idempotent_and_never_moves_forward(long ticks, int minutosDeOffset)
    {
        var instante = new DateTimeOffset(Math.Abs(ticks % (DateTimeOffset.MaxValue.Ticks - TimeSpan.TicksPerDay * 2)) + TimeSpan.TicksPerDay, TimeSpan.FromMinutes(minutosDeOffset % 840));
        var truncado = RegrasDeRodada.Microssegundos(instante);
        return RegrasDeRodada.Microssegundos(truncado) == truncado
            && truncado <= instante
            && (instante - truncado).Ticks < 10
            && truncado.Offset == instante.Offset;
    }

    [Property]
    public bool The_undo_window_is_open_exactly_between_sending_and_the_limit(int depoisDeSegundos, int janelaSegundos)
    {
        var janela = Math.Abs(janelaSegundos % 3600);
        var delta = depoisDeSegundos % 7200;
        var aberta = RegrasDeRodada.DentroDaJanela(Base, Base.AddSeconds(delta), janela);
        return aberta == (delta >= 0 && delta <= janela);
    }

    [Property]
    public bool An_observation_is_trimmed_bounded_and_never_has_control_characters(string? bruto)
    {
        var ok = RegrasDeRodada.TentarObservacao(bruto, out var valor);
        if (!ok)
        {
            return true;
        }

        return valor is null
            || (valor.Length is >= 1 and <= RegrasDeRodada.TextoMaximo
                && valor == valor.Trim()
                && !valor.Any(char.IsControl)
                && RegrasDeRodada.TentarObservacao(valor, out var denovo)
                && denovo == valor);
    }

    [Property]
    public bool A_cancellation_reason_is_a_known_word_or_the_free_text_of_other(string? codigo, string? texto)
    {
        var motivo = RegrasDeRodada.MotivoDeItem(codigo, texto);
        if (motivo is null)
        {
            return true;
        }

        return codigo == RegrasDeRodada.Outro
            ? RegrasDeRodada.TentarObservacao(texto, out var livre) && livre == motivo
            : codigo is "cliente-desistiu" or "erro-de-lancamento" or "item-indisponivel";
    }

    // ---- table names and the menu ----

    [Property]
    public bool A_table_name_is_normalized_once_and_for_all(string? bruto)
    {
        var nome = RegrasDeMesa.Identificacao(bruto);
        return nome is null
            || (nome.Length is >= 1 and <= RegrasDeMesa.IdentificacaoMaxima
                && !nome.Contains("  ", StringComparison.Ordinal)
                && nome == nome.Trim()
                && !nome.Any(char.IsControl)
                && RegrasDeMesa.Identificacao(nome) == nome);
    }

    [Property]
    public bool A_product_code_is_upper_case_letters_digits_and_dashes_and_stable(string? bruto)
    {
        var codigo = RegrasDoCardapio.Codigo(bruto);
        return codigo is null
            || (codigo.Length is >= 1 and <= RegrasDoCardapio.CodigoMaximo
                && codigo[0] != '-'
                && codigo.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')
                && RegrasDoCardapio.Codigo(codigo) == codigo);
    }

    [Property]
    public bool A_product_name_is_trimmed_bounded_and_stable(string? bruto)
    {
        var nome = RegrasDoCardapio.NomeDeProduto(bruto);
        return nome is null
            || (nome.Length is >= 1 and <= RegrasDoCardapio.NomeMaximo
                && nome == nome.Trim()
                && !nome.Any(char.IsControl)
                && RegrasDoCardapio.NomeDeProduto(nome) == nome);
    }

    [Property]
    public bool A_price_is_valid_exactly_inside_the_limits(long centavos) =>
        RegrasDoCardapio.PrecoValido(centavos) == (centavos >= 0 && centavos <= RegrasDoCardapio.PrecoMaximoCentavos);

    // ---- boundaries the random strings almost never reach (found by the mutation test, F4-0106) ----

    private static int Dentro(int n, int modulo) => Math.Abs(n % modulo);

    [Property]
    public bool A_table_name_is_accepted_exactly_from_one_to_the_maximum_length(int n, int posicao)
    {
        var tamanho = Dentro(n, RegrasDeMesa.IdentificacaoMaxima + 20);
        var nome = new string('a', tamanho);
        var aceito = RegrasDeMesa.Identificacao(nome) is not null == (tamanho is >= 1 and <= RegrasDeMesa.IdentificacaoMaxima);
        var comControle = tamanho == 0 ? "\t" : nome.Insert(Dentro(posicao, tamanho + 1), "\n");
        var comEspacos = RegrasDeMesa.Identificacao("  " + nome + "   ") == RegrasDeMesa.Identificacao(nome);
        return aceito && RegrasDeMesa.Identificacao(comControle) is null && comEspacos;
    }

    [Property]
    public bool An_observation_is_accepted_up_to_the_maximum_length_blank_means_none_and_control_is_refused(int n, int posicao)
    {
        var tamanho = Dentro(n, RegrasDeRodada.TextoMaximo + 60);
        var texto = new string('a', tamanho);
        var ok = RegrasDeRodada.TentarObservacao(texto, out var valor);
        var limite = ok == tamanho <= RegrasDeRodada.TextoMaximo && (!ok || valor == (tamanho == 0 ? null : texto));
        var branca = RegrasDeRodada.TentarObservacao(new string(' ', tamanho), out var nenhum) && nenhum is null;
        var controle = !RegrasDeRodada.TentarObservacao(texto.Insert(Dentro(posicao, tamanho + 1), "\u0007"), out _);
        return limite && branca && controle;
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(199, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void The_observation_limit_is_exactly_200(int tamanho, bool aceita) =>
        Assert.Equal(aceita, RegrasDeRodada.TentarObservacao(new string('a', tamanho), out _));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(39, true)]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void The_table_name_limit_is_exactly_1_to_40(int tamanho, bool aceita) =>
        Assert.Equal(aceita, RegrasDeMesa.Identificacao(new string('a', tamanho)) is not null);

    [Theory]
    [InlineData("cliente-desistiu", "cliente desistiu")]
    [InlineData("erro-de-lancamento", "erro de lançamento")]
    [InlineData("item-indisponivel", "item indisponível")]
    public void Each_standard_cancellation_code_has_its_fixed_words_and_ignores_any_text(string codigo, string palavras)
    {
        Assert.Equal(palavras, RegrasDeRodada.MotivoDeItem(codigo, "qualquer coisa"));
        Assert.Equal(palavras, RegrasDeRodada.MotivoDeItem(codigo, null));
    }

    [Property]
    public bool A_code_outside_the_list_never_gives_a_reason_and_other_needs_valid_text(string? codigo, string? texto)
    {
        var known = codigo is "cliente-desistiu" or "erro-de-lancamento" or "item-indisponivel" or "outro";
        var motivo = RegrasDeRodada.MotivoDeItem(codigo, texto);
        var outroSemTexto = RegrasDeRodada.MotivoDeItem("outro", null) is null && RegrasDeRodada.MotivoDeItem("outro", "   ") is null;
        return (known || motivo is null) && outroSemTexto && RegrasDeRodada.MotivoDeItem("outro", "  porque sim ") == "porque sim";
    }

    // ---- menu boundaries (mutation test, F4-0106) ----

    [Theory]
    [InlineData("A", "A")]
    [InlineData("ab-1", "AB-1")]
    [InlineData("  pizza  ", "PIZZA")]
    [InlineData("A-", "A-")]
    [InlineData("Z9", "Z9")]
    [InlineData("0", "0")]
    [InlineData("-A", null)]
    [InlineData("-", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("A@", null)]
    [InlineData("A[", null)]
    [InlineData("A/", null)]
    [InlineData("A:", null)]
    [InlineData("A B", null)]
    [InlineData("Á", null)]
    public void A_product_code_keeps_only_upper_letters_digits_and_inner_dashes(string? bruto, string? esperado) =>
        Assert.Equal(esperado, RegrasDoCardapio.Codigo(bruto));

    [Theory]
    [InlineData(1, true)]
    [InlineData(19, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void A_product_code_has_at_most_20_characters(int tamanho, bool aceito) =>
        Assert.Equal(aceito, RegrasDoCardapio.Codigo(new string('A', tamanho)) is not null);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(119, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void A_product_name_has_1_to_120_characters(int tamanho, bool aceito) =>
        Assert.Equal(aceito, RegrasDoCardapio.NomeDeProduto(new string('a', tamanho)) is not null);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(79, true)]
    [InlineData(80, true)]
    [InlineData(81, false)]
    public void A_category_name_has_1_to_80_characters(int tamanho, bool aceito) =>
        Assert.Equal(aceito, RegrasDoCardapio.NomeDeCategoria(new string('a', tamanho)) is not null);

    [Theory]
    [InlineData("Pastel de queijo", "Pastel de queijo")]
    [InlineData("  Pastel  ", "Pastel")]
    [InlineData("Past\nel", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_name_is_trimmed_and_refused_when_blank_or_with_control_characters(string? bruto, string? esperado) =>
        Assert.Equal(esperado, RegrasDoCardapio.NomeDeProduto(bruto));

    [Theory]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(1L, true)]
    [InlineData(99_999_999L, true)]
    [InlineData(100_000_000L, true)]
    [InlineData(100_000_001L, false)]
    public void A_price_is_valid_from_zero_to_the_maximum_inclusive(long centavos, bool valido) =>
        Assert.Equal(valido, RegrasDoCardapio.PrecoValido(centavos));

    // ---- cash register boundaries (mutation test, F4-0106) ----

    [Theory]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(1L, true)]
    [InlineData(99_999_999L, true)]
    [InlineData(100_000_000L, true)]
    [InlineData(100_000_001L, false)]
    public void A_register_amount_is_valid_from_zero_to_the_maximum_inclusive(long centavos, bool valido) =>
        Assert.Equal(valido, RegrasDoCaixa.ValorValido(centavos));

    [Theory]
    [InlineData(0L, "Confere")]
    [InlineData(-1L, "Falta R$ 0,01")]
    [InlineData(-5L, "Falta R$ 0,05")]
    [InlineData(1L, "Sobra R$ 0,01")]
    [InlineData(100L, "Sobra R$ 1,00")]
    [InlineData(150L, "Sobra R$ 1,50")]
    [InlineData(-123456L, "Falta R$ 1.234,56")]
    [InlineData(123456L, "Sobra R$ 1.234,56")]
    public void The_difference_sentence_is_exact(long diferenca, string frase) =>
        Assert.Equal(frase, RegrasDoCaixa.Palavra(diferenca));

    private static ItemDaComanda Item(long preco, int quantidade, EstadoDoItem estado) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), "x", preco, quantidade, null, estado, null, null, null);
}
