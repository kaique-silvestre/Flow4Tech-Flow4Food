using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using static Flow4Tech.Authorization.Tests.Fixtures;

namespace Flow4Tech.Authorization.Tests;

public sealed class OperatingDayTests
{
    private static DateTimeOffset Utc(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(11, 3, 2, 30, 2)] // 23:30 local on 2 Nov
    [InlineData(11, 3, 4, 0, 2)] // 01:00 local on 3 Nov, before rollover: still 2 Nov
    [InlineData(11, 3, 7, 59, 2)] // 04:59 local
    [InlineData(11, 3, 8, 0, 3)] // 05:00 local sharp: new operating day
    [InlineData(11, 3, 14, 0, 3)]
    public void Default_rule_is_Sao_Paulo_with_a_05_00_rollover(int month, int day, int hour, int minute, int expectedDay)
    {
        Assert.Equal(new DateOnly(2026, 11, expectedDay), OperatingDayRule.Default.DayOf(Utc(month, day, hour, minute)));
    }

    [Fact]
    public void Defaults_are_documented_values()
    {
        Assert.Equal("America/Sao_Paulo", OperatingDayRule.Default.TimeZoneId);
        Assert.Equal(new TimeOnly(5, 0), OperatingDayRule.Default.Rollover);
    }

    [Theory]
    [InlineData(3, 7, 9, 59, 3, 6)] // 04:59 EST
    [InlineData(3, 7, 10, 0, 3, 7)] // 05:00 EST
    [InlineData(3, 8, 8, 59, 3, 7)] // spring-forward day (02:00 -> 03:00 local at 07:00 UTC): 04:59 EDT
    [InlineData(3, 8, 9, 0, 3, 8)] // 05:00 EDT
    [InlineData(11, 1, 5, 30, 10, 31)] // 01:30 EDT
    [InlineData(11, 1, 6, 30, 10, 31)] // 01:30 EST: the repeated hour stays in the same operating day
    [InlineData(11, 1, 9, 59, 10, 31)] // 04:59 EST
    [InlineData(11, 1, 10, 0, 11, 1)] // 05:00 EST
    public void Daylight_saving_shifts_never_split_or_merge_an_operating_day(int month, int day, int hour, int minute, int expectedMonth, int expectedDay)
    {
        var rule = new OperatingDayRule("America/New_York", new TimeOnly(5, 0));

        Assert.Equal(new DateOnly(2026, expectedMonth, expectedDay), rule.DayOf(Utc(month, day, hour, minute)));
    }

    [Fact]
    public void The_rollover_is_configurable_and_midnight_means_the_plain_local_calendar_day()
    {
        var sixAm = new OperatingDayRule("America/Sao_Paulo", new TimeOnly(6, 0));
        var midnight = new OperatingDayRule("America/Sao_Paulo", TimeOnly.MinValue);

        Assert.Equal(new DateOnly(2026, 11, 2), sixAm.DayOf(Utc(11, 3, 8, 59)));
        Assert.Equal(new DateOnly(2026, 11, 3), sixAm.DayOf(Utc(11, 3, 9, 0)));
        Assert.Equal(new DateOnly(2026, 11, 3), midnight.DayOf(Utc(11, 3, 3, 0)));
        Assert.Equal(new DateOnly(2026, 11, 2), midnight.DayOf(Utc(11, 3, 2, 59)));
    }

    [Fact]
    public void An_unknown_time_zone_is_rejected_when_the_rule_is_built()
    {
        Assert.ThrowsAny<ArgumentException>(() => new OperatingDayRule("Mars/Olympus", new TimeOnly(5, 0)));
        Assert.ThrowsAny<ArgumentException>(() => new OperatingDayRule("", new TimeOnly(5, 0)));
    }

    [Fact]
    public async Task The_resolver_uses_the_units_own_rule_and_the_default_for_other_or_missing_units()
    {
        var clock = new FixedClock(Utc(11, 3, 6, 0)); // 03:00 Sao Paulo, 06:00 Lisbon (UTC+0 in November)
        var resolver = new ConfiguredOperatingDayResolver(clock, new Dictionary<Guid, OperatingDayRule>
        {
            [Unit2] = new("Europe/Lisbon", new TimeOnly(5, 0)),
        });

        Assert.Equal(new DateOnly(2026, 11, 3), await resolver.CurrentAsync(OrgId, Unit2, CancellationToken.None)); // 06:00 Lisbon is past rollover
        Assert.Equal(new DateOnly(2026, 11, 2), await resolver.CurrentAsync(OrgId, Unit1, CancellationToken.None)); // default zone: 03:00
        Assert.Equal(new DateOnly(2026, 11, 2), await resolver.CurrentAsync(OrgId, null, CancellationToken.None));
    }
}
