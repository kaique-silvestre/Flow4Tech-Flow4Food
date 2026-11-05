using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Flow4Tech.Identidade.Tests;

/// <summary>An OpenID Connect provider living in memory: token endpoint and JWKS served by a message handler, id tokens signed with a test RSA key. No network.</summary>
internal sealed class ProvedorOidcFalso : HttpMessageHandler
{
    public const string ClientId = "cliente-de-teste.apps.googleusercontent.com";
    public const string ClientSecret = "segredo-de-teste";
    public const string Redirect = "https://api.exemplo.com/login/google/retorno";
    public const string Emissor = "https://accounts.google.com";

    public RSA Chave { get; private set; } = RSA.Create(2048);

    public string Kid { get; set; } = "k1";

    public int BuscasDeChaves { get; private set; }

    public List<Dictionary<string, string>> Trocas { get; } = [];

    public string Codigo { get; set; } = "codigo-bom";

    public string? IdTokenFixo { get; set; }

    public HttpStatusCode StatusDoToken { get; set; } = HttpStatusCode.OK;

    public void GirarChave(string novoKid)
    {
        Chave.Dispose();
        Chave = RSA.Create(2048);
        Kid = novoKid;
    }

    public string IdToken(Action<Dictionary<string, object>>? ajustar = null, DateTimeOffset? agora = null, RSA? assinarCom = null, string? kid = null, string algoritmo = SecurityAlgorithms.RsaSha256, string? emissor = null, string? audiencia = null)
    {
        var now = (agora ?? DateTimeOffset.UtcNow).UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "110169484474386276334",
            ["email"] = "maria@a.com",
            ["email_verified"] = true,
            ["nonce"] = "nonce-esperado",
        };
        ajustar?.Invoke(claims);
        var chave = new RsaSecurityKey(assinarCom ?? Chave) { KeyId = kid ?? Kid };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = emissor ?? Emissor,
            Audience = audiencia ?? ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(chave, algoritmo),
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/jwks" && request.Method == HttpMethod.Get)
        {
            BuscasDeChaves++;
            var parametros = Chave.ExportParameters(false);
            var jwk = new { kty = "RSA", use = "sig", alg = "RS256", kid = Kid, n = Base64UrlEncoder.Encode(parametros.Modulus), e = Base64UrlEncoder.Encode(parametros.Exponent) };
            return Json(HttpStatusCode.OK, new { keys = new[] { jwk } });
        }

