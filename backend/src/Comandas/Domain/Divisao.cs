using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Flow4Tech.Comandas.Domain;

/// <summary>The five ways to split a bill.</summary>
public enum ModoDeDivisao
{
    /// <summary>N equal parts; the remainder cents go to the first parts.</summary>
    Iguais,

    /// <summary>One part per seat of the launched items; items without a seat are shared equally by all the seats.</summary>
    Assento,

    /// <summary>The caller groups the launched items: one part per group, every item in exactly one group.</summary>
    Item,

    /// <summary>Free values per part; they must add up to the bill.</summary>
    Valores,

    /// <summary>A percent per part (two decimals); they must add up to 100.</summary>
    Percentual,
}

/// <summary>
/// One part of a split. <see cref="TotalCentavos"/> is what the part owes of the bill (net consumption plus service fee, so the parts add up to the bill); <see cref="GorjetaCentavos"/> is its share of the tip,
/// which stays outside the bill (ADR-018), so what the part pays is the two together. <see cref="Assento"/> is set in the seat mode; <see cref="ItemIds"/> in the seat and item modes.
/// </summary>
public sealed record ParteDaDivisao(int Numero, int? Assento, IReadOnlyList<Guid> ItemIds, long ConsumoCentavos, long TaxaServicoCentavos, long TotalCentavos, long GorjetaCentavos)
{
    public long APagarCentavos => TotalCentavos + GorjetaCentavos;
}

/// <summary>What the caller asks for; only the fields of the chosen <see cref="Modo"/> are read.</summary>
public sealed record PedidoDeDivisao(
    ModoDeDivisao Modo,
    int? Partes = null,
    IReadOnlyList<IReadOnlyList<Guid>>? Grupos = null,
    IReadOnlyList<long>? Valores = null,
    IReadOnlyList<decimal>? Percentuais = null,
    long GorjetaCentavos = 0);

public enum RecusaDeDivisao
{
    Nenhuma,

    /// <summary>The request is malformed for its mode (part count, empty group, repeated or unknown item, negative value, no seat...).</summary>
    Invalida,

    /// <summary>Free values do not add up to the bill, or percents do not add up to 100.</summary>
    SomaDiferente,

    /// <summary>Some launched item is in no group (item mode).</summary>
    ItensForaDasPartes,
}

public sealed record ResultadoDaDivisao(RecusaDeDivisao Recusa, IReadOnlyList<ParteDaDivisao> Partes)
{
    public static ResultadoDaDivisao De(RecusaDeDivisao recusa) => new(recusa, []);
}

/// <summary>
/// Pure rules of splitting a bill (no I/O). ONE rounding rule everywhere: a total is shared by weights with each part getting the floor of its share, and the remaining cents go one each to the first
/// parts (in the stable order of the parts) that have a weight. So the parts always add up to the total, never go negative, the result is deterministic, and equal parts differ by at most one cent.
/// The bill is the one of ADR-018 (consumption, discounts, service fee); the tip follows the same rule over the weights of the chosen mode, and the fee over the part totals (so a part never owes more fee than its total).
/// </summary>
public static class RegrasDeDivisao
{
    public const int PartesMinimo = 2;

    public const int PartesMaximo = 50;

    public const int AssentoMaximo = 99;

    public const long GorjetaMaximaCentavos = 1_000_000_000;

    /// <summary>Basis points of 100 percent with two decimals.</summary>
    private const long CemPorCentoEmCentesimos = 10_000;

    public static string Palavra(ModoDeDivisao modo) => modo switch
    {
        ModoDeDivisao.Iguais => "iguais",
        ModoDeDivisao.Assento => "assento",
        ModoDeDivisao.Item => "item",
        ModoDeDivisao.Valores => "valores",
        ModoDeDivisao.Percentual => "percentual",
        _ => throw new ArgumentOutOfRangeException(nameof(modo)),
    };

    /// <summary>The mode of a word; false when unknown.</summary>
    public static bool TentarModo(string? palavra, out ModoDeDivisao modo)
    {
        foreach (var candidato in Enum.GetValues<ModoDeDivisao>())
        {
            if (string.Equals(palavra, Palavra(candidato), StringComparison.Ordinal))
            {
                modo = candidato;
                return true;
            }
        }

        modo = default;
        return false;
    }

    public static bool AssentoValido(int? assento) => assento is null or (>= 1 and <= AssentoMaximo);

