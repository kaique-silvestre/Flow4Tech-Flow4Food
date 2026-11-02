namespace Flow4Tech.Kernel;

/// <summary>Source of time. Always UTC; inject instead of using DateTime.UtcNow directly.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
