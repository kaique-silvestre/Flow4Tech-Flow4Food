using System.Globalization;

namespace Flow4Tech.Organizacao.Domain;

/// <summary>
/// Opening and closing of one operating day, as local wall-clock times. Closing before opening means it closes after midnight (still the same operating day); equal times mean the whole
/// operating day (no limit). A day without a window is closed. Pure BCL, no I/O.
/// </summary>
public readonly record struct JanelaDeFuncionamento(TimeOnly Abre, TimeOnly Fecha)
{
    public bool DiaInteiro => Abre == Fecha;

    /// <summary>The local instant the window closes, for the operating day <paramref name="dia"/>.</summary>
    public DateTime FimEm(DateOnly dia) => dia.ToDateTime(Fecha) + (Fecha <= Abre ? TimeSpan.FromDays(1) : TimeSpan.Zero);

    /// <summary>
    /// Can the window work under this rollover? A window that does not cover the whole day must open at or after the rollover (earlier instants belong to the previous operating day) and,
    /// when it closes after midnight, close by the rollover (later instants belong to the next one).
    /// </summary>
    public bool FuncionaCom(TimeOnly virada) => DiaInteiro || (Abre >= virada && (Fecha > Abre || Fecha <= virada));

    /// <summary>Is the LOCAL instant inside the window of operating day <paramref name="dia"/>?</summary>
    public bool Contem(DateOnly dia, DateTime local) => DiaInteiro || (local >= dia.ToDateTime(Abre) && local < FimEm(dia));
}

/// <summary>
/// The text forms the agenda is stored in (a layered setting is one short text, at most 200 characters, so the encoding is compact) and the rules that read them. Week: seven windows
/// separated by <c>;</c>, Monday first, each <c>HHmm-HHmm</c> or <c>-</c> for closed. Exceptions: <c>yyMMdd=HHmm-HHmm</c> or <c>yyMMdd=-</c> (closed), separated by <c>;</c>, or <c>-</c> for none.
/// Extension of today: <c>yyMMdd=HHmm</c> (the operating day and the new closing time), or <c>-</c>. Dates are OPERATING days.
/// </summary>
public static class AgendaDeFuncionamento
{
    public const string Nenhuma = "-";

    public const int MaximoDeExcecoes = 10;

    public static readonly string SemanaPadrao = string.Join(';', Enumerable.Repeat("0000-0000", 7));

    public static bool TentarSemana(string? texto, out JanelaDeFuncionamento?[] semana)
    {
        semana = [];
        var partes = texto?.Split(';');
        if (partes is not { Length: 7 })
        {
            return false;
        }

        var lida = new JanelaDeFuncionamento?[7];
        for (var i = 0; i < 7; i++)
        {
            if (!TentarJanela(partes[i], out lida[i]))
            {
                return false;
            }
        }

        semana = lida;
        return true;
    }

    public static string FormatarSemana(IReadOnlyList<JanelaDeFuncionamento?> semana)
    {
        ArgumentNullException.ThrowIfNull(semana);
        return string.Join(';', semana.Select(Formatar));
    }

    public static bool TentarExcecoes(string? texto, out SortedDictionary<DateOnly, JanelaDeFuncionamento?> excecoes)
    {
        excecoes = [];
        if (texto is null)
        {
            return false;
        }

        if (texto == Nenhuma)
        {
            return true;
        }

        foreach (var parte in texto.Split(';'))
        {
            var par = parte.Split('=');
            if (par.Length != 2 || !TentarData(par[0], out var dia) || !TentarJanela(par[1], out var janela) || !excecoes.TryAdd(dia, janela))
            {
                excecoes = [];
                return false;
            }
        }

        return excecoes.Count <= MaximoDeExcecoes;
    }

    public static string FormatarExcecoes(IReadOnlyDictionary<DateOnly, JanelaDeFuncionamento?> excecoes)
    {
        ArgumentNullException.ThrowIfNull(excecoes);
        return excecoes.Count == 0 ? Nenhuma : string.Join(';', excecoes.OrderBy(e => e.Key).Select(e => $"{Data(e.Key)}={Formatar(e.Value)}"));
    }

    public static bool TentarExtensao(string? texto, out DateOnly dia, out TimeOnly fecha)
    {
        dia = default;
        fecha = default;
        var par = texto?.Split('=');
        return par is { Length: 2 } && TentarData(par[0], out dia) && TentarHora(par[1], out fecha);
    }

    public static string FormatarExtensao(DateOnly dia, TimeOnly fecha) => $"{Data(dia)}={Hora(fecha)}";

    /// <summary>The window in force for operating day <paramref name="dia"/>: the extension of that day (it only moves the closing), else the exception of that date, else the weekly default.</summary>
    public static JanelaDeFuncionamento? Efetiva(DateOnly dia, IReadOnlyList<JanelaDeFuncionamento?> semana, IReadOnlyDictionary<DateOnly, JanelaDeFuncionamento?> excecoes, (DateOnly Dia, TimeOnly Fecha)? extensao)
    {
        ArgumentNullException.ThrowIfNull(semana);
        ArgumentNullException.ThrowIfNull(excecoes);
        var janela = excecoes.TryGetValue(dia, out var daExcecao) ? daExcecao : semana[Indice(dia)];
        return janela is { } j && extensao is { } e && e.Dia == dia && !j.DiaInteiro ? j with { Fecha = e.Fecha } : janela;
    }

    /// <summary>The operating day of a local instant: before the rollover it is still the previous calendar date.</summary>
    public static DateOnly DiaOperacional(DateTime local, TimeOnly virada) => DateOnly.FromDateTime(local - virada.ToTimeSpan());

    public static int Indice(DateOnly dia) => ((int)dia.DayOfWeek + 6) % 7;

    public static bool TentarHora(string? texto, out TimeOnly hora)
    {
        hora = default;
        if (texto is not { Length: 4 } || !int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var valor) || valor / 100 > 23 || valor % 100 > 59)
        {
            return false;
        }

        hora = new TimeOnly(valor / 100, valor % 100);
        return true;
    }

    private static bool TentarJanela(string texto, out JanelaDeFuncionamento? janela)
    {
        janela = null;
        if (texto == Nenhuma)
        {
            return true;
        }

        var par = texto.Split('-');
        if (par.Length != 2 || !TentarHora(par[0], out var abre) || !TentarHora(par[1], out var fecha))
        {
            return false;
        }

        janela = new JanelaDeFuncionamento(abre, fecha);
        return true;
    }

    /// <summary>The year is always 20yy (the two digits are stored, never read through a calendar pivot).</summary>
    private static bool TentarData(string texto, out DateOnly dia)
    {
        dia = default;
        if (texto.Length != 6 || !int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var valor))
        {
            return false;
        }

        var (ano, mes, dd) = (2000 + valor / 10000, valor / 100 % 100, valor % 100);
        if (mes is < 1 or > 12 || dd < 1 || dd > DateTime.DaysInMonth(ano, mes))
        {
            return false;
        }

        dia = new DateOnly(ano, mes, dd);
        return true;
    }

    public static bool DataArmazenavel(DateOnly dia) => dia.Year is >= 2000 and <= 2099;

    private static string Data(DateOnly dia) => string.Create(CultureInfo.InvariantCulture, $"{dia.Year - 2000:00}{dia.Month:00}{dia.Day:00}");

    private static string Hora(TimeOnly hora) => hora.ToString("HHmm", CultureInfo.InvariantCulture);

    private static string Formatar(JanelaDeFuncionamento? janela) => janela is { } j ? $"{Hora(j.Abre)}-{Hora(j.Fecha)}" : Nenhuma;
}
