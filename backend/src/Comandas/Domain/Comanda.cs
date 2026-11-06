namespace Flow4Tech.Comandas.Domain;

public enum EstadoDaComanda
{
    Aberta,
    Fechada,
    Cancelada,
}

/// <summary>
/// An open bill of one unit. <see cref="Numero"/> is the display number (#12): unique per unit and operating day, restarting each day. <see cref="Versao"/> starts at 1 and every write bumps it.
/// <see cref="TaxaServicoPercentual"/> is frozen when the comanda opens.
/// </summary>
public sealed record Comanda(
    Guid Id,
    Guid UnidadeId,
    DateOnly DiaOperacional,
    int Numero,
    string Identificacao,
    Guid? MesaId,
    string Canal,
    Guid GarcomId,
    int? Pessoas,
    EstadoDaComanda Estado,
    DateTimeOffset AbertaEm,
    DateTimeOffset? FechadaEm,
    decimal TaxaServicoPercentual,
    bool TaxaServicoRemovida,
    int Versao);

/// <summary>Pure rules of the comanda (no I/O, no clock). Return null / false when the input is not acceptable; the caller never stores what these refuse.</summary>
public static class RegrasDeComanda
{
    public const string CanalPadrao = "salao";

    public const int PessoasMaximo = 999;

    public const decimal TaxaMaxima = 20m;

    /// <summary>Only an open comanda moves, and only to closed or cancelled (reopening is outside this slice).</summary>
    public static bool PodeTransitar(EstadoDaComanda de, EstadoDaComanda para) =>
        de == EstadoDaComanda.Aberta && para is EstadoDaComanda.Fechada or EstadoDaComanda.Cancelada;

    /// <summary>The stored word of a state.</summary>
    public static string Palavra(EstadoDaComanda estado) => estado switch
    {
        EstadoDaComanda.Aberta => "aberta",
        EstadoDaComanda.Fechada => "fechada",
        EstadoDaComanda.Cancelada => "cancelada",
        _ => throw new ArgumentOutOfRangeException(nameof(estado)),
    };

    public static EstadoDaComanda Estado(string palavra) => palavra switch
    {
        "aberta" => EstadoDaComanda.Aberta,
        "fechada" => EstadoDaComanda.Fechada,
        "cancelada" => EstadoDaComanda.Cancelada,
        _ => throw new ArgumentOutOfRangeException(nameof(palavra)),
    };

    /// <summary>The number that follows the last one handed out on an operating day (1 when none was).</summary>
    public static int ProximoNumero(int? ultimo) => (ultimo is > 0 ? ultimo.Value : 0) + 1;

    public static string NumeroDeExibicao(int numero) => $"#{numero}";

    /// <summary>The name a comanda gets when opened with a number only.</summary>
    public static string IdentificacaoPadrao(int numero) => $"Comanda {numero}";

    /// <summary>A free name follows the same rules as a table identification (trimmed, 1 to 40, no control characters); null when refused.</summary>
    public static string? Identificacao(string? value) => RegrasDeMesa.Identificacao(value);

    /// <summary>Optional head count; when given, 1 to <see cref="PessoasMaximo"/>.</summary>
    public static bool PessoasValidas(int? pessoas) => pessoas is null or (>= 1 and <= PessoasMaximo);

    public static bool TaxaValida(decimal percentual) => percentual is >= 0m and <= TaxaMaxima;

    /// <summary>Optimistic concurrency: a write is accepted only against the version the caller last saw.</summary>
    public static bool VersaoConfere(int enviada, int atual) => enviada == atual;
}
