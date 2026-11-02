using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flow4Tech.Kernel;

namespace Flow4Tech.Api;

/// <summary>What the authentication layer knows about a signed-in request. The organization comes from here and nowhere else (spec 5.1).</summary>
internal sealed record AuthenticatedSession(Guid SessionId, Guid UserId, Guid OrganizationId, Guid? ActiveUnitId);

/// <summary>
/// Seam between the request context and the real session (tickets 03/04 replace the implementation, nothing else changes). Reading returns
/// null when there is no valid session. Switching the active unit is a write on the session.
/// </summary>
internal interface ISessionSource
{
    Task<AuthenticatedSession?> ReadAsync(HttpContext context, CancellationToken cancellationToken);

    Task SetActiveUnitAsync(AuthenticatedSession session, Guid unitId, CancellationToken cancellationToken);
}

/// <summary>Default (production): there is no session mechanism until ticket 04, so no request is authenticated and every protected route answers 401.</summary>
internal sealed class NoSessionSource : ISessionSource
{
    public Task<AuthenticatedSession?> ReadAsync(HttpContext context, CancellationToken cancellationToken) => Task.FromResult<AuthenticatedSession?>(null);

    public Task SetActiveUnitAsync(AuthenticatedSession session, Guid unitId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No session mechanism is configured.");
}

/// <summary>
/// DECISION (ticket 01): until the real identity (ticket 03) and sessions (ticket 04) exist, tests and local development use a TEST SESSION: a
/// signed header (<c>X-Test-Session</c> = base64url(json) + "." + base64url(HMAC-SHA256)). It is registered ONLY when the environment is
/// Development or Testing AND <c>Flow4Tech:TestSession:Key</c> is configured; in any other environment <see cref="NoSessionSource"/> is
/// used and the header is ignored (covered by a test with a valid signature in Production). The header is signed, never a free client value.
/// Ticket 04 replaces <see cref="ISessionSource"/> and deletes this class.
/// </summary>
internal static class TestSession
{
    public const string HeaderName = "X-Test-Session";
    public const string KeyConfiguration = "Flow4Tech:TestSession:Key";
    private const int MinimumKeyLength = 32;

    public static bool IsEnabled(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        return (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            && configuration[KeyConfiguration] is { Length: >= MinimumKeyLength };
    }

    public static string CreateToken(string key, Guid sessionId, Guid userId, Guid organizationId, Guid? unitId)
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Payload(sessionId, userId, organizationId, unitId)));
        return payload + "." + Base64Url(Sign(key, payload));
    }

    public static AuthenticatedSession? ReadToken(string key, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Split('.') is not [var payload, var signature])
        {
            return null;
        }

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Sign(key, payload), FromBase64Url(signature)))
            {
                return null;
            }

            var data = JsonSerializer.Deserialize<Payload>(FromBase64Url(payload));
            return data is null || data.UserId == Guid.Empty || data.OrganizationId == Guid.Empty
                ? null
                : new AuthenticatedSession(data.SessionId, data.UserId, data.OrganizationId, data.UnitId);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static byte[] Sign(string key, string payload) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    private sealed record Payload(
        [property: JsonPropertyName("sid")] Guid SessionId,
        [property: JsonPropertyName("uid")] Guid UserId,
        [property: JsonPropertyName("org")] Guid OrganizationId,
        [property: JsonPropertyName("unit")] Guid? UnitId);
}

/// <summary>Test-session implementation of <see cref="ISessionSource"/>. The active unit chosen by a switch is kept in memory per session id and wins over the one in the header.</summary>
internal sealed class SignedHeaderSessionSource(string key) : ISessionSource
{
    private readonly ConcurrentDictionary<Guid, Guid> _activeUnits = new();

    public Task<AuthenticatedSession?> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = TestSession.ReadToken(key, context.Request.Headers[TestSession.HeaderName].ToString());
        if (session is not null && _activeUnits.TryGetValue(session.SessionId, out var unit))
        {
            session = session with { ActiveUnitId = unit };
        }

        return Task.FromResult(session);
    }

    public Task SetActiveUnitAsync(AuthenticatedSession session, Guid unitId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _activeUnits[session.SessionId] = unitId;
        return Task.CompletedTask;
    }
}
