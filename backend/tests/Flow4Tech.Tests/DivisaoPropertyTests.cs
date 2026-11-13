using Flow4Tech.Comandas.Domain;
using FsCheck.Xunit;

namespace Flow4Tech.Tests;

/// <summary>F4-0061: property tests of splitting a bill. For EVERY input the parts add up to the total, none is negative, the result is deterministic and equal parts differ by at most one cent. No I/O.</summary>
public sealed class DivisaoPropertyTests
{
    private static readonly Guid Comanda = Guid.NewGuid();

    private static List<long> Pesos(byte[] bruto) => bruto.Take(RegrasDeDivisao.PartesMaximo).Select(b => (long)b).ToList();

    private static ItemDaComanda Item(long preco, int? assento) =>
        new(Guid.NewGuid(), Comanda, Guid.NewGuid(), 1, Guid.NewGuid(), "P", preco, 1, null, EstadoDoItem.Lancado, null, null, null, Assento: assento);

    [Property]
    public bool Sharing_by_weights_adds_up_never_goes_negative_and_is_deterministic(uint total, byte[] bruto)
    {
        var pesos = Pesos(bruto);
        if (pesos.Count == 0)
        {
            return true;
        }

        var a = RegrasDeDivisao.Distribuir(total, pesos);
        var b = RegrasDeDivisao.Distribuir(total, pesos);
        return a.Sum() == total && a.All(p => p >= 0) && a.SequenceEqual(b) && a.Count == pesos.Count;
    }

    [Property]
    public bool A_weight_of_zero_gets_nothing_unless_every_weight_is_zero(uint total, byte[] bruto)
    {
        var pesos = Pesos(bruto);
        if (pesos.Count == 0 || pesos.All(p => p == 0))
        {
            return true;
        }

        var partes = RegrasDeDivisao.Distribuir(total, pesos);
        return pesos.Select((p, i) => p > 0 || partes[i] == 0).All(x => x);
    }

    [Property]
    public bool Equal_parts_add_up_and_differ_by_at_most_one_cent_with_the_extra_cents_first(uint total, byte quantas)
    {
        var n = (quantas % RegrasDeDivisao.PartesMaximo) + 1;
        var partes = RegrasDeDivisao.PartesIguais(total, n);
        return partes.Sum() == total && partes.Max() - partes.Min() <= 1 && partes.SequenceEqual(partes.OrderByDescending(p => p));
    }

    [Property]
    public bool Every_mode_gives_parts_that_add_up_to_the_bill_the_fee_and_the_tip(byte[] precos, byte taxaDezenas, uint gorjeta, byte quantas, bool conferirItens)
    {
        var n = (quantas % (RegrasDeDivisao.PartesMaximo - 1)) + 2;
        var itens = precos.Take(30).Select((p, i) => Item(p * 10L, (i % 3) + 1)).ToList();
        var conta = RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens), (taxaDezenas % 201) / 10m, false, 0);
        var tip = gorjeta % 100_000;
        var pedidos = new List<PedidoDeDivisao>
        {
            new(ModoDeDivisao.Iguais, Partes: n, GorjetaCentavos: tip),
            new(ModoDeDivisao.Percentual, Percentuais: Enumerable.Range(0, n).Select(i => i == 0 ? 100m - ((n - 1) * 0.01m) : 0.01m).ToList(), GorjetaCentavos: tip),
            new(ModoDeDivisao.Valores, Valores: Enumerable.Range(0, n).Select(i => i == 0 ? conta.TotalCentavos : 0L).ToList(), GorjetaCentavos: tip),
        };
        if (itens.Count >= 3)
        {
            pedidos.Add(new PedidoDeDivisao(ModoDeDivisao.Assento, GorjetaCentavos: tip));
            pedidos.Add(new PedidoDeDivisao(ModoDeDivisao.Item, Grupos: [[itens[0].Id], [.. itens.Skip(1).Select(i => i.Id)]], GorjetaCentavos: tip));
        }

        return pedidos.All(pedido =>
        {
            var r = RegrasDeDivisao.Dividir(pedido, itens, conta);
            var de = RegrasDeDivisao.Dividir(pedido, itens, conta);
            return r.Recusa == RecusaDeDivisao.Nenhuma
                && r.Partes.Sum(p => p.TotalCentavos) == conta.TotalCentavos
                && r.Partes.Sum(p => p.TaxaServicoCentavos) == conta.TaxaServicoCentavos
                && r.Partes.Sum(p => p.GorjetaCentavos) == tip
                && r.Partes.Sum(p => p.ConsumoCentavos) == conta.TotalCentavos - conta.TaxaServicoCentavos
                && r.Partes.All(p => p.TotalCentavos >= 0 && p.ConsumoCentavos >= 0 && p.TaxaServicoCentavos >= 0 && p.GorjetaCentavos >= 0 && p.TotalCentavos == p.ConsumoCentavos + p.TaxaServicoCentavos)
                && r.Partes.Select(p => p.TotalCentavos).SequenceEqual(de.Partes.Select(p => p.TotalCentavos));
        });
    }

    [Property]
    public bool Equal_mode_totals_and_tips_each_differ_by_at_most_one_cent(byte[] precos, byte taxaDezenas, uint gorjeta, byte quantas)
    {
        var n = (quantas % (RegrasDeDivisao.PartesMaximo - 1)) + 2;
        var itens = precos.Take(30).Select(p => Item(p * 10L, null)).ToList();
        var conta = RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens), (taxaDezenas % 201) / 10m, false, 0);

        var r = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Iguais, Partes: n, GorjetaCentavos: gorjeta % 100_000), itens, conta);

        var totais = r.Partes.Select(p => p.TotalCentavos).ToList();
        var gorjetas = r.Partes.Select(p => p.GorjetaCentavos).ToList();
        return totais.Max() - totais.Min() <= 1 && gorjetas.Max() - gorjetas.Min() <= 1;
    }

    [Property]
    public bool Free_values_and_percents_that_do_not_add_up_are_always_refused(byte a, byte b)
    {
        var itens = new[] { Item(1000, null) };
        var conta = RegrasDeComanda.Conta(1000, 0m, false, 0);
        var valores = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Valores, Valores: [a, b]), itens, conta);
        var pct = RegrasDeDivisao.Dividir(new PedidoDeDivisao(ModoDeDivisao.Percentual, Percentuais: [a / 10m, b / 10m]), itens, conta);
        return valores.Recusa == RecusaDeDivisao.SomaDiferente && (pct.Recusa == RecusaDeDivisao.SomaDiferente || pct.Recusa == RecusaDeDivisao.Invalida);
    }
}
