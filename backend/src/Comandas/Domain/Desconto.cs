namespace Flow4Tech.Comandas.Domain;

/// <summary>How an item is charged: <c>Normal</c> at its frozen price, <c>Cortesia</c> or <c>Brinde</c> at zero (the price stays on the row for reports and the future stock write-off).</summary>
public enum TipoDoItem
{
    Normal,
    Cortesia,
    Brinde,
}

public enum FormaDoDesconto
{
    Percentual,
    Valor,
}

/// <summary>
/// A discount given on a comanda. <see cref="ItemId"/> null means the whole comanda. <see cref="AplicadoCentavos"/> is the amount the SERVER computed when it was given (from the percent or the value
/// and the stored comanda); the bill uses it, clamped at calculation time so the total never goes below zero.
/// </summary>
public sealed record Desconto(
    Guid Id,
    Guid ComandaId,
    Guid? ItemId,
    FormaDoDesconto Forma,
    decimal? Percentual,
    long? ValorCentavos,
    long AplicadoCentavos,
    string Motivo,
    Guid CriadoPor,
    DateTimeOffset CriadoEm);

/// <summary>What the discounts took off the consumption: those given on items and those given on the whole comanda (each clamped so the bill is never negative).</summary>
public sealed record DescontosDaConta(long DeItensCentavos, long DaComandaCentavos)
{
    public long TotalCentavos => DeItensCentavos + DaComandaCentavos;
}

/// <summary>Pure rules of courtesy, gift and discounts (no I/O). Order of the bill: ADR-018.</summary>
public static class RegrasDeDesconto
{
    public const decimal PercentualMaximo = 100m;

    public static string Palavra(TipoDoItem tipo) => tipo switch
    {
        TipoDoItem.Normal => "normal",
        TipoDoItem.Cortesia => "cortesia",
        TipoDoItem.Brinde => "brinde",
        _ => throw new ArgumentOutOfRangeException(nameof(tipo)),
    };

    public static TipoDoItem Tipo(string palavra) => palavra switch
    {
        "normal" => TipoDoItem.Normal,
        "cortesia" => TipoDoItem.Cortesia,
        "brinde" => TipoDoItem.Brinde,
        _ => throw new ArgumentOutOfRangeException(nameof(palavra)),
    };

    public static string Palavra(FormaDoDesconto forma) => forma == FormaDoDesconto.Percentual ? "percentual" : "valor";

    public static FormaDoDesconto Forma(string palavra) => palavra switch
    {
        "percentual" => FormaDoDesconto.Percentual,
        "valor" => FormaDoDesconto.Valor,
        _ => throw new ArgumentOutOfRangeException(nameof(palavra)),
    };

    /// <summary>A percent is between 0 (exclusive) and 100 and has at most 2 decimals (what the column stores).</summary>
    public static bool PercentualValido(decimal percentual) => percentual is > 0m and <= PercentualMaximo && decimal.Round(percentual, 2) == percentual;

    public static bool ValorValido(long centavos) => centavos >= 1;

    /// <summary>The ONE place a percent becomes cents: <paramref name="percentual"/> of <paramref name="baseCentavos"/>, half up (0.5 cent goes up).</summary>
    public static long DoPercentual(long baseCentavos, decimal percentual) =>
        (long)Math.Round(Math.Max(0, baseCentavos) * percentual / 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>The percent a value is of its base, exact (what the engine measures a percent limit against); 0 when there is nothing to take it from.</summary>
    public static decimal PercentualDoValor(long valorCentavos, long baseCentavos) =>
        baseCentavos <= 0 ? 0m : valorCentavos * 100m / baseCentavos;

    /// <summary>
    /// What the discounts take off, in the fixed order: each item discount from its line (never below zero, in the order they were given), then each comanda discount from what is left of the
    /// consumption. Items that are cancelled, or that are courtesy or gift (their line is zero), take nothing.
    /// </summary>
    public static DescontosDaConta Calcular(IEnumerable<ItemDaComanda> itens, IEnumerable<Desconto> descontos)
    {
        ArgumentNullException.ThrowIfNull(itens);
        ArgumentNullException.ThrowIfNull(descontos);
        var restante = itens.Where(i => i.Estado == EstadoDoItem.Lancado).ToDictionary(i => i.Id, i => i.TotalCentavos);
        var lista = descontos.ToList();
        long deItens = 0;
        foreach (var d in lista.Where(d => d.ItemId is not null))
        {
            var linha = restante.GetValueOrDefault(d.ItemId!.Value);
            var tira = Math.Min(Math.Max(0, d.AplicadoCentavos), linha);
            restante[d.ItemId.Value] = linha - tira;
            deItens += tira;
        }

        var sobra = restante.Values.Sum();
        long daComanda = 0;
        foreach (var d in lista.Where(d => d.ItemId is null))
        {
            var tira = Math.Min(Math.Max(0, d.AplicadoCentavos), sobra);
            sobra -= tira;
            daComanda += tira;
        }

        return new DescontosDaConta(deItens, daComanda);
    }

    /// <summary>What is still chargeable on one item: its line minus the discounts already given on it. The most a new discount on the item can take.</summary>
    public static long BaseDoItem(ItemDaComanda item, IEnumerable<Desconto> descontos)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(descontos);
        var resto = item.Estado == EstadoDoItem.Lancado ? item.TotalCentavos : 0;
        foreach (var d in descontos.Where(d => d.ItemId == item.Id))
        {
            resto -= Math.Min(Math.Max(0, d.AplicadoCentavos), resto);
        }

        return resto;
    }

    /// <summary>What is still chargeable on the whole comanda: the consumption minus every discount already given. The most a new comanda discount can take.</summary>
    public static long BaseDaComanda(IReadOnlyCollection<ItemDaComanda> itens, IReadOnlyCollection<Desconto> descontos) =>
        RegrasDeRodada.Consumo(itens) - Calcular(itens, descontos).TotalCentavos;
}
