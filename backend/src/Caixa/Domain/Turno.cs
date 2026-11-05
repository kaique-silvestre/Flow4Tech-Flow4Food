using System.Globalization;

namespace Flow4Tech.Caixa.Domain;

/// <summary>
/// A cash shift: one operator, one unit, opened with a float (change fund) and closed with a count. The three closing figures are all null while the shift is open and all set once closed.
/// Amounts are integer cents (BRL).
/// </summary>
public sealed record Turno(
    Guid Id,
    Guid UnidadeId,
    Guid OperadorId,
    DateTimeOffset AbertoEm,
    long FundoCentavos,
    DateTimeOffset? FechadoEm,
    long? ContadoCentavos,
    long? EsperadoCentavos,
    long? DiferencaCentavos);

/// <summary>Pure rules of the shift. The float is change money, not a sale; it only enters the expected cash at closing.</summary>
public static class RegrasDoCaixa
{
    /// <summary>Upper limit of a float or a count: R$ 1.000.000,00 (a typo guard, not a business rule).</summary>
    public const long ValorMaximoCentavos = 100_000_000;

    /// <summary>A float or a cash count: zero is allowed, negative is not.</summary>
    public static bool ValorValido(long centavos) => centavos is >= 0 and <= ValorMaximoCentavos;

    /// <summary>Cash that should be in the drawer: the float plus the cash payments applied during the shift (no supply, bleed or refund in this slice).</summary>
    public static long Esperado(long fundoCentavos, long recebimentosEmDinheiroCentavos) => fundoCentavos + recebimentosEmDinheiroCentavos;

    /// <summary>Counted minus expected: negative is a shortage, positive a surplus.</summary>
    public static long Diferenca(long contadoCentavos, long esperadoCentavos) => contadoCentavos - esperadoCentavos;

    /// <summary>The difference in words: <c>Confere</c>, <c>Falta R$ x</c> or <c>Sobra R$ x</c>.</summary>
    public static string Palavra(long diferencaCentavos) => diferencaCentavos switch
    {
        0 => "Confere",
        < 0 => "Falta " + Reais(-diferencaCentavos),
        _ => "Sobra " + Reais(diferencaCentavos),
    };

    private static string Reais(long centavos) =>
        "R$ " + (centavos / 100).ToString("N0", CultureInfo.GetCultureInfo("pt-BR")) + "," + (centavos % 100).ToString("00", CultureInfo.InvariantCulture);
}
