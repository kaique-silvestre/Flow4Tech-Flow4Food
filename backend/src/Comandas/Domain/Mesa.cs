namespace Flow4Tech.Comandas.Domain;

/// <summary>A physical table of a unit, optionally placed in an <see cref="Area"/>. Status (free, occupied) is NOT part of it: it is computed from the open comandas.</summary>
public sealed record Mesa(Guid Id, Guid UnidadeId, string Identificacao, int? Capacidade, bool Ativa, Guid? AreaId = null);

/// <summary>A named zone of a unit ("Varanda"). A retired area (<c>Ativa</c> false) is kept for history; its tables then show without an area.</summary>
public sealed record Area(Guid Id, Guid UnidadeId, string Nome, bool Ativa);

public enum StatusDaMesa
{
    Livre,
    Ocupada,

    /// <summary>At least one open comanda of the table asked for the bill (set once the pre-bill lands, F4-0067).</summary>
    AguardandoConta,
}

/// <summary>A table with its derived status.</summary>
public sealed record MesaComStatus(Mesa Mesa, StatusDaMesa Status);

/// <summary>What the open comandas say about one table: how many are open, how many of them asked for the bill, and when the first one opened.</summary>
public sealed record OcupacaoDaMesa(int ComandasAbertas, int AguardandoConta, DateTimeOffset? AbertaDesde);

/// <summary>A table as the map shows it: derived status, number of open comandas and the opening time of the first one (null when free).</summary>
public sealed record MesaNoMapa(Mesa Mesa, StatusDaMesa Status, int ComandasAbertas, DateTimeOffset? AbertaDesde);

public sealed record AreaNoMapa(Area Area, IReadOnlyList<MesaNoMapa> Mesas);

/// <summary>The table map of a unit: active areas with their tables, plus the tables that belong to no active area.</summary>
public sealed record MapaDeMesas(IReadOnlyList<AreaNoMapa> Areas, IReadOnlyList<MesaNoMapa> SemArea)
{
    public static MapaDeMesas Vazio { get; } = new([], []);
}

/// <summary>Pure input rules of the tables. Return null / false when the input is not acceptable; the caller never stores what these refuse.</summary>
public static class RegrasDeMesa
{
    public const int IdentificacaoMaxima = 40;

    public const int CapacidadeMaxima = 999;

    /// <summary>Trimmed, inner runs of spaces collapsed to one; null when missing, blank, too long or holding control characters.</summary>
    public static string? Identificacao(string? value)
    {
        if (value is null || value.Any(char.IsControl))
        {
            return null;
        }

        var text = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length is >= 1 and <= IdentificacaoMaxima ? text : null;
    }

    /// <summary>Optional; when given, 1 to <see cref="CapacidadeMaxima"/>.</summary>
    public static bool CapacidadeValida(int? capacidade) => capacidade is null or (>= 1 and <= CapacidadeMaxima);

    /// <summary>Free with no open comanda; waiting for the bill when any open comanda asked for it; otherwise occupied. Never stored.</summary>
    public static StatusDaMesa Status(int comandasAbertas, int aguardandoConta = 0) =>
        comandasAbertas <= 0 ? StatusDaMesa.Livre : aguardandoConta > 0 ? StatusDaMesa.AguardandoConta : StatusDaMesa.Ocupada;

    /// <summary>
    /// The map: each ACTIVE area (ordered by name) with its tables, then the tables with no area or an area that is not active. Tables are ordered by identification inside a group.
    /// A table missing from <paramref name="ocupacao"/> is free.
    /// </summary>
    public static MapaDeMesas MontarMapa(IReadOnlyCollection<Area> areas, IReadOnlyCollection<Mesa> mesas, IReadOnlyDictionary<Guid, OcupacaoDaMesa> ocupacao)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(mesas);
        ArgumentNullException.ThrowIfNull(ocupacao);
        var ativas = areas.Where(a => a.Ativa).ToDictionary(a => a.Id);
        var noMapa = mesas.Where(m => m.Ativa).Select(m => Posicionar(m, ocupacao)).ToList();
        IReadOnlyList<MesaNoMapa> Ordenadas(IEnumerable<MesaNoMapa> grupo) =>
            [.. grupo.OrderBy(x => x.Mesa.Identificacao, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Mesa.Id)];

        var comArea = ativas.Values
            .OrderBy(a => a.Nome, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id)
            .Select(a => new AreaNoMapa(a, Ordenadas(noMapa.Where(x => x.Mesa.AreaId == a.Id))))
            .ToList();
        return new MapaDeMesas(comArea, Ordenadas(noMapa.Where(x => x.Mesa.AreaId is not { } id || !ativas.ContainsKey(id))));
    }

    private static MesaNoMapa Posicionar(Mesa mesa, IReadOnlyDictionary<Guid, OcupacaoDaMesa> ocupacao) =>
        ocupacao.TryGetValue(mesa.Id, out var o)
            ? new MesaNoMapa(mesa, Status(o.ComandasAbertas, o.AguardandoConta), o.ComandasAbertas, o.AbertaDesde)
            : new MesaNoMapa(mesa, StatusDaMesa.Livre, 0, null);
}

/// <summary>Pure input rules of the areas.</summary>
public static class RegrasDeArea
{
    /// <summary>An area name follows the same rules as a table identification (trimmed, inner spaces collapsed, 1 to 40, no control characters); null when refused.</summary>
    public static string? Nome(string? value) => RegrasDeMesa.Identificacao(value);
}
