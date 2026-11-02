namespace Flow4Tech.Permissoes.Domain;

/// <summary>
/// How a unit turns an instant into its OPERATING DAY (the "day" of a cumulative cap): the unit's time zone plus the local time at which
/// the next operating day starts. Operations before the rollover still belong to the previous calendar date (a bar closing at 02:00 is
/// still "yesterday"). Pure BCL, no I/O.
/// </summary>
/// <remarks>
/// Defaults: America/Sao_Paulo and 05:00. Rollover 05:00 sits after the usual late closing (03:00-04:00) and before the morning opening;
/// units that open earlier or close later set their own. The comparison is on local wall-clock time, so a DST shift never splits or
/// merges a day (the repeated hour of a fall-back stays in one day) as long as the rollover is not inside the zone's DST gap.
/// </remarks>
public sealed class OperatingDayRule
{
    public const string DefaultTimeZoneId = "America/Sao_Paulo";

    public static readonly TimeOnly DefaultRollover = new(5, 0);

    private readonly TimeZoneInfo _zone;

    public OperatingDayRule(string timeZoneId, TimeOnly rollover)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new ArgumentException($"Unknown time zone '{timeZoneId}'.", nameof(timeZoneId), ex);
        }

        TimeZoneId = timeZoneId;
        Rollover = rollover;
    }

    public static OperatingDayRule Default { get; } = new(DefaultTimeZoneId, DefaultRollover);

    public string TimeZoneId { get; }

    public TimeOnly Rollover { get; }

    public DateOnly DayOf(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, _zone).DateTime;
        return DateOnly.FromDateTime(local - Rollover.ToTimeSpan());
    }
}
