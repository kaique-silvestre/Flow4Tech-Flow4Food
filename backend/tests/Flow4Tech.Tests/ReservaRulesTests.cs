using Flow4Tech.Comandas.Domain;
using FsCheck.Xunit;

namespace Flow4Tech.Tests;

/// <summary>F4-0153: the pure rules of the reservations (input, state moves, overlap, map status). Examples and properties; no I/O.</summary>
public sealed class ReservaRulesTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);

    private static readonly Guid Mesa1 = Guid.Parse("0197b000-0000-7000-8000-000000000001");
    private static readonly Guid Mesa2 = Guid.Parse("0197b000-0000-7000-8000-000000000002");

    [Theory]
    [InlineData("Ana", "Ana")]
    [InlineData("  Ana   Maria ", "Ana Maria")]
    public void The_name_is_trimmed_and_collapsed(string bruto, string esperado) => Assert.Equal(esperado, RegrasDeReserva.Nome(bruto));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ana\nMaria")]
    public void A_missing_blank_or_control_name_is_refused(string? bruto) => Assert.Null(RegrasDeReserva.Nome(bruto));

    [Fact]
    public void A_name_over_the_limit_is_refused() => Assert.Null(RegrasDeReserva.Nome(new string('x', RegrasDeReserva.NomeMaximo + 1)));

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("   ", true, null)]
    [InlineData("11 91234-5678", true, "11 91234-5678")]
    [InlineData("+55 (11) 91234-5678", true, "+55 (11) 91234-5678")]
    [InlineData("1234567", false, null)]
    [InlineData("1234567890123456", false, null)]
    [InlineData("11 9123x-5678", false, null)]
    [InlineData("11+91234-5678", false, null)]
    public void The_phone_is_optional_digits_with_usual_separators(string? bruto, bool aceito, string? valor)
    {
        Assert.Equal(aceito, RegrasDeReserva.TentarTelefone(bruto, out var guardado));
        if (aceito)
        {
            Assert.Equal(valor, guardado);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    public void The_head_count_is_one_to_the_comanda_limit(int pessoas, bool valido) => Assert.Equal(valido, RegrasDeReserva.PessoasValidas(pessoas));

    [Fact]
    public void A_booking_in_the_past_is_refused_and_now_is_accepted()
    {
        Assert.True(RegrasDeReserva.HorarioValido(Agora, Agora));
        Assert.True(RegrasDeReserva.HorarioValido(Agora.AddMinutes(1), Agora));
        Assert.False(RegrasDeReserva.HorarioValido(Agora.AddTicks(-1), Agora));
    }

    [Theory]
    [InlineData(EstadoDaReserva.Reservada, EstadoDaReserva.Chegou, true)]
    [InlineData(EstadoDaReserva.Reservada, EstadoDaReserva.Cancelada, true)]
    [InlineData(EstadoDaReserva.Reservada, EstadoDaReserva.NaoCompareceu, true)]
    [InlineData(EstadoDaReserva.Reservada, EstadoDaReserva.Reservada, false)]
    [InlineData(EstadoDaReserva.Chegou, EstadoDaReserva.Cancelada, false)]
    [InlineData(EstadoDaReserva.Cancelada, EstadoDaReserva.Chegou, false)]
    [InlineData(EstadoDaReserva.NaoCompareceu, EstadoDaReserva.Chegou, false)]
    public void Only_a_waiting_booking_can_move(EstadoDaReserva de, EstadoDaReserva para, bool pode) => Assert.Equal(pode, RegrasDeReserva.PodeMudar(de, para));

    [Fact]
    public void The_state_word_round_trips()
    {
        foreach (var estado in Enum.GetValues<EstadoDaReserva>())
        {
            Assert.Equal(estado, RegrasDeReserva.EstadoDe(RegrasDeReserva.Palavra(estado)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => RegrasDeReserva.EstadoDe("x"));
    }

    [Fact]
    public void Bookings_closer_than_the_duration_overlap_and_the_edge_does_not()
    {
        var existente = new ReservaNaMesa(Mesa1, Agora, EstadoDaReserva.Reservada);

        Assert.True(RegrasDeReserva.Conflita(Mesa1, Agora, [existente]));
        Assert.True(RegrasDeReserva.Conflita(Mesa1, Agora.AddMinutes(RegrasDeReserva.DuracaoEmMinutos - 1), [existente]));
        Assert.True(RegrasDeReserva.Conflita(Mesa1, Agora.AddMinutes(-(RegrasDeReserva.DuracaoEmMinutos - 1)), [existente]));
        Assert.False(RegrasDeReserva.Conflita(Mesa1, Agora.AddMinutes(RegrasDeReserva.DuracaoEmMinutos), [existente]));
        Assert.False(RegrasDeReserva.Conflita(Mesa1, Agora.AddMinutes(-RegrasDeReserva.DuracaoEmMinutos), [existente]));
        Assert.False(RegrasDeReserva.Conflita(Mesa2, Agora, [existente]));
    }

    [Theory]
    [InlineData(EstadoDaReserva.Chegou)]
    [InlineData(EstadoDaReserva.Cancelada)]
    [InlineData(EstadoDaReserva.NaoCompareceu)]
    public void A_final_booking_holds_nothing(EstadoDaReserva estado) =>
        Assert.False(RegrasDeReserva.Conflita(Mesa1, Agora, [new ReservaNaMesa(Mesa1, Agora, estado)]));

    [Fact]
    public void A_table_shows_as_reserved_from_the_lead_time_until_the_hold_ends_with_the_earliest_booking()
    {
        var cedo = Agora.AddMinutes(30);
        ReservaNaMesa Em(Guid mesa, DateTimeOffset horario, EstadoDaReserva estado = EstadoDaReserva.Reservada) => new(mesa, horario, estado);

        var mapa = RegrasDeReserva.MesasReservadas(
            [
                Em(Mesa1, Agora.AddMinutes(90)),
                Em(Mesa1, cedo),
                Em(Mesa2, Agora.AddMinutes(RegrasDeReserva.AntecedenciaNoMapaEmMinutos + 1)),
                Em(Guid.Parse("0197b000-0000-7000-8000-000000000003"), Agora.AddMinutes(10), EstadoDaReserva.Cancelada),
                Em(Guid.Parse("0197b000-0000-7000-8000-000000000004"), Agora.AddMinutes(-RegrasDeReserva.DuracaoEmMinutos)),
                Em(Guid.Parse("0197b000-0000-7000-8000-000000000005"), Agora.AddMinutes(-30)),
            ],
            Agora);

        Assert.Equal(cedo, mapa[Mesa1]);
        Assert.False(mapa.ContainsKey(Mesa2));
        Assert.Equal(2, mapa.Count);
        Assert.True(mapa.ContainsKey(Guid.Parse("0197b000-0000-7000-8000-000000000005")));
    }

    [Fact]
    public void A_busy_table_never_shows_as_reserved_on_the_map()
    {
        var mesa = new Mesa(Mesa1, Guid.NewGuid(), "1", null, true);
        var ocupacao = new Dictionary<Guid, OcupacaoDaMesa> { [Mesa1] = new(1, 0, Agora) };

        var mapa = RegrasDeMesa.MontarMapa([], [mesa], ocupacao, new Dictionary<Guid, DateTimeOffset> { [Mesa1] = Agora });

        Assert.Equal(StatusDaMesa.Ocupada, Assert.Single(mapa.SemArea).Status);
    }

    [Fact]
    public void A_free_table_with_a_due_booking_shows_as_reserved_with_the_time()
    {
        var mesa = new Mesa(Mesa1, Guid.NewGuid(), "1", null, true);
        var outra = new Mesa(Mesa2, mesa.UnidadeId, "2", null, true);

        var mapa = RegrasDeMesa.MontarMapa([], [mesa, outra], new Dictionary<Guid, OcupacaoDaMesa>(), new Dictionary<Guid, DateTimeOffset> { [Mesa1] = Agora });

        Assert.Equal((StatusDaMesa.Reservada, (DateTimeOffset?)Agora), (mapa.SemArea[0].Status, mapa.SemArea[0].ReservadaPara));
        Assert.Equal((StatusDaMesa.Livre, (DateTimeOffset?)null), (mapa.SemArea[1].Status, mapa.SemArea[1].ReservadaPara));
    }

    private static DateTimeOffset Instante(int minutos) => Agora.AddMinutes(minutos % 20_000);

    [Property]
    public bool Overlap_is_symmetric_and_reflexive(int a, int b)
    {
        var x = Instante(a);
        var y = Instante(b);
        return RegrasDeReserva.Sobrepoe(x, y) == RegrasDeReserva.Sobrepoe(y, x) && RegrasDeReserva.Sobrepoe(x, x);
    }

    [Property]
    public bool A_table_never_ends_up_with_two_overlapping_waiting_bookings_whatever_the_request_order(int[] pedidos, byte[] mesas)
    {
        ArgumentNullException.ThrowIfNull(pedidos);
        ArgumentNullException.ThrowIfNull(mesas);
        var ids = new[] { Mesa1, Mesa2 };
        var aceitas = new List<ReservaNaMesa>();
        for (var i = 0; i < pedidos.Length; i++)
        {
            var mesa = ids[(mesas.Length == 0 ? 0 : mesas[i % mesas.Length]) % 2];
            var horario = Instante(pedidos[i]);
            if (!RegrasDeReserva.Conflita(mesa, horario, aceitas))
            {
                aceitas.Add(new ReservaNaMesa(mesa, horario, EstadoDaReserva.Reservada));
            }
        }

        return aceitas.All(a => aceitas.Where(b => !ReferenceEquals(a, b) && b.MesaId == a.MesaId).All(b => !RegrasDeReserva.Sobrepoe(a.Horario, b.Horario)));
    }

    [Property]
    public bool A_request_is_refused_exactly_when_a_waiting_booking_of_the_same_table_is_closer_than_the_duration(int[] horarios, int pedido)
    {
        ArgumentNullException.ThrowIfNull(horarios);
        var existentes = horarios.Select(h => new ReservaNaMesa(Mesa1, Instante(h), EstadoDaReserva.Reservada)).ToList();
        var esperado = existentes.Any(e => (e.Horario - Instante(pedido)).Duration() < TimeSpan.FromMinutes(RegrasDeReserva.DuracaoEmMinutos));
        return RegrasDeReserva.Conflita(Mesa1, Instante(pedido), existentes) == esperado
            && !RegrasDeReserva.Conflita(Mesa2, Instante(pedido), existentes);
    }

    [Property]
    public bool Cancelling_a_booking_frees_its_slot(int horario)
    {
        var instante = Instante(horario);
        var espera = new ReservaNaMesa(Mesa1, instante, EstadoDaReserva.Reservada);
        var cancelada = espera with { Estado = EstadoDaReserva.Cancelada };
        return RegrasDeReserva.Conflita(Mesa1, instante, [espera]) && !RegrasDeReserva.Conflita(Mesa1, instante, [cancelada]);
    }

    [Property]
    public bool The_phone_is_accepted_only_with_8_to_15_digits(string? bruto)
    {
        var aceito = RegrasDeReserva.TentarTelefone(bruto, out var valor);
        if (!aceito)
        {
            return valor is null || valor.Count(char.IsAsciiDigit) is < 8 or > 15 || valor.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-' or '(' or ')' or '+'));
        }

        return valor is null ? string.IsNullOrWhiteSpace(bruto) : valor.Count(char.IsAsciiDigit) is >= 8 and <= 15;
    }
}
