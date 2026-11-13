namespace Flow4Tech.Pagamentos.Domain;

/// <summary>
/// One payment line received for a comanda (one method of one payer; a mixed payment is several lines). Written once (the database refuses UPDATE and DELETE): a correction is a refund, outside this slice.
/// Amounts are integer cents (BRL). <see cref="RecebidoCentavos"/> = <see cref="AplicadoCentavos"/> + <see cref="TrocoCentavos"/>; only the applied part reduces the comanda's balance and only cash has change.
/// <see cref="GorjetaCentavos"/> is the tip: outside the balance, never part of the applied cents. <see cref="Metodo"/> is the stable key of the method (<c>personalizado</c> for one the organization created),
/// <see cref="MetodoNome"/> the name it had at the time. <see cref="DivisaoId"/> and <see cref="DivisaoParte"/> link the payment to a part of a recorded split.
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
    int ComandaVersao,
    Guid? MetodoId,
    string MetodoNome,
    long GorjetaCentavos,
    EstadoDoPagamento Estado,
    Guid? DivisaoId,
    int? DivisaoParte);

/// <summary>A payment is confirmed once the money is considered received. <see cref="Pendente"/> exists in the model for an asynchronous provider (Pix, card machine); nothing sets it yet and a pending payment does not count in the balance.</summary>
public enum EstadoDoPagamento
{
    Confirmado,
    Pendente,
}

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