    /// <summary>The ONE function that shares cents: <paramref name="totalCentavos"/> by <paramref name="pesos"/>. All weights zero share it equally.</summary>
    public static IReadOnlyList<long> Distribuir(long totalCentavos, IReadOnlyList<long> pesos)
    {
        ArgumentNullException.ThrowIfNull(pesos);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCentavos);
        if (pesos.Count == 0 || pesos.Any(p => p < 0))
        {
            throw new ArgumentException("There must be at least one weight and none negative.", nameof(pesos));
        }

        var soma = pesos.Aggregate((Int128)0, (a, p) => a + p);
        var efetivos = soma == 0 ? pesos.Select(_ => 1L).ToList() : pesos;
        var somaEfetiva = soma == 0 ? efetivos.Count : soma;
        var partes = efetivos.Select(p => (long)(totalCentavos * (Int128)p / somaEfetiva)).ToArray();
        var resto = totalCentavos - partes.Sum();
        for (var i = 0; resto > 0; i = (i + 1) % partes.Length)
        {
            if (efetivos[i] > 0)
            {
                partes[i]++;
                resto--;
            }
        }

        return partes;
    }

    /// <summary>Equal parts: the remainder cents go one each to the first parts.</summary>
    public static IReadOnlyList<long> PartesIguais(long totalCentavos, int partes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partes, 1);
        return Distribuir(totalCentavos, Enumerable.Repeat(1L, partes).ToList());
    }

    /// <summary>The split of <paramref name="conta"/> as asked. Nothing is stored here; the preview and the record both come from this one function.</summary>
    public static ResultadoDaDivisao Dividir(PedidoDeDivisao pedido, IReadOnlyList<ItemDaComanda> itens, ContaDaComanda conta)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        ArgumentNullException.ThrowIfNull(itens);
        ArgumentNullException.ThrowIfNull(conta);
        if (pedido.GorjetaCentavos is < 0 or > GorjetaMaximaCentavos || conta.TotalCentavos < 0)
        {
            return ResultadoDaDivisao.De(RecusaDeDivisao.Invalida);
        }

        var lancados = itens.Where(i => i.Estado == EstadoDoItem.Lancado).ToList();
        var (recusa, pesos, itensPorParte, assentos) = pedido.Modo switch
        {
            ModoDeDivisao.Iguais => Iguais(pedido),
            ModoDeDivisao.Assento => PorAssento(lancados),
            ModoDeDivisao.Item => PorItem(pedido, lancados),
            ModoDeDivisao.Valores => Livres(pedido, conta.TotalCentavos),
            ModoDeDivisao.Percentual => Percentuais(pedido),
            _ => (RecusaDeDivisao.Invalida, [], [], []),
        };
        if (recusa != RecusaDeDivisao.Nenhuma)
        {
            return ResultadoDaDivisao.De(recusa);
        }

        var totais = Distribuir(conta.TotalCentavos, pesos);
        var taxas = Distribuir(Math.Min(conta.TaxaServicoCentavos, conta.TotalCentavos), totais);
        var gorjetas = Distribuir(pedido.GorjetaCentavos, pesos);
        var partes = totais.Select((t, i) => new ParteDaDivisao(i + 1, assentos[i], itensPorParte[i], t - taxas[i], taxas[i], t, gorjetas[i])).ToList();
        return new ResultadoDaDivisao(RecusaDeDivisao.Nenhuma, partes);
    }

    /// <summary>A fingerprint of what the bill is made of (launched items with their lines and seats, discounts, fee rule): a split is valid only while it still matches. Deterministic and order independent.</summary>
    public static string Assinatura(IEnumerable<ItemDaComanda> itens, IEnumerable<Desconto> descontos, decimal taxaPercentual, bool taxaRemovida)
    {
        ArgumentNullException.ThrowIfNull(itens);
        ArgumentNullException.ThrowIfNull(descontos);
        var texto = new StringBuilder();
        foreach (var i in itens.Where(i => i.Estado == EstadoDoItem.Lancado).OrderBy(i => i.Id))
        {
            texto.Append(CultureInfo.InvariantCulture, $"i:{i.Id:N}:{i.TotalCentavos}:{i.Assento};");
        }

        foreach (var d in descontos.OrderBy(d => d.Id))
        {
            texto.Append(CultureInfo.InvariantCulture, $"d:{d.Id:N}:{d.AplicadoCentavos};");
        }

        texto.Append(CultureInfo.InvariantCulture, $"t:{taxaPercentual}:{taxaRemovida}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto.ToString())));
    }

    private static (RecusaDeDivisao, IReadOnlyList<long>, IReadOnlyList<IReadOnlyList<Guid>>, IReadOnlyList<int?>) Iguais(PedidoDeDivisao pedido)
    {
        if (pedido.Partes is not { } n || n is < PartesMinimo or > PartesMaximo)
        {
            return Recusa(RecusaDeDivisao.Invalida);
        }

        return Montar(Enumerable.Repeat(1L, n).ToList(), n);
    }

    private static (RecusaDeDivisao, IReadOnlyList<long>, IReadOnlyList<IReadOnlyList<Guid>>, IReadOnlyList<int?>) PorAssento(List<ItemDaComanda> lancados)
    {
        var assentos = lancados.Where(i => i.Assento is not null).Select(i => i.Assento!.Value).Distinct().Order().ToList();
        if (assentos.Count is < PartesMinimo or > PartesMaximo)
        {
            return Recusa(RecusaDeDivisao.Invalida);
        }

        var compartilhado = Distribuir(lancados.Where(i => i.Assento is null).Sum(i => i.TotalCentavos), Enumerable.Repeat(1L, assentos.Count).ToList());
        var pesos = assentos.Select((a, k) => lancados.Where(i => i.Assento == a).Sum(i => i.TotalCentavos) + compartilhado[k]).ToList();
        var ids = assentos.Select(a => (IReadOnlyList<Guid>)lancados.Where(i => i.Assento == a).Select(i => i.Id).ToList()).ToList();
        return (RecusaDeDivisao.Nenhuma, pesos, ids, assentos.Select(a => (int?)a).ToList());
    }

    private static (RecusaDeDivisao, IReadOnlyList<long>, IReadOnlyList<IReadOnlyList<Guid>>, IReadOnlyList<int?>) PorItem(PedidoDeDivisao pedido, List<ItemDaComanda> lancados)
    {
        if (pedido.Grupos is not { Count: >= PartesMinimo and <= PartesMaximo } grupos || grupos.Any(g => g is null || g.Count == 0))
        {
            return Recusa(RecusaDeDivisao.Invalida);
        }

        var todos = grupos.SelectMany(g => g).ToList();
        if (todos.Distinct().Count() != todos.Count || todos.Any(id => lancados.All(i => i.Id != id)))
        {
            return Recusa(RecusaDeDivisao.Invalida);
        }

        if (lancados.Any(i => !todos.Contains(i.Id)))
        {
            return Recusa(RecusaDeDivisao.ItensForaDasPartes);
        }

        var pesos = grupos.Select(g => lancados.Where(i => g.Contains(i.Id)).Sum(i => i.TotalCentavos)).ToList();
        return (RecusaDeDivisao.Nenhuma, pesos, grupos.Select(g => (IReadOnlyList<Guid>)[.. g]).ToList(), grupos.Select(_ => (int?)null).ToList());
    }

    private static (RecusaDeDivisao, IReadOnlyList<long>, IReadOnlyList<IReadOnlyList<Guid>>, IReadOnlyList<int?>) Livres(PedidoDeDivisao pedido, long totalCentavos)
    {
        if (pedido.Valores is not { Count: >= PartesMinimo and <= PartesMaximo } valores || valores.Any(v => v < 0))
        {
            return Recusa(RecusaDeDivisao.Invalida);
        }

        return valores.Sum() == totalCentavos ? Montar([.. valores], valores.Count) : Recusa(RecusaDeDivisao.SomaDiferente);
    }

    private static (RecusaDeDivisao, IReadOnlyList<long>, IReadOnlyList<IReadOnlyList<Guid>>, IReadOnlyList<int?>) Percentuais(PedidoDeDivisao pedido)
    {
        if (pedido.Percentuais is not { Count: >= PartesMinimo and <= PartesMaximo } percentuais || percentuais.Any(p => p is < 0m or > 100m || decimal.Round(p, 2) != p))
        {
            return Recusa(RecusaDeDivisao.Invalida);
        }

        var centesimos = percentuais.Select(p => (long)(p * 100m)).ToList();
        return centesimos.Sum() == CemPorCentoEmCentesimos ? Montar(centesimos, centesimos.Count) : Recusa(RecusaDeDivisao.SomaDiferente);
    }

    private static (RecusaDeDivisao, IReadOnlyList<long>, IReadOnlyList<IReadOnlyList<Guid>>, IReadOnlyList<int?>) Montar(IReadOnlyList<long> pesos, int n) =>
        (RecusaDeDivisao.Nenhuma, pesos, Enumerable.Range(0, n).Select(_ => (IReadOnlyList<Guid>)[]).ToList(), Enumerable.Repeat((int?)null, n).ToList());

    private static (RecusaDeDivisao, IReadOnlyList<long>, IReadOnlyList<IReadOnlyList<Guid>>, IReadOnlyList<int?>) Recusa(RecusaDeDivisao recusa) => (recusa, [], [], []);
}
