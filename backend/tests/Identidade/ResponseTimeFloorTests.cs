using System.Diagnostics;
using Flow4Tech.Identidade.Application;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.8: every login outcome takes at least a fixed time.</summary>
public sealed class ResponseTimeFloorTests
{
    [Fact]
    public async Task It_waits_for_what_is_missing_to_reach_the_floor()
    {
        var waited = new List<TimeSpan>();
        var floor = new ResponseTimeFloor(TimeSpan.FromMilliseconds(400), (d, _) =>
        {
            waited.Add(d);
            return Task.CompletedTask;
        });

        await floor.AguardarAsync(Stopwatch.GetTimestamp(), CancellationToken.None);

        var delay = Assert.Single(waited);
        Assert.InRange(delay, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task It_does_not_wait_when_the_work_already_took_longer_or_the_floor_is_zero()
    {
        var waited = new List<TimeSpan>();
        Task Record(TimeSpan d, CancellationToken _)
        {
            waited.Add(d);
            return Task.CompletedTask;
        }

        await new ResponseTimeFloor(TimeSpan.FromMilliseconds(10), Record).AguardarAsync(Stopwatch.GetTimestamp() - Stopwatch.Frequency, CancellationToken.None);
        await new ResponseTimeFloor(TimeSpan.Zero, Record).AguardarAsync(Stopwatch.GetTimestamp(), CancellationToken.None);

        Assert.Empty(waited);
    }

    [Fact]
    public async Task With_the_real_delay_the_call_never_returns_before_the_floor()
    {
        var started = Stopwatch.GetTimestamp();

        await new ResponseTimeFloor(TimeSpan.FromMilliseconds(150)).AguardarAsync(started, CancellationToken.None);

        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(140));
    }
}
