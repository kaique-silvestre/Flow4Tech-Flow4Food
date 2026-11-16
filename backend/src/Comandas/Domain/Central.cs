namespace Flow4Tech.Comandas.Domain;

/// <summary>What the central de comandas filters open comandas by, as the caller asked: waiter, table, minimum minutes open, and the display number.</summary>
public sealed record FiltroDaCentral(Guid? GarcomId = null, Guid? MesaId = null, int? TempoMinimoMinutos = null, int? Numero = null);

/// <summary>The filter after the server decided it: the waiter already folded in with the caller's visibility, the open time turned into an opening instant ("opened at or before").</summary>
public sealed record ConsultaDaCentral(Guid? GarcomId = null, Guid? MesaId = null, DateTimeOffset? AbertaAte = null, int? Numero = null);

/// <summary>Pure rules of the central de comandas query: what is a valid filter and how it maps to the stored columns.</summary>
public static class RegrasDaCentral
{
    /// <summary>One year; keeps the open-time arithmetic far from overflow.</summary>
    public const int TempoMaximoEmMinutos = 525_600;

    public static bool Valido(FiltroDaCentral filtro)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        return (filtro.TempoMinimoMinutos is null or (>= 0 and <= TempoMaximoEmMinutos)) && (filtro.Numero is null or >= 1);
    }

    /// <summary>
    /// The stored-column query for a valid filter. <paramref name="soDoGarcom"/> is the waiter the caller is limited to (null when they see everyone's): it is applied by the server, and a filter
    /// naming ANOTHER waiter then can match nothing, so the result is null (no query needed).
    /// </summary>
    public static ConsultaDaCentral? Mapear(FiltroDaCentral filtro, Guid? soDoGarcom, DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        if (soDoGarcom is { } own && filtro.GarcomId is { } pedido && pedido != own)
        {
            return null;
        }

        return new ConsultaDaCentral(soDoGarcom ?? filtro.GarcomId, filtro.MesaId, filtro.TempoMinimoMinutos is { } minutos ? agora.AddMinutes(-minutos) : null, filtro.Numero);
    }
}
