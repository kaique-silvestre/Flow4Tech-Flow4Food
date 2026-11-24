namespace Flow4Tech.Comandas.Domain;

public enum EstadoDaReserva
{
    /// <summary>Booked and waiting. The only state that holds a table and the only one that can still move.</summary>
    Reservada,

    /// <summary>The guests arrived: the reservation became a comanda (<see cref="Reserva.ComandaId"/>).</summary>
    Chegou,

    Cancelada,

    NaoCompareceu,
}

/// <summary>
/// A booking of a unit. <see cref="Nome"/> and <see cref="Telefone"/> are personal data: they are shown to who may see reservations and are NEVER written to the audit trail, the outbox or the technical log.
/// <see cref="DiaOperacional"/> is the unit's operating day of <see cref="Horario"/> when it was booked.
/// </summary>
public sealed record Reserva(
    Guid Id,
    Guid UnidadeId,
    Guid? MesaId,
    string Nome,
    string? Telefone,
    int Pessoas,
    DateTimeOffset Horario,
    DateOnly DiaOperacional,
    string? Observacao,
    EstadoDaReserva Estado,
    Guid? ComandaId,
    DateTimeOffset CriadaEm);

/// <summary>What the overlap rule needs of a booking already stored.</summary>
public sealed record ReservaNaMesa(Guid MesaId, DateTimeOffset Horario, EstadoDaReserva Estado);

/// <summary>Pure rules of the reservations: input, state moves, the overlap of two bookings of one table and when the table map shows a table as reserved. No I/O.</summary>
public static class RegrasDeReserva
{
    public const int NomeMaximo = 80;

    public const int ObservacaoMaxima = 200;

    public const int DigitosMinimos = 8;

    public const int DigitosMaximos = 15;

    /// <summary>How long a booking holds its table: two bookings of the same table closer than this overlap.</summary>
    public const int DuracaoEmMinutos = 120;

    /// <summary>The table map shows a table as reserved from this many minutes before the booking (and while it is late and still waiting).</summary>
    public const int AntecedenciaNoMapaEmMinutos = 120;

    /// <summary>Trimmed, inner runs of spaces collapsed to one; null when missing, blank, too long or holding control characters.</summary>
    public static string? Nome(string? value)
    {
        if (value is null || value.Any(char.IsControl))
        {
            return null;
        }

        var text = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length is >= 1 and <= NomeMaximo ? text : null;
    }

    /// <summary>Optional phone: digits with an optional leading plus, spaces, dashes and parentheses; 8 to 15 digits. False when refused; <paramref name="valor"/> is null for an absent or blank one.</summary>
    public static bool TentarTelefone(string? bruto, out string? valor)
    {
        var texto = bruto?.Trim();
        valor = string.IsNullOrEmpty(texto) ? null : texto;
        if (valor is null)
        {
            return true;
        }

        var digitos = valor.Count(char.IsAsciiDigit);
        var permitido = valor.Select((c, i) => char.IsAsciiDigit(c) || c is ' ' or '-' or '(' or ')' || (c == '+' && i == 0)).All(ok => ok);
        return permitido && digitos is >= DigitosMinimos and <= DigitosMaximos;
    }

    public static bool TentarObservacao(string? bruto, out string? valor)
    {
        var aceita = RegrasDeComanda.TentarObservacao(bruto, out valor);
        return aceita && (valor is null || valor.Length <= ObservacaoMaxima);
    }

    public static bool PessoasValidas(int pessoas) => pessoas is >= 1 and <= RegrasDeComanda.PessoasMaximo;

    /// <summary>A booking can only be made for an instant that has not passed (server clock, the same one the opening hours use).</summary>
    public static bool HorarioValido(DateTimeOffset horario, DateTimeOffset agora) => horario >= agora;

    /// <summary>From a waiting booking the guests may arrive, the house may cancel it or mark the no-show; every other state is final.</summary>
    public static bool PodeMudar(EstadoDaReserva de, EstadoDaReserva para) =>
        de == EstadoDaReserva.Reservada && para is EstadoDaReserva.Chegou or EstadoDaReserva.Cancelada or EstadoDaReserva.NaoCompareceu;

    public static bool Sobrepoe(DateTimeOffset a, DateTimeOffset b) => (a - b).Duration() < TimeSpan.FromMinutes(DuracaoEmMinutos);

    /// <summary>True when a new booking of <paramref name="mesaId"/> at <paramref name="horario"/> overlaps a WAITING one of the same table. Final bookings (arrived, cancelled, no-show) hold nothing.</summary>
    public static bool Conflita(Guid mesaId, DateTimeOffset horario, IEnumerable<ReservaNaMesa> existentes)
    {
        ArgumentNullException.ThrowIfNull(existentes);
        return existentes.Any(r => r.Estado == EstadoDaReserva.Reservada && r.MesaId == mesaId && Sobrepoe(r.Horario, horario));
    }

    /// <summary>
    /// Per table, the booking time the map shows for a FREE table: the earliest waiting booking that starts within <see cref="AntecedenciaNoMapaEmMinutos"/> from now, or started less than
    /// <see cref="DuracaoEmMinutos"/> ago (still holding the table). Tables with none are absent.
    /// </summary>
    public static IReadOnlyDictionary<Guid, DateTimeOffset> MesasReservadas(IEnumerable<ReservaNaMesa> existentes, DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(existentes);
        var de = agora.AddMinutes(-DuracaoEmMinutos);
        var ate = agora.AddMinutes(AntecedenciaNoMapaEmMinutos);
        return existentes
            .Where(r => r.Estado == EstadoDaReserva.Reservada && r.Horario > de && r.Horario <= ate)
            .GroupBy(r => r.MesaId)
            .ToDictionary(g => g.Key, g => g.Min(r => r.Horario));
    }

    public static string Palavra(EstadoDaReserva estado) => estado switch
    {
        EstadoDaReserva.Chegou => "chegou",
        EstadoDaReserva.Cancelada => "cancelada",
        EstadoDaReserva.NaoCompareceu => "nao-compareceu",
        _ => "reservada",
    };

    public static EstadoDaReserva EstadoDe(string palavra) => palavra switch
    {
        "reservada" => EstadoDaReserva.Reservada,
        "chegou" => EstadoDaReserva.Chegou,
        "cancelada" => EstadoDaReserva.Cancelada,
        "nao-compareceu" => EstadoDaReserva.NaoCompareceu,
        _ => throw new ArgumentOutOfRangeException(nameof(palavra)),
    };
}
