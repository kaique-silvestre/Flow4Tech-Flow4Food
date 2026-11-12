using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>Dominio do salao, ticket 04: pure rules of the tables (no I/O).</summary>
public sealed class MesasRulesTests
{
    [Theory]
    [InlineData("12", "12")]
    [InlineData("  Varanda 3  ", "Varanda 3")]
    [InlineData("Mesa  1", "Mesa 1")]
    public void An_identification_is_trimmed_and_inner_spaces_collapsed(string input, string expected) =>
        Assert.Equal(expected, RegrasDeMesa.Identificacao(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\tb")]
    public void A_missing_blank_or_control_identification_is_refused(string? input) =>
        Assert.Null(RegrasDeMesa.Identificacao(input));

    [Fact]
    public void An_identification_longer_than_the_limit_is_refused_and_exactly_the_limit_is_kept()
    {
        Assert.Null(RegrasDeMesa.Identificacao(new string('x', RegrasDeMesa.IdentificacaoMaxima + 1)));
        Assert.NotNull(RegrasDeMesa.Identificacao(new string('x', RegrasDeMesa.IdentificacaoMaxima)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(RegrasDeMesa.CapacidadeMaxima, true)]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    [InlineData(RegrasDeMesa.CapacidadeMaxima + 1, false)]
    public void Capacity_is_optional_but_when_given_is_between_one_and_the_limit(int? capacity, bool valid) =>
        Assert.Equal(valid, RegrasDeMesa.CapacidadeValida(capacity));

    [Theory]
    [InlineData(0, 0, StatusDaMesa.Livre)]
    [InlineData(0, 3, StatusDaMesa.Livre)]
    [InlineData(1, 0, StatusDaMesa.Ocupada)]
    [InlineData(4, 0, StatusDaMesa.Ocupada)]
    [InlineData(2, 1, StatusDaMesa.AguardandoConta)]
    [InlineData(1, 1, StatusDaMesa.AguardandoConta)]
    public void Status_is_derived_free_without_open_comandas_waiting_when_any_asked_for_the_bill_else_occupied(int abertas, int aguardando, StatusDaMesa expected) =>
        Assert.Equal(expected, RegrasDeMesa.Status(abertas, aguardando));

    [Fact]
    public void Status_defaults_to_nobody_waiting_for_the_bill() =>
        Assert.Equal(StatusDaMesa.Ocupada, RegrasDeMesa.Status(1));

    [Theory]
    [InlineData("  Varanda  Alta ", "Varanda Alta")]
    [InlineData("Salao", "Salao")]
    public void An_area_name_follows_the_table_identification_rules(string input, string expected) =>
        Assert.Equal(expected, RegrasDeArea.Nome(input));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("a\tb")]
    public void A_missing_blank_or_control_area_name_is_refused(string? input) =>
        Assert.Null(RegrasDeArea.Nome(input));

    private static readonly Guid U = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");

    private static Mesa NovaMesa(string nome, Guid? area = null, bool ativa = true) => new(Guid.CreateVersion7(), U, nome, null, ativa, area);

    private static Area NovaArea(string nome, bool ativa = true) => new(Guid.CreateVersion7(), U, nome, ativa);

    [Fact]
    public void The_map_lists_active_areas_by_name_with_their_tables_by_identification_and_the_rest_apart()
    {
        var varanda = NovaArea("varanda");
        var salao = NovaArea("Salao");
        var velha = NovaArea("Velha", ativa: false);
        var m10 = NovaMesa("10", salao.Id);
        var m2 = NovaMesa("2", salao.Id);
        var v1 = NovaMesa("V1", varanda.Id);
        var solta = NovaMesa("Balcao");
        var daVelha = NovaMesa("Fundos", velha.Id);
        var desconhecida = NovaMesa("Perdida", Guid.CreateVersion7());
        var aposentada = NovaMesa("Aposentada", salao.Id, ativa: false);

        var mapa = RegrasDeMesa.MontarMapa([varanda, salao, velha], [m10, m2, v1, solta, daVelha, desconhecida, aposentada], new Dictionary<Guid, OcupacaoDaMesa>());

        Assert.Equal(["Salao", "varanda"], mapa.Areas.Select(a => a.Area.Nome));
        Assert.Equal(["10", "2"], mapa.Areas[0].Mesas.Select(m => m.Mesa.Identificacao));
        Assert.Equal(["V1"], mapa.Areas[1].Mesas.Select(m => m.Mesa.Identificacao));
        Assert.Equal(["Balcao", "Fundos", "Perdida"], mapa.SemArea.Select(m => m.Mesa.Identificacao));
        Assert.All(mapa.Areas.SelectMany(a => a.Mesas).Concat(mapa.SemArea), m => Assert.Equal((StatusDaMesa.Livre, 0, (DateTimeOffset?)null), (m.Status, m.ComandasAbertas, m.AbertaDesde)));
    }

    [Fact]
    public void The_map_shows_an_empty_active_area_and_takes_status_count_and_first_opening_from_the_occupation()
    {
        var vazia = NovaArea("Anexo");
        var salao = NovaArea("Salao");
        var ocupada = NovaMesa("1", salao.Id);
        var conta = NovaMesa("2", salao.Id);
        var aberta = DateTimeOffset.Parse("2026-11-11T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var mapa = RegrasDeMesa.MontarMapa(
            [vazia, salao],
            [ocupada, conta],
            new Dictionary<Guid, OcupacaoDaMesa> { [ocupada.Id] = new(2, 0, aberta), [conta.Id] = new(1, 1, aberta.AddMinutes(5)) });

        Assert.Empty(mapa.Areas[0].Mesas);
        Assert.Equal((StatusDaMesa.Ocupada, 2, (DateTimeOffset?)aberta), (mapa.Areas[1].Mesas[0].Status, mapa.Areas[1].Mesas[0].ComandasAbertas, mapa.Areas[1].Mesas[0].AbertaDesde));
        Assert.Equal((StatusDaMesa.AguardandoConta, 1), (mapa.Areas[1].Mesas[1].Status, mapa.Areas[1].Mesas[1].ComandasAbertas));
        Assert.Empty(mapa.SemArea);
    }

    [Fact]
    public void An_empty_map_has_no_areas_and_no_loose_tables()
    {
        Assert.Empty(MapaDeMesas.Vazio.Areas);
        Assert.Empty(MapaDeMesas.Vazio.SemArea);
        var mapa = RegrasDeMesa.MontarMapa([], [], new Dictionary<Guid, OcupacaoDaMesa>());
        Assert.Empty(mapa.Areas);
        Assert.Empty(mapa.SemArea);
    }

    [Theory]
    [InlineData("12", "12", "7", "7")]
    [InlineData("12", null, "7", "12")]
    [InlineData("Aniversario", "12", "7", "Aniversario")]
    [InlineData("Comanda 5", "12", "7", "Comanda 5")]
    public void A_comanda_named_after_its_table_follows_the_move_and_any_other_name_stays(string atual, string? mesaAnterior, string mesaNova, string expected) =>
        Assert.Equal(expected, RegrasDeComanda.IdentificacaoAposTrocaDeMesa(atual, mesaAnterior, mesaNova));
}
