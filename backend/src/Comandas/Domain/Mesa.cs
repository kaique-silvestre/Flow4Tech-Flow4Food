namespace Flow4Tech.Comandas.Domain;

/// <summary>A physical table of a unit. Status (free, occupied) is NOT part of it: it is computed from the open comandas.</summary>
public sealed record Mesa(Guid Id, Guid UnidadeId, string Identificacao, int? Capacidade, bool Ativa);

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
}
