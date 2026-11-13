using Flow4Tech.Pagamentos.Domain;
using FsCheck.Xunit;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0063: property tests of the receipt rule. For EVERY balance and set of lines: the applied cents never pass the balance, cash plus the other methods add up to what was paid, change exists only
/// on cash lines, the tip never moves the applied cents, and the rule is deterministic. No I/O.
/// </summary>
public sealed class RecebimentoMistoPropertyTests
{
    private static long Saldo(uint bruto) => (bruto % 2_000_000) + 1;

    private static List<LinhaDeRecebimento> Linhas(byte[] valores, bool[] dinheiro, byte[] gorjetas) =>
        valores.Take(RegrasDeRecebimentoMisto.MaximoDeLinhas).Select((v, i) => new LinhaDeRecebimento(dinheiro.Length > 0 && dinheiro[i % dinheiro.Length], (v * 137L) + 1, gorjetas.Length > 0 ? gorjetas[i % gorjetas.Length] * 11L : 0)).ToList();

    [Property]
    public bool Applied_never_passes_the_balance_every_line_applies_something_and_received_is_applied_plus_change(uint bruto, byte[] valores, bool[] dinheiro, byte[] gorjetas)
    {
        var saldo = Saldo(bruto);
        var linhas = Linhas(valores, dinheiro, gorjetas);
        var r = RegrasDeRecebimentoMisto.Ratear(saldo, linhas);
        return r.Recusa != RecusaDeRecebimento.Nenhuma
            || (r.Linhas.Sum(l => l.AplicadoCentavos) <= saldo && r.Linhas.All(l => l.AplicadoCentavos >= 1 && l.RecebidoCentavos == l.AplicadoCentavos + l.TrocoCentavos) && r.Linhas.Count == linhas.Count);
    }

    [Property]
    public bool Cash_plus_the_other_methods_add_up_to_what_was_applied_and_the_other_methods_apply_all_they_carry(uint bruto, byte[] valores, bool[] dinheiro)
    {
        var linhas = Linhas(valores, dinheiro, []);
        var r = RegrasDeRecebimentoMisto.Ratear(Saldo(bruto), linhas);
        if (r.Recusa != RecusaDeRecebimento.Nenhuma)
        {
            return true;
        }

        var pares = linhas.Zip(r.Linhas).ToList();
        var dinheiroAplicado = pares.Where(p => p.First.Dinheiro).Sum(p => p.Second.AplicadoCentavos);
        var outrosAplicado = pares.Where(p => !p.First.Dinheiro).Sum(p => p.Second.AplicadoCentavos);
        return dinheiroAplicado + outrosAplicado == r.Linhas.Sum(l => l.AplicadoCentavos)
            && outrosAplicado == pares.Where(p => !p.First.Dinheiro).Sum(p => p.First.RecebidoCentavos);
    }

    [Property]
    public bool Change_only_exists_on_cash_lines(uint bruto, byte[] valores, bool[] dinheiro)
    {
        var linhas = Linhas(valores, dinheiro, []);
        var r = RegrasDeRecebimentoMisto.Ratear(Saldo(bruto), linhas);
        return r.Recusa != RecusaDeRecebimento.Nenhuma || linhas.Zip(r.Linhas).All(p => p.First.Dinheiro || p.Second.TrocoCentavos == 0);
    }

    [Property]
    public bool The_tip_stays_outside_the_balance_and_never_changes_what_is_applied(uint bruto, byte[] valores, bool[] dinheiro, byte[] gorjetas)
    {
        var saldo = Saldo(bruto);
        var com = RegrasDeRecebimentoMisto.Ratear(saldo, Linhas(valores, dinheiro, gorjetas));
        var sem = RegrasDeRecebimentoMisto.Ratear(saldo, Linhas(valores, dinheiro, []));
        return com.Recusa == sem.Recusa
            && com.Linhas.Select(l => (l.RecebidoCentavos, l.AplicadoCentavos, l.TrocoCentavos)).SequenceEqual(sem.Linhas.Select(l => (l.RecebidoCentavos, l.AplicadoCentavos, l.TrocoCentavos)));
    }

    [Property]
    public bool A_receipt_that_totals_no_more_than_the_balance_is_always_accepted_as_is_whatever_the_methods(uint bruto, byte[] valores, bool[] dinheiro)
    {
        var saldo = Saldo(bruto);
        var linhas = Linhas(valores, dinheiro, []);
        if (linhas.Count == 0 || linhas.Sum(l => l.RecebidoCentavos) > saldo)
        {
            return true;
        }

        var r = RegrasDeRecebimentoMisto.Ratear(saldo, linhas);
        return r.Recusa == RecusaDeRecebimento.Nenhuma && r.Linhas.All(l => l.TrocoCentavos == 0 && l.AplicadoCentavos == l.RecebidoCentavos);
    }

    [Property]
    public bool Without_cash_a_receipt_above_the_balance_is_refused(uint bruto, byte[] valores)
    {
        var saldo = Saldo(bruto);
        var linhas = Linhas(valores, [false], []);
        return linhas.Count == 0 || linhas.Sum(l => l.RecebidoCentavos) <= saldo || RegrasDeRecebimentoMisto.Ratear(saldo, linhas).Recusa == RecusaDeRecebimento.ExcedeSaldo;
    }

    [Property]
    public bool The_rule_is_deterministic(uint bruto, byte[] valores, bool[] dinheiro, byte[] gorjetas)
    {
        var saldo = Saldo(bruto);
        var linhas = Linhas(valores, dinheiro, gorjetas);
        var a = RegrasDeRecebimentoMisto.Ratear(saldo, linhas);
        var b = RegrasDeRecebimentoMisto.Ratear(saldo, linhas);
        return a.Recusa == b.Recusa && a.Linhas.SequenceEqual(b.Linhas);
    }
}
