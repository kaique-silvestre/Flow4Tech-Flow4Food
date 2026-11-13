namespace Flow4Tech.Pagamentos.Domain;

/// <summary>One method line of a receipt, as asked: cash or not, the money handed over for the bill, and the tip on top of it.</summary>
public sealed record LinhaDeRecebimento(bool Dinheiro, long RecebidoCentavos, long GorjetaCentavos);

/// <summary>A line as split by the rules: <c>Recebido = Aplicado + Troco</c>; the tip is separate.</summary>
public sealed record LinhaRateada(long RecebidoCentavos, long AplicadoCentavos, long TrocoCentavos, long GorjetaCentavos);

public enum RecusaDeRecebimento
{
    Nenhuma,

    /// <summary>No lines, too many, a value outside its range, or no balance to pay.</summary>
    Invalido,

    /// <summary>The non-cash lines alone pass the balance (only cash can give change), or a line would apply nothing.</summary>
    ExcedeSaldo,
}

public sealed record Rateio(RecusaDeRecebimento Recusa, IReadOnlyList<LinhaRateada> Linhas);

/// <summary>Pure rule of a partial, split or mixed receipt: how the lines share the balance. No I/O.</summary>
public static class RegrasDeRecebimentoMisto
{
    public const int MaximoDeLinhas = 10;

    /// <summary>
    /// Non-cash lines apply exactly what they carry, so together they cannot pass the balance; cash lines then take what is left in order and anything handed over beyond it is change. Every line applies at
    /// least one cent, the applied total never passes the balance (it may stay below it: a partial payment), the tip never enters the applied cents.
    /// </summary>
    public static Rateio Ratear(long saldoCentavos, IReadOnlyList<LinhaDeRecebimento> linhas)
    {
        ArgumentNullException.ThrowIfNull(linhas);
        if (saldoCentavos < 1
            || linhas.Count is < 1 or > MaximoDeLinhas
            || linhas.Any(l => !RegrasDePagamento.RecebidoValido(l.RecebidoCentavos) || l.GorjetaCentavos is < 0 or > RegrasDePagamento.ValorMaximoCentavos))
        {
            return new Rateio(RecusaDeRecebimento.Invalido, []);
        }

        var restante = saldoCentavos - linhas.Where(l => !l.Dinheiro).Sum(l => l.RecebidoCentavos);
        if (restante < 0)
        {
            return new Rateio(RecusaDeRecebimento.ExcedeSaldo, []);
        }

        var rateadas = new List<LinhaRateada>(linhas.Count);
        foreach (var linha in linhas)
        {
            var aplicado = linha.Dinheiro ? RegrasDePagamento.Aplicado(linha.RecebidoCentavos, restante) : linha.RecebidoCentavos;
            if (aplicado < 1)
            {
                return new Rateio(RecusaDeRecebimento.ExcedeSaldo, []);
            }

            restante -= linha.Dinheiro ? aplicado : 0;
            rateadas.Add(new LinhaRateada(linha.RecebidoCentavos, aplicado, RegrasDePagamento.Troco(linha.RecebidoCentavos, aplicado), linha.GorjetaCentavos));
        }

        return new Rateio(RecusaDeRecebimento.Nenhuma, rateadas);
    }
}
