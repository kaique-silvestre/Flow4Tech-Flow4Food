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
    long? DiferencaCentavos,
    long RecebidoDinheiroCentavos = 0,
    long? SuprimentosCentavos = null,
    long? SangriasCentavos = null,
    SituacaoDaConferencia? Situacao = null,
    bool EsperadoOculto = false);

/// <summary>Outcome of the count of a closed shift: <c>Concluida</c> (inside the tolerance, or closed before approvals existed), <c>Pendente</c> (waiting for an approver) or <c>Aprovada</c>.</summary>
public enum SituacaoDaConferencia
{
    Concluida,

    Pendente,

    Aprovada,
}

public enum TipoDeMovimento
{
    Suprimento,

    Sangria,
}

/// <summary>Money put in (supply) or taken out (withdrawal) of the drawer during a shift. Append-only.</summary>
public sealed record MovimentoDoCaixa(Guid Id, Guid TurnoId, Guid UnidadeId, TipoDeMovimento Tipo, long ValorCentavos, string Motivo, string? Destino, Guid OperadorId, DateTimeOffset CriadoEm);

/// <summary>The count of one non-cash payment method of a shift against what the system recorded for it.</summary>
public sealed record ContagemDoMetodo(Guid MetodoId, string MetodoNome, long ContadoCentavos, long EsperadoCentavos)
{
    public long DiferencaCentavos => ContadoCentavos - EsperadoCentavos;
}

/// <summary>Pure rules of the shift. The float is change money, not a sale; it only enters the expected cash at closing.</summary>
public static class RegrasDoCaixa
{
    /// <summary>Upper limit of a float or a count: R$ 1.000.000,00 (a typo guard, not a business rule).</summary>
    public const long ValorMaximoCentavos = 100_000_000;

    /// <summary>A float or a cash count: zero is allowed, negative is not.</summary>
    public static bool ValorValido(long centavos) => centavos is >= 0 and <= ValorMaximoCentavos;

    /// <summary>Upper bound of a reason (supply, withdrawal, approval).</summary>
    public const int MotivoMaximo = 200;

    /// <summary>Upper bound of the destination of a withdrawal.</summary>
    public const int DestinoMaximo = 80;

    /// <summary>Cash that should be in the drawer: the float plus the cash payments applied during the shift plus supplies minus withdrawals.</summary>
    public static long Esperado(long fundoCentavos, long recebimentosEmDinheiroCentavos, long suprimentosCentavos = 0, long sangriasCentavos = 0) =>
        fundoCentavos + recebimentosEmDinheiroCentavos + suprimentosCentavos - sangriasCentavos;

    /// <summary>A supply or a withdrawal: at least one cent, up to the typo guard.</summary>
    public static bool MovimentoValido(long centavos) => centavos is >= 1 and <= ValorMaximoCentavos;

    /// <summary>A free text that is required: trimmed, not empty, at most <paramref name="maximo"/> characters.</summary>
    public static bool TextoValido(string? texto, int maximo, out string? limpo)
    {
        limpo = texto?.Trim();
        if (string.IsNullOrEmpty(limpo) || limpo.Length > maximo)
        {
            limpo = null;
            return false;
        }

        return true;
    }

    /// <summary>A withdrawal never takes more than the drawer holds (the expected cash would go negative).</summary>
    public static bool PodeSangrar(long saldoDaGavetaCentavos, long valorCentavos) => valorCentavos >= 1 && valorCentavos <= saldoDaGavetaCentavos;

    /// <summary>The difference of a shift for the tolerance and the approval limit: the sum of the absolute differences of the cash and of each counted method.</summary>
    public static long DiferencaTotal(long diferencaDinheiroCentavos, IEnumerable<long> diferencasPorMetodoCentavos)
    {
        ArgumentNullException.ThrowIfNull(diferencasPorMetodoCentavos);
        return Math.Abs(diferencaDinheiroCentavos) + diferencasPorMetodoCentavos.Sum(Math.Abs);
    }

    /// <summary>Inside the tolerance (limit included) the count concludes alone; above it the shift waits for an approver.</summary>
    public static SituacaoDaConferencia Situacao(long diferencaTotalCentavos, long toleranciaCentavos) =>
        diferencaTotalCentavos <= toleranciaCentavos ? SituacaoDaConferencia.Concluida : SituacaoDaConferencia.Pendente;

    /// <summary>Separation of duties: the person who closed the shift approves its difference only when self-approval is on.</summary>
    public static bool PodeAprovar(bool aprovadorFechouOTurno, bool autoaprovacaoPermitida) => !aprovadorFechouOTurno || autoaprovacaoPermitida;

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