        if (request.RequestUri.AbsolutePath == "/token" && request.Method == HttpMethod.Post)
        {
            var corpo = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = corpo.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            Trocas.Add(form);
            var certo = form.GetValueOrDefault("code") == Codigo && form.GetValueOrDefault("client_id") == ClientId && form.GetValueOrDefault("client_secret") == ClientSecret
                && form.GetValueOrDefault("redirect_uri") == Redirect && form.GetValueOrDefault("grant_type") == "authorization_code" && !string.IsNullOrEmpty(form.GetValueOrDefault("code_verifier"));
            return !certo || StatusDoToken != HttpStatusCode.OK
                ? Json(certo ? StatusDoToken : HttpStatusCode.BadRequest, new { error = "invalid_grant" })
                : Json(HttpStatusCode.OK, new { access_token = "nao-usado", id_token = IdTokenFixo ?? IdToken(), token_type = "Bearer" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

/// <summary>The real <see cref="ProvedorOidcGoogle"/> against the in-memory provider: id token validation (signature, algorithm, issuer, audience, lifetime, nonce) and the PKCE code exchange.</summary>
public sealed class ProvedorOidcGoogleTests : IDisposable
{
    private readonly ProvedorOidcFalso _servidor = new();
    private readonly FixedClock _clock = new() { UtcNow = DateTimeOffset.UtcNow };
    private readonly HttpClient _http;

    private readonly ProvedorOidcGoogle _provedor;

    public ProvedorOidcGoogleTests()
    {
        _http = new HttpClient(_servidor);
        _provedor = new ProvedorOidcGoogle(
            _http,
            new OpcoesGoogle
            {
                ClientId = ProvedorOidcFalso.ClientId,
                ClientSecret = ProvedorOidcFalso.ClientSecret,
                RedirectUri = new Uri(ProvedorOidcFalso.Redirect),
                EndpointDeToken = new Uri("https://provedor.teste/token"),
                EndpointDeChaves = new Uri("https://provedor.teste/jwks"),
            },
            _clock);
    }

    public void Dispose()
    {
        _provedor.Dispose();
        _http.Dispose();
        _servidor.Dispose();
    }

    private static Task<IdentidadeOidc?> Trocar(ProvedorOidcGoogle provedor, string code = "codigo-bom") => provedor.TrocarCodigoAsync(code, "verifier-de-teste", "nonce-esperado", CancellationToken.None);

    [Fact]
    public async Task A_valid_id_token_yields_the_verified_identity()
    {
        var identidade = await Trocar(_provedor);

        Assert.Equal(new IdentidadeOidc("110169484474386276334", "maria@a.com", true), identidade);
        var troca = Assert.Single(_servidor.Trocas);
        Assert.Equal("verifier-de-teste", troca["code_verifier"]); // PKCE: the verifier goes to the token endpoint only
    }

    [Fact]
    public void The_authorization_url_has_everything_the_provider_needs_and_no_secret()
    {
        var url = _provedor.UrlDeAutorizacao("meu-state", "meu-nonce", "meu-challenge");

        Assert.Equal("accounts.google.com", url.Host);
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal(ProvedorOidcFalso.ClientId, q["client_id"]);
        Assert.Equal(ProvedorOidcFalso.Redirect, q["redirect_uri"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("openid email", q["scope"]);
        Assert.Equal("meu-state", q["state"]);
        Assert.Equal("meu-nonce", q["nonce"]);
        Assert.Equal("meu-challenge", q["code_challenge"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.DoesNotContain(ProvedorOidcFalso.ClientSecret, url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_e_mail_verified_claim_is_reported_as_it_is_even_as_a_string()
    {
        _servidor.IdTokenFixo = _servidor.IdToken(c => c["email_verified"] = false);
        Assert.False((await Trocar(_provedor))!.EmailVerificado);

        _servidor.IdTokenFixo = _servidor.IdToken(c => c["email_verified"] = "true");
        Assert.True((await Trocar(_provedor))!.EmailVerificado);

        _servidor.IdTokenFixo = _servidor.IdToken(c => c.Remove("email_verified"));
        Assert.False((await Trocar(_provedor))!.EmailVerificado);
    }

    [Fact]
    public async Task A_rejected_code_or_a_failing_token_endpoint_is_null()
    {
        Assert.Null(await Trocar(_provedor, "codigo-ruim"));

        _servidor.StatusDoToken = HttpStatusCode.InternalServerError;
        Assert.Null(await Trocar(_provedor));
    }

    [Fact]
    public async Task Every_way_an_id_token_can_be_wrong_is_null()
    {
        var outraChave = RSA.Create(2048);
        var casos = new Dictionary<string, string>
        {
            ["assinatura de outra chave"] = _servidor.IdToken(assinarCom: outraChave),
            ["kid desconhecido"] = _servidor.IdToken(kid: "inexistente"),
            ["nonce errado"] = _servidor.IdToken(c => c["nonce"] = "outro"),
            ["sem nonce"] = _servidor.IdToken(c => c.Remove("nonce")),
            ["expirado"] = _servidor.IdToken(agora: DateTimeOffset.UtcNow.AddHours(-2)),
            ["emissor errado"] = _servidor.IdToken(emissor: "https://malicioso.exemplo.com"),
            ["audiencia errada"] = _servidor.IdToken(audiencia: "outro-cliente"),
            ["sem sub"] = _servidor.IdToken(c => c["sub"] = ""),
            ["algoritmo diferente (PS256)"] = _servidor.IdToken(algoritmo: SecurityAlgorithms.RsaSsaPssSha256),
            ["lixo"] = "isto.nao.e-jwt",
            ["vazio"] = "",
        };

        foreach (var (nome, token) in casos)
        {
            _servidor.IdTokenFixo = token;
            Assert.True(await Trocar(_provedor) is null, "deveria recusar: " + nome);
        }

        outraChave.Dispose();
    }

    [Fact]
    public async Task An_unsigned_token_is_refused()
    {
        var cabecalho = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var corpo = Base64UrlEncoder.Encode($$"""{"iss":"{{ProvedorOidcFalso.Emissor}}","aud":"{{ProvedorOidcFalso.ClientId}}","sub":"1","email":"maria@a.com","email_verified":true,"nonce":"nonce-esperado","exp":{{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}}}""");
        _servidor.IdTokenFixo = cabecalho + "." + corpo + ".";

        Assert.Null(await Trocar(_provedor));
    }

    [Fact]
    public async Task The_lifetime_is_judged_by_the_injected_clock()
    {
        _clock.UtcNow = DateTimeOffset.UtcNow.AddHours(1); // the token was issued "now" in real time: for this clock it is expired
        Assert.Null(await Trocar(_provedor));

        _clock.UtcNow = DateTimeOffset.UtcNow;
        Assert.NotNull(await Trocar(_provedor));
    }

    [Fact]
    public async Task Provider_keys_are_cached_and_refetched_once_on_rotation()
    {
        var provedor = _provedor;
        await Trocar(provedor);
        await Trocar(provedor);
        Assert.Equal(1, _servidor.BuscasDeChaves);

        _servidor.GirarChave("k2");
        Assert.NotNull(await Trocar(provedor)); // unknown kid: one refetch and it works
        Assert.Equal(2, _servidor.BuscasDeChaves);

        _servidor.IdTokenFixo = _servidor.IdToken(kid: "nunca-existiu");
        Assert.Null(await Trocar(provedor));
        Assert.Equal(3, _servidor.BuscasDeChaves); // a forged kid costs ONE refetch, not a loop
    }

    [Fact]
    public async Task Forged_kids_cannot_make_the_service_hammer_the_provider()
    {
        var provedor = _provedor;
        await Trocar(provedor);
        _servidor.IdTokenFixo = _servidor.IdToken(kid: "forjado");

        for (var i = 0; i < 20; i++)
        {
            await Trocar(provedor);
        }

        Assert.True(_servidor.BuscasDeChaves <= 3, $"refetches: {_servidor.BuscasDeChaves}");
    }

    [Fact]
    public void Options_without_a_client_id_or_secret_are_not_configured_and_an_http_redirect_is_refused()
    {
        Assert.False(new OpcoesGoogle().Configurada);
        Assert.True(new OpcoesGoogle { ClientId = "a", ClientSecret = "b", RedirectUri = new Uri("https://x.com/cb") }.Configurada);
        Assert.Throws<InvalidOperationException>(() => new OpcoesGoogle { ClientId = "a", ClientSecret = "b", RedirectUri = new Uri("http://x.com/cb") }.Validar());
        new OpcoesGoogle { ClientId = "a", ClientSecret = "b", RedirectUri = new Uri("http://localhost:8080/cb") }.Validar(); // local development
    }
}
