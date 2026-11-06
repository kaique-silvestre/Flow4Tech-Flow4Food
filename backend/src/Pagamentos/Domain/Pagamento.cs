namespace Flow4Tech.Pagamentos.Domain;

/// <summary>
/// One payment received for a comanda. Written once (the database refuses UPDATE and DELETE): a correction is a refund, outside this slice. Amounts are integer cents (BRL).
/// <see cref="RecebidoCentavos"/> = <see cref="AplicadoCentavos"/> + <see cref="TrocoCentavos"/>; only the applied part reduces the comanda's balance.
/// </summary>
public sealed record Pagamento(
    Guid Id,
    Guid UnidadeId,
    Guid ComandaId,
    Guid? TurnoId,
    string Metodo,
    long RecebidoCentavos,
    long AplicadoCentavos,
    long TrocoCentavos,
    Guid OperadorId,
    DateTimeOffset RegistradoEm,
    long SaldoAposCentavos,
    int ComandaVersao);

/// <summary>Pure rules of a cash payment (no I/O, no clock).</summary>
public static class RegrasDePagamento
{
    public const string Dinheiro = "dinheiro";

    /// <summary>Upper limit of one payment: R$ 1.000.000,00 (a typo guard, not a business rule).</summary>
    public const long ValorMaximoCentavos = 100_000_000;

    /// <summary>The cash handed over: at least one cent, up to the typo guard.</summary>
    public static bool RecebidoValido(long recebidoCentavos) => recebidoCentavos is >= 1 and <= ValorMaximoCentavos;

    /// <summary>What the payment takes off the balance: all of the cash, or the whole balance when more was handed over. Never negative.</summary>
    public static long Aplicado(long recebidoCentavos, long saldoCentavos) => Math.Max(0, Math.Min(recebidoCentavos, saldoCentavos));

    /// <summary>Change to give back: received minus applied, never negative.</summary>
    public static long Troco(long recebidoCentavos, long aplicadoCentavos) => Math.Max(0, recebidoCentavos - aplicadoCentavos);
}
