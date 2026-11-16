using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0141: pure rules of the central de comandas filter (what is valid, how it maps to stored columns). No I/O.</summary>
public sealed class CentralRulesTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ana = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
    private static readonly Guid Bia = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(0, 1, true)]
    [InlineData(525_600, null, true)]
    [InlineData(-1, null, false)]
    [InlineData(525_601, null, false)]
    [InlineData(null, 0, false)]
    [InlineData(null, -3, false)]
    public void A_filter_is_valid_only_with_a_sane_open_time_and_number(int? minutos, int? numero, bool valido) =>
        Assert.Equal(valido, RegrasDaCentral.Valido(new FiltroDaCentral(TempoMinimoMinutos: minutos, Numero: numero)));

    [Fact]
    public void Each_filter_maps_to_its_column_and_the_open_time_becomes_an_opening_instant()
    {
        var mesa = Guid.NewGuid();

        var consulta = RegrasDaCentral.Mapear(new FiltroDaCentral(Ana, mesa, 30, 7), null, Agora);

        Assert.Equal(new ConsultaDaCentral(Ana, mesa, Agora.AddMinutes(-30), 7), consulta);
        Assert.Equal(new ConsultaDaCentral(), RegrasDaCentral.Mapear(new FiltroDaCentral(), null, Agora));
    }

    [Fact]
    public void A_caller_limited_to_their_own_comandas_is_always_narrowed_to_themselves()
    {
        Assert.Equal(Ana, RegrasDaCentral.Mapear(new FiltroDaCentral(), Ana, Agora)!.GarcomId);
        Assert.Equal(Ana, RegrasDaCentral.Mapear(new FiltroDaCentral(Ana), Ana, Agora)!.GarcomId);
    }

    [Fact]
    public void A_limited_caller_asking_for_another_waiter_can_match_nothing()
    {
        Assert.Null(RegrasDaCentral.Mapear(new FiltroDaCentral(Bia), Ana, Agora));
        Assert.Equal(Bia, RegrasDaCentral.Mapear(new FiltroDaCentral(Bia), null, Agora)!.GarcomId);
    }
}
