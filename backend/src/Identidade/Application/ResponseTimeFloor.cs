using System.Diagnostics;

namespace Flow4Tech.Identidade.Application;

/// <summary>Makes every login outcome take at least a fixed time, so the cost of the audit write of a known account does not tell it from an unknown one.</summary>
public interface IResponseTimeFloor
{
    Task AguardarAsync(long startedAtTimestamp, CancellationToken cancellationToken);
}

/// <summary>
/// Waits for whatever is missing until <paramref name="minimum"/> has passed since <c>startedAtTimestamp</c> (<see cref="Stopwatch.GetTimestamp"/>). It is a floor, not
/// a ceiling: if the work itself takes longer (a slow database), the response is slower than the floor and that excess is NOT hidden. Choose the floor above the usual
/// cost of the slowest path (hashing plus one audit write).
/// </summary>
public sealed class ResponseTimeFloor(TimeSpan minimum, Func<TimeSpan, CancellationToken, Task>? delay = null) : IResponseTimeFloor
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public Task AguardarAsync(long startedAtTimestamp, CancellationToken cancellationToken)
    {
        var missing = minimum - Stopwatch.GetElapsedTime(startedAtTimestamp);
        return missing > TimeSpan.Zero ? _delay(missing, cancellationToken) : Task.CompletedTask;
    }
}
