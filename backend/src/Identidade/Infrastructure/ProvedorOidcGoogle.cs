using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>
/// Google sign-in settings (env: <c>Flow4Tech__Identidade__Google__ClientId|ClientSecret|RedirectUri</c>). Without client id and secret the feature is OFF (the routes answer 404). The secret only ever goes to the
/// token endpoint over HTTPS. <see cref="RedirectUri"/> must be the callback registered at Google (<c>/login/google/retorno</c> of this API): https, or http only on a loopback address for local development.
/// The endpoints are settings so the tests point them at an in-memory provider; the defaults are Google's published ones.
/// </summary>
public sealed class OpcoesGoogle
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public Uri? RedirectUri { get; set; }

    public Uri EndpointDeAutorizacao { get; set; } = new("https://accounts.google.com/o/oauth2/v2/auth");

    public Uri EndpointDeToken { get; set; } = new("https://oauth2.googleapis.com/token");

    public Uri EndpointDeChaves { get; set; } = new("https://www.googleapis.com/oauth2/v3/certs");

    public IReadOnlyList<string> Emissores { get; set; } = ["https://accounts.google.com", "accounts.google.com"];

    public bool Configurada => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && RedirectUri is not null;

    public void Validar()
    {
        if (!Configurada)
        {
            return;
        }

        var loopback = RedirectUri!.IsLoopback;
        if (RedirectUri.Scheme != Uri.UriSchemeHttps && !(loopback && RedirectUri.Scheme == Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("The Google redirect URI must be https (http only on a loopback address).");
        }

        if (EndpointDeToken.Scheme != Uri.UriSchemeHttps || EndpointDeChaves.Scheme != Uri.UriSchemeHttps || EndpointDeAutorizacao.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The Google endpoints must be https.");
        }
    }
}

/// <summary>No provider configured: nothing starts, nothing completes.</summary>
public sealed class OidcNaoConfigurado : IProvedorOidc
{
    public bool Configurado => false;

    public Uri UrlDeAutorizacao(string state, string nonce, string codeChallenge) => throw new InvalidOperationException("No OpenID Connect provider is configured.");

    public Task<IdentidadeOidc?> TrocarCodigoAsync(string code, string codeVerifier, string nonceEsperado, CancellationToken cancellationToken) => Task.FromResult<IdentidadeOidc?>(null);
}

