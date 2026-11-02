namespace Flow4Tech.Kernel.Audit;

/// <summary>Technical log severity (the contract names no logging library).</summary>
public enum LogSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// Structured technical log, SEPARATE from the audit trail (5.6.6): for operators, may be sampled or rotated, and masks secrets
/// (<see cref="SensitiveData"/>). Every line carries the correlation id (<see cref="Correlation.Current"/>).
/// </summary>
public interface ITechnicalLog
{
    void Write(LogSeverity severity, string eventName, IReadOnlyDictionary<string, object?>? fields = null);
}

/// <summary>
/// The id that ties the request, the technical log and the audit record together: the W3C trace id (<c>traceparent</c>) of the current
/// <see cref="System.Diagnostics.Activity"/> (ASP.NET Core starts one per request and honors an incoming <c>traceparent</c>). Outside any activity
/// (a background job) a random 32-hex id is created once and kept for the rest of that execution flow.
/// </summary>
public static class Correlation
{
    private static readonly AsyncLocal<string?> Fallback = new();

    public static string Current
    {
        get
        {
            var activity = System.Diagnostics.Activity.Current;
            if (activity is not null && activity.TraceId != default)
            {
                return activity.TraceId.ToHexString();
            }

            return Fallback.Value ??= Guid.CreateVersion7().ToString("N");
        }
    }
}
