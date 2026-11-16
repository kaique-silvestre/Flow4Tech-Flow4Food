namespace Flow4Tech.Pagamentos.Domain;

/// <summary>
/// A refund of (part of) a payment: a NEGATIVE entry linked to the payment, written once (the database refuses UPDATE and DELETE); the payment itself is never edited. <see cref="ValorCentavos"/> is negative:
/// the cents handed back. <see cref="TurnoId"/> is the shift the refund belongs to (null only when the unit does not require an open shift).
/// </summary>
public sealed record Estorno(
    Guid Id,
    Guid UnidadeId,
    Guid PagamentoId,
    Guid ComandaId,
    Guid? TurnoId,
    Guid? MetodoId,
    string Metodo,
    long ValorCentavos,
    string Motivo,
    Guid OperadorId,
    DateTimeOffset RegistradoEm);

/// <summary>Pure rules of a refund (no I/O, no clock).</summary>
public static class RegrasDeEstorno
{
    public const int MotivoMaximo = 200;

    /// <summary>What is still refundable of a payment: what it applied to the bill minus what was already refunded (<paramref name="estornadoCentavos"/> is the positive total refunded). Never negative.</summary>
    public static long SaldoEstornavel(long aplicadoCentavos, long estornadoCentavos) => Math.Max(0, aplicadoCentavos - estornadoCentavos);

    /// <summary>The cents of a refund: the amount asked, or all that is left when none is asked (a total refund). Null when it is not acceptable: below one cent or beyond the refundable balance.</summary>
    public static long? Valor(long? pedidoCentavos, long saldoEstornavelCentavos)
    {
        var valor = pedidoCentavos ?? saldoEstornavelCentavos;
        return valor >= 1 && valor <= saldoEstornavelCentavos ? valor : null;
    }

    /// <summary>The reason is required: trimmed, not empty, at most <see cref="MotivoMaximo"/> characters, no control characters.</summary>
    public static bool TentarMotivo(string? motivo, out string texto)
    {
        texto = motivo?.Trim() ?? "";
        return texto.Length is >= 1 and <= MotivoMaximo && !texto.Any(char.IsControl);
    }
}
