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
    int Versao,
    string? Observacao = null,
    string? MotivoCancelamento = null);

/// <summary>The bill of a comanda in cents: consumption of the launched items, service fee, total, what the payments applied and what remains.</summary>
public sealed record ContaDaComanda(long ConsumoCentavos, long TaxaServicoCentavos, long TotalCentavos, long PagoCentavos, long SaldoCentavos, bool TaxaServicoRemovida, decimal TaxaServicoPercentual, long DescontoCentavos = 0);

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

    /// <summary>The general note of a comanda: trimmed, 1 to 200, no control characters; false when refused, <paramref name="valor"/> null for an absent or blank note.</summary>
    public static bool TentarObservacao(string? bruto, out string? valor) => RegrasDeRodada.TentarObservacao(bruto, out valor);

    /// <summary>Optional head count; when given, 1 to <see cref="PessoasMaximo"/>.</summary>
    public static bool PessoasValidas(int? pessoas) => pessoas is null or (>= 1 and <= PessoasMaximo);

    public static bool TaxaValida(decimal percentual) => percentual is >= 0m and <= TaxaMaxima;

    /// <summary>The service fee in cents: <paramref name="percentual"/> of the consumption, rounded half up (0.5 cent goes up) in this one function.</summary>
    public static long TaxaDeServico(long consumoCentavos, decimal percentual) =>
        (long)Math.Round(Math.Max(0, consumoCentavos) * percentual / 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>The bill: fee included unless it was removed; balance = total minus what was applied (never hidden when negative, so the caller can refuse to close).</summary>
    public static ContaDaComanda Conta(long consumoCentavos, decimal percentual, bool taxaRemovida, long pagoCentavos, long descontoCentavos = 0)
    {
        // The order is fixed (ADR-018): consumption, discounts (already clamped to it), service fee on what is left, total. The fee is never charged on a discounted amount.
        var liquido = Math.Max(0, consumoCentavos - Math.Max(0, descontoCentavos));
        var taxa = taxaRemovida ? 0 : TaxaDeServico(liquido, percentual);
        var total = liquido + taxa;
        return new ContaDaComanda(consumoCentavos, taxa, total, pagoCentavos, total - pagoCentavos, taxaRemovida, percentual, consumoCentavos - liquido);
    }

    /// <summary>A comanda closes only at balance zero.</summary>
    public static bool PodeFechar(ContaDaComanda conta) => conta is not null && conta.SaldoCentavos == 0;

    /// <summary>
    /// The name a comanda carries after moving to another table. A comanda named after its table (the name opening it gives) follows the move; a comanda with any other name keeps it.
    /// </summary>
    public static string IdentificacaoAposTrocaDeMesa(string atual, string? identificacaoDaMesaAnterior, string identificacaoDaMesaNova)
    {
        ArgumentNullException.ThrowIfNull(atual);
        ArgumentNullException.ThrowIfNull(identificacaoDaMesaNova);
        return identificacaoDaMesaAnterior is not null && string.Equals(atual, identificacaoDaMesaAnterior, StringComparison.Ordinal) ? identificacaoDaMesaNova : atual;
    }

    /// <summary>Optimistic concurrency: a write is accepted only against the version the caller last saw.</summary>
    public static bool VersaoConfere(int enviada, int atual) => enviada == atual;
}
