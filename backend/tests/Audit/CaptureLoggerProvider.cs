using Microsoft.Extensions.Logging;

namespace Flow4Tech.Audit.Tests;

internal sealed record CapturedLine(LogLevel Level, string Text, IReadOnlyDictionary<string, string?> Fields)
{
    public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;

    /// <summary>Message, state and scopes as one text: what any sink could write.</summary>
    public string Everything => Text + " " + string.Join(' ', Fields.Select(kv => kv.Key + "=" + kv.Value));
}

/// <summary>Collects every log line with its structured state and the scopes active when it was written.</summary>
internal sealed class CaptureLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public List<CapturedLine> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(CaptureLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            void Add(object? item)
            {
                if (item is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var (key, value) in pairs)
                    {
                        fields[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }

            Add(state);
            owner._scopes.ForEachScope((scope, _) => Add(scope), 0);
            lock (owner.Lines)
            {
                owner.Lines.Add(new CapturedLine(logLevel, formatter(state, exception), fields));
            }
        }
    }
}
