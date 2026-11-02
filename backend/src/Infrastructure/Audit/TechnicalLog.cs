using Flow4Tech.Kernel.Audit;
using Microsoft.Extensions.Logging;

namespace Flow4Tech.Infrastructure.Audit;

/// <summary>
/// The technical log (spec 5.6.6): structured lines for operators, SEPARATE from the audit trail (no table, no chain, may be sampled or rotated).
/// Every line is written inside a log scope that carries <c>CorrelationId</c> (<see cref="Correlation.Current"/>, the W3C trace id), so the same id appears
/// in the request, here and in the audit record. Secrets are MASKED, never rejected (<see cref="SensitiveData"/>): a log call must not fail.
/// </summary>
public sealed partial class TechnicalLog(ILogger<TechnicalLog> logger) : ITechnicalLog
{
    public void Write(LogSeverity severity, string eventName, IReadOnlyDictionary<string, object?>? fields = null)
    {
        var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                scope[key] = SensitiveData.IsSensitiveKey(key) ? SensitiveData.Mask : Safe(value);
            }
        }

        scope["CorrelationId"] = Correlation.Current;
        var text = SensitiveData.MaskText(eventName);
        using (logger.BeginScope(scope))
        {
            switch (severity)
            {
                case LogSeverity.Warning:
                    Warning(logger, text);
                    break;
                case LogSeverity.Error:
                    Error(logger, text);
                    break;
                default:
                    Information(logger, text);
                    break;
            }
        }
    }

    private static object? Safe(object? value) => value switch
    {
        null => null,
        string text => SensitiveData.MaskText(text),
        bool or byte or short or int or long or float or double or decimal or Guid or DateTime or DateTimeOffset => value,
        _ => SensitiveData.MaskText(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "{Evento}")]
    private static partial void Information(ILogger logger, string evento);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Evento}")]
    private static partial void Warning(ILogger logger, string evento);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Evento}")]
    private static partial void Error(ILogger logger, string evento);
}