/// <summary>
/// Google over OpenID Connect, authorization code flow with PKCE (S256) and a confidential client. The id token is trusted ONLY after it passes ALL of: signature by a key of the provider's JWKS (algorithm pinned to
/// RS256, <c>none</c> and every other algorithm refused, <c>kid</c> must exist), issuer, audience = our client id, lifetime by the injected clock (60 s skew), and <c>nonce</c> equal to the one of this round trip. Anything
/// else, or any network/format failure, is null: the reason is never exposed. Keys are cached for an hour; an unknown <c>kid</c> triggers ONE refetch and, if still unknown, no other refetch for a minute (a forged
/// <c>kid</c> cannot make the service hammer the provider).
/// </summary>
public sealed class ProvedorOidcGoogle(HttpClient http, OpcoesGoogle opcoes, IClock clock) : IProvedorOidc, IDisposable
{
    private static readonly TimeSpan IdadeMaximaDasChaves = TimeSpan.FromHours(1);
    private static readonly TimeSpan PausaAposKidAusente = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tolerancia = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _trava = new(1, 1);
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };
    private IReadOnlyList<SecurityKey> _chaves = [];
    private DateTimeOffset? _carregadoEm;
    private DateTimeOffset? _kidAusenteEm;

    public bool Configurado => opcoes.Configurada;

    public void Dispose() => _trava.Dispose();

    public Uri UrlDeAutorizacao(string state, string nonce, string codeChallenge)
    {
        var query = string.Join(
            '&',
            new (string, string)[]
            {
                ("client_id", opcoes.ClientId!),
                ("redirect_uri", opcoes.RedirectUri!.ToString()),
                ("response_type", "code"),
                ("scope", "openid email"),
                ("state", state),
                ("nonce", nonce),
                ("code_challenge", codeChallenge),
                ("code_challenge_method", "S256"),
                ("prompt", "select_account"),
            }.Select(p => Uri.EscapeDataString(p.Item1) + "=" + Uri.EscapeDataString(p.Item2)));
        return new UriBuilder(opcoes.EndpointDeAutorizacao) { Query = query }.Uri;
    }

    public async Task<IdentidadeOidc?> TrocarCodigoAsync(string code, string codeVerifier, string nonceEsperado, CancellationToken cancellationToken)
    {
        if (!Configurado)
        {
            return null;
        }

        try
        {
            var idToken = await TrocarPorIdTokenAsync(code, codeVerifier, cancellationToken);
            return idToken is null ? null : await ValidarAsync(idToken, nonceEsperado, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or SecurityTokenException or InvalidOperationException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null; // network down, timeout, malformed answer or token: the same "not proven" for the caller
        }
    }

    private async Task<string?> TrocarPorIdTokenAsync(string code, string codeVerifier, CancellationToken cancellationToken)
    {
        using var corpo = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = opcoes.ClientId!,
            ["client_secret"] = opcoes.ClientSecret!,
            ["redirect_uri"] = opcoes.RedirectUri!.ToString(),
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier,
        });
        using var resposta = await http.PostAsync(opcoes.EndpointDeToken, corpo, cancellationToken);
        if (!resposta.IsSuccessStatusCode)
        {
            return null;
        }

        var tokens = await resposta.Content.ReadFromJsonAsync<RespostaDeToken>(cancellationToken);
        return string.IsNullOrEmpty(tokens?.IdToken) || tokens.IdToken.Length > 8192 ? null : tokens.IdToken;
    }

    private async Task<IdentidadeOidc?> ValidarAsync(string idToken, string nonceEsperado, CancellationToken cancellationToken)
    {
        var kid = new JsonWebToken(idToken).Kid;
        var agora = clock.UtcNow;
        var chaves = await ChavesParaAsync(kid, cancellationToken);
        var daChave = chaves.Where(k => kid is not null && k.KeyId == kid).ToList();
        if (daChave.Count == 0)
        {
            return null; // the token must name a key of the provider's set; the library would otherwise try them all
        }

        var parametros = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = opcoes.Emissores,
            ValidateAudience = true,
            ValidAudience = opcoes.ClientId,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = daChave,
            ValidateLifetime = true,
            // The lifetime is judged by the injected clock (not the machine's), like the access tokens.
            LifetimeValidator = (notBefore, expires, _, _) => expires is { } exp && exp.ToUniversalTime() > agora.UtcDateTime - Tolerancia && (notBefore is not { } nb || nb.ToUniversalTime() <= agora.UtcDateTime + Tolerancia),
        };
        var resultado = await _handler.ValidateTokenAsync(idToken, parametros);
        if (!resultado.IsValid)
        {
            return null;
        }

        var claims = resultado.Claims;
        if (!(claims.TryGetValue("nonce", out var nonce) && nonce is string n && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(n), Encoding.UTF8.GetBytes(nonceEsperado)))
            || !(claims.TryGetValue("sub", out var sub) && sub is string subject && subject.Length is > 0 and <= 255))
        {
            return null;
        }

        var email = claims.TryGetValue("email", out var e) ? e as string : null;
        var verificado = claims.TryGetValue("email_verified", out var v) && v switch
        {
            bool b => b,
            string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        return new IdentidadeOidc(subject, email, verificado);
    }

    private async Task<IReadOnlyList<SecurityKey>> ChavesParaAsync(string? kid, CancellationToken cancellationToken)
    {
        await _trava.WaitAsync(cancellationToken);
        try
        {
            var agora = clock.UtcNow;
            if (_carregadoEm is null || agora - _carregadoEm > IdadeMaximaDasChaves)
            {
                await RecarregarAsync(cancellationToken);
            }
            else if (kid is not null && !_chaves.Any(k => k.KeyId == kid) && (_kidAusenteEm is null || agora - _kidAusenteEm >= PausaAposKidAusente))
            {
                await RecarregarAsync(cancellationToken);
                if (!_chaves.Any(k => k.KeyId == kid))
                {
                    _kidAusenteEm = agora;
                }
            }

            return _chaves;
        }
        finally
        {
            _trava.Release();
        }
    }

    private async Task RecarregarAsync(CancellationToken cancellationToken)
    {
        using var resposta = await http.GetAsync(opcoes.EndpointDeChaves, cancellationToken);
        resposta.EnsureSuccessStatusCode();
        var conjunto = new JsonWebKeySet(await resposta.Content.ReadAsStringAsync(cancellationToken));
        _chaves = [.. conjunto.GetSigningKeys()];
        _carregadoEm = clock.UtcNow;
    }

    private sealed record RespostaDeToken([property: JsonPropertyName("id_token")] string? IdToken)
    {
        public override string ToString() => "[token]";
    }
}
