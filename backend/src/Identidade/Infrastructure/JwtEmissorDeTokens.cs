using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>
/// Signing keys and lifetimes of the access tokens. The keys come from CONFIGURATION (environment variables), never from the repository: <c>Flow4Tech:Tokens:AppKey</c> (current),
/// <c>Flow4Tech:Tokens:AppKeyPrevious</c> (still accepted while a rotation drains) and <c>Flow4Tech:Tokens:ControlKey</c> (reserved for the Control Tower realm). Each is at least 32
/// bytes of text (HMAC-SHA256 needs a key as long as its output).
/// </summary>
public sealed class TokensOptions
{
    public const int MinimumKeyBytes = 32;

    public string? AppKey { get; set; }

    public string? AppKeyPrevious { get; set; }

    public string? ControlKey { get; set; }

    public string Issuer { get; set; } = "flow4tech";

    /// <summary>Lifetime of an access token (default 5 minutes). Invisible to the user: the refresh renews it.</summary>
    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Tolerance for clock differences when checking <c>exp</c>/<c>nbf</c> (default 30 seconds).</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Access tokens as JWT (JWS compact, HS256) over <c>Microsoft.IdentityModel.JsonWebTokens</c>. Hard rules, all covered by tests:
/// the algorithm is FIXED (HS256; <c>none</c> and any other algorithm are refused), the signature is required, the issuer and the AUDIENCE are checked (audience = realm: a token of
/// <c>control</c> is refused by <c>app</c> and the other way round, and the realms use different keys), <c>exp</c> is required and the lifetime is checked against the injected
/// <see cref="IClock"/> (not the machine clock), with a small skew. Any failure is null, never an exception or a reason.
/// </summary>
public sealed class JwtEmissorDeTokens : IEmissorDeTokens
{
    private readonly IClock _clock;
    private readonly TokensOptions _options;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };
    private readonly Dictionary<RealmDeToken, RealmKeys> _keys = [];

    public JwtEmissorDeTokens(IClock clock, TokensOptions options)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock;
        _options = options;
        _keys[RealmDeToken.App] = new RealmKeys(
            Key(options.AppKey, "Flow4Tech:Tokens:AppKey", required: true)!,
            Key(options.AppKeyPrevious, "Flow4Tech:Tokens:AppKeyPrevious", required: false));
        if (Key(options.ControlKey, "Flow4Tech:Tokens:ControlKey", required: false) is { } control)
        {
            _keys[RealmDeToken.Control] = new RealmKeys(control, null);
        }

        if (options.AccessLifetime <= TimeSpan.Zero || options.ClockSkew < TimeSpan.Zero || options.ClockSkew > TimeSpan.FromMinutes(2))
        {
            throw new InvalidOperationException("The access token lifetime must be positive and the clock skew between zero and two minutes.");
        }
    }

    public TimeSpan ValidadeDoAcesso => _options.AccessLifetime;

    public string EmitirAcesso(RealmDeToken realm, ClaimsDeAcesso claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (!_keys.TryGetValue(realm, out var keys))
        {
            throw new InvalidOperationException($"The {Audience(realm)} realm has no signing key configured.");
        }

        var now = _clock.UtcNow.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = Audience(realm),
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, claims.ContaId.ToString("D")),
                new Claim("org", claims.OrganizacaoId.ToString("D")),
                new Claim("sid", claims.SessaoId.ToString("D")),
                new Claim("ver", claims.VersaoCredencial.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer32),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            NotBefore = now,
            IssuedAt = now,
            Expires = now + _options.AccessLifetime,
            SigningCredentials = new SigningCredentials(keys.Current, SecurityAlgorithms.HmacSha256),
        };
        return _handler.CreateToken(descriptor);
    }

    public ClaimsDeAcesso? ValidarAcesso(RealmDeToken realm, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048 || !_keys.TryGetValue(realm, out var keys))
        {
            return null;
        }

        try
        {
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = true,
                ValidAudience = Audience(realm),
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                IssuerSigningKeys = keys.All,
                // The lifetime is judged by the injected clock (not the machine's), so tests and the host share one source of time; exp is required.
                ValidateLifetime = true,
                LifetimeValidator = LifetimeIsValid,
            };
            var result = _handler.ValidateTokenAsync(token, parameters).GetAwaiter().GetResult();
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            {
                return null;
            }

            if (!Guid.TryParseExact(jwt.Subject, "D", out var conta)
                || !Guid.TryParseExact(Claim(jwt, "org"), "D", out var organizacao)
                || !Guid.TryParseExact(Claim(jwt, "sid"), "D", out var sessao)
                || !int.TryParse(Claim(jwt, "ver"), NumberStyles.None, CultureInfo.InvariantCulture, out var versao))
            {
                return null;
            }

            return new ClaimsDeAcesso(conta, organizacao, sessao, versao);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool LifetimeIsValid(DateTime? notBefore, DateTime? expires, SecurityToken token, TokenValidationParameters parameters)
    {
        var now = _clock.UtcNow.UtcDateTime;
        return expires is { } exp && exp + _options.ClockSkew > now && (notBefore is not { } nbf || nbf - _options.ClockSkew <= now);
    }

    private static string? Claim(JsonWebToken jwt, string type) => jwt.TryGetClaim(type, out var claim) ? claim.Value : null;

    private static string Audience(RealmDeToken realm) => realm == RealmDeToken.App ? "app" : "control";

    private static SymmetricSecurityKey? Key(string? value, string name, bool required)
    {
        if (string.IsNullOrEmpty(value))
        {
            return required ? throw new InvalidOperationException($"{name} is required: set it to a random secret of at least {TokensOptions.MinimumKeyBytes} bytes in the environment.") : null;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < TokensOptions.MinimumKeyBytes)
        {
            throw new InvalidOperationException($"{name} must have at least {TokensOptions.MinimumKeyBytes} bytes.");
        }

        // The key id is a short hash of the key (it identifies WHICH key signed; it does not reveal it) so a rotation picks the right key without trying all.
        return new SymmetricSecurityKey(bytes) { KeyId = Convert.ToHexString(SHA256.HashData(bytes))[..16] };
    }

    private sealed class RealmKeys
    {
        public RealmKeys(SymmetricSecurityKey current, SymmetricSecurityKey? previous)
        {
            Current = current;
            All = previous is null ? [current] : [current, previous];
        }

        public SymmetricSecurityKey Current { get; }

        public IReadOnlyList<SecurityKey> All { get; }
    }
}
