using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Kernel.Audit;
using Microsoft.Extensions.Logging;

namespace Flow4Tech.Audit.Tests;

/// <summary>Spec 5.6.6: structured technical log, separate from the audit trail, same correlation id, secrets masked.</summary>
public sealed class TechnicalLogTests
{
    private const string Password = "S3nh@Secreta!2026";
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
    private const string Hash = "$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy";

    [Fact]
    public void A_line_carries_the_event_name_the_fields_and_the_correlation_id_of_the_current_activity()
    {
        using var rig = new LogRig();
        var capture = rig.Capture;
        var log = rig.Log;
        using var activity = new System.Diagnostics.Activity("req");
        activity.Start();

        log.Write(LogSeverity.Information, "http.requisicao", new Dictionary<string, object?> { ["status"] = 200, ["rota"] = "/unidades" });

        var line = Assert.Single(capture.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("http.requisicao", line.Text, StringComparison.Ordinal);
        Assert.Equal(activity.TraceId.ToHexString(), line.Field("CorrelationId"));
        Assert.Equal("200", line.Field("status"));
        Assert.Equal("/unidades", line.Field("rota"));
    }

    [Fact]
    public void Known_secrets_written_to_the_log_are_masked_by_key_and_by_value()
    {
        using var rig = new LogRig();
        var capture = rig.Capture;
        var log = rig.Log;

        log.Write(
            LogSeverity.Warning,
            "login.negado " + Jwt,
            new Dictionary<string, object?>
            {
                ["senha"] = Password,
                ["passwordHash"] = Hash,
                ["accessToken"] = Jwt,
                ["detalhe"] = "enviou Bearer abcdef0123456789abcdef e " + Hash,
                ["usuario"] = "maria",
            });

        var everything = string.Join('\n', capture.Lines.Select(l => l.Everything));
        foreach (var secret in new[] { Password, Jwt, Hash, "abcdef0123456789abcdef" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }

        Assert.Contains("maria", everything, StringComparison.Ordinal); // ordinary data is kept
        Assert.Contains(SensitiveData.Mask, everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_an_activity_the_log_still_gets_a_stable_correlation_id_per_flow()
    {
        using var rig = new LogRig();
        var capture = rig.Capture;
        var log = rig.Log;

        log.Write(LogSeverity.Information, "a");
        log.Write(LogSeverity.Information, "b");

        Assert.Equal(2, capture.Lines.Count);
        Assert.Equal(32, capture.Lines[0].Field("CorrelationId")!.Length);
        Assert.Equal(capture.Lines[0].Field("CorrelationId"), capture.Lines[1].Field("CorrelationId"));
    }

    [Fact]
    public void Correlation_is_the_w3c_trace_id_of_the_current_activity()
    {
        using var activity = new System.Diagnostics.Activity("req");
        activity.Start();

        Assert.Equal(activity.TraceId.ToHexString(), Correlation.Current);
        Assert.Equal(32, Correlation.Current.Length);
    }
}

internal sealed class LogRig : IDisposable
{
    private readonly LoggerFactory _factory;

    public LogRig()
    {
        _factory = new LoggerFactory([Capture]);
        Log = new TechnicalLog(_factory.CreateLogger<TechnicalLog>());
    }

    public CaptureLoggerProvider Capture { get; } = new();

    public TechnicalLog Log { get; }

    public void Dispose()
    {
        _factory.Dispose();
        Capture.Dispose();
    }
}
