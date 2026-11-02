using System.Security.Claims;
using System.Text;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Ticket 04 (spec 5.3.1, 5.3.8): the access token is short, signed with a fixed algorithm, bound to an audience (realm) and keys come from configuration and rotate.</summary>
public sealed class TokenDeAcessoTests
{
    private const string AppKey = "chave-app-0123456789-0123456789-0123456789";
    private const string OutraChave = "outra-chave-9876543210-9876543210-9876543210";
    private const string ControlKey = "chave-control-0123456789-0123456789-01234";

    private static readonly Guid Conta = Guid.Parse("0197a000-0000-7000-8000-0000000000c1");
    private static readonly Guid Org = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    private static readonly Guid Sessao = Guid.Parse("0197a000-0000-7000-8000-0000000000d1");
    private static readonly ClaimsDeAcesso Claims = new(Conta, Org, Sessao, 3);

    private static JwtEmissorDeTokens Emissor(FixedClock? clock = null, Action<TokensOptions>? tweak = null)
    {
        var options = new TokensOptions { AppKey = AppKey, ControlKey = ControlKey };
        tweak?.Invoke(options);
        return new JwtEmissorDeTokens(clock ?? new FixedClock(), options);
    }

    private static string Raw(string algorithm, string key, string audience = "app", string issuer = "flow4tech", DateTime? expires = null, DateTime? notBefore = null, string? kid = null, bool omitExpiration = false)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", Conta.ToString("D")), new Claim("org", Org.ToString("D")), new Claim("sid", Sessao.ToString("D")), new Claim("ver", "3")]),
            NotBefore = notBefore ?? new DateTime(2026, 11, 2, 11, 59, 0, DateTimeKind.Utc),
            Expires = omitExpiration ? null : expires ?? new DateTime(2026, 11, 2, 12, 5, 0, DateTimeKind.Utc),
        };
        if (algorithm != "none")
        {
            descriptor.SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)) { KeyId = kid }, algorithm);
        }

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public void A_token_round_trips_with_subject_organization_session_and_version()
    {
        var emissor = Emissor();

        var token = emissor.EmitirAcesso(RealmDeToken.App, Claims);

        Assert.Equal(Claims, emissor.ValidarAcesso(RealmDeToken.App, token));
    }

    [Fact]
    public void The_token_carries_audience_issuer_expiry_and_the_session_claims_and_the_default_life_is_five_minutes()
    {
        var emissor = Emissor();

        var jwt = new JsonWebToken(emissor.EmitirAcesso(RealmDeToken.App, Claims));

        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal(["app"], jwt.Audiences);
        Assert.Equal("flow4tech", jwt.Issuer);
        Assert.Equal(Conta.ToString("D"), jwt.Subject);
        Assert.Equal(Org.ToString("D"), jwt.GetClaim("org").Value);
        Assert.Equal(Sessao.ToString("D"), jwt.GetClaim("sid").Value);
        Assert.Equal("3", jwt.GetClaim("ver").Value);
        Assert.Equal(new DateTime(2026, 11, 2, 12, 5, 0, DateTimeKind.Utc), jwt.ValidTo);
        Assert.Equal(TimeSpan.FromMinutes(5), emissor.ValidadeDoAcesso);
    }

    [Fact]
    public void A_token_signed_with_another_key_is_refused()
    {
        Assert.Null(Emissor().ValidarAcesso(RealmDeToken.App, Raw("HS256", OutraChave)));
    }

    [Theory]
    [InlineData("HS384")]
    [InlineData("HS512")]
    public void A_token_with_another_algorithm_is_refused_even_with_the_right_key(string algorithm)
    {
        // HS384/HS512 need keys of 48/64 bytes: the app key is 80 here, so the token is a perfectly valid HS384/HS512 token of the SAME key.
        var longKey = AppKey + AppKey;
        var emissor = Emissor(tweak: o => o.AppKey = longKey);

        Assert.NotNull(emissor.ValidarAcesso(RealmDeToken.App, emissor.EmitirAcesso(RealmDeToken.App, Claims))); // control: HS256 of that key passes
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, Raw(algorithm, longKey)));
    }

    [Fact]
    public void An_unsigned_token_with_alg_none_is_refused()
    {
        var emissor = Emissor();
        var unsigned = Raw("none", AppKey);
        Assert.Contains("\"alg\":\"none\"", Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(unsigned.Split('.')[0])), StringComparison.Ordinal);

        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, unsigned));
        // The same token with an emptied signature part and with a signature stripped from a good one.
        var good = emissor.EmitirAcesso(RealmDeToken.App, Claims).Split('.');
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, $"{good[0]}.{good[1]}."));
    }

    [Fact]
    public void A_tampered_payload_is_refused()
    {
        var emissor = Emissor();
        var parts = emissor.EmitirAcesso(RealmDeToken.App, Claims).Split('.');
        var evil = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace(Org.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal)));

        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, $"{parts[0]}.{evil}.{parts[2]}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("lixo")]
    [InlineData("a.b.c")]
    [InlineData("a.b.c.d.e")]
    public void Garbage_is_refused_without_throwing(string token)
    {
        Assert.Null(Emissor().ValidarAcesso(RealmDeToken.App, token));
    }

    [Fact]
    public void An_expired_token_is_refused_by_the_injected_clock_after_the_small_skew()
    {
        var clock = new FixedClock();
        var emissor = Emissor(clock);
        var token = emissor.EmitirAcesso(RealmDeToken.App, Claims);

        clock.UtcNow = clock.UtcNow.AddMinutes(5).AddSeconds(29); // inside the 30 s skew
        Assert.NotNull(emissor.ValidarAcesso(RealmDeToken.App, token));
        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, token));
    }

    [Fact]
    public void A_token_that_is_not_valid_yet_is_refused()
    {
        var clock = new FixedClock();
        var future = Raw("HS256", AppKey, notBefore: new DateTime(2026, 11, 2, 12, 10, 0, DateTimeKind.Utc), expires: new DateTime(2026, 11, 2, 12, 15, 0, DateTimeKind.Utc));

        Assert.Null(Emissor(clock).ValidarAcesso(RealmDeToken.App, future));
    }

    [Fact]
    public void A_token_without_expiration_is_refused()
    {
        // The library gives a token with no Expires a default hour, so these are built from a raw payload.
        string Build(string exp) => new JsonWebTokenHandler().CreateToken(
            $$"""{"iss":"flow4tech","aud":"app","sub":"{{Conta:D}}","org":"{{Org:D}}","sid":"{{Sessao:D}}","ver":3,"nbf":{{new DateTimeOffset(2026, 11, 2, 11, 59, 0, TimeSpan.Zero).ToUnixTimeSeconds()}}{{exp}}}""",
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AppKey)), "HS256"));

        Assert.NotNull(Emissor().ValidarAcesso(RealmDeToken.App, Build($",\"exp\":{new DateTimeOffset(2026, 11, 2, 12, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds()}"))); // control: the recipe is valid with exp
        Assert.Null(Emissor().ValidarAcesso(RealmDeToken.App, Build(string.Empty)));
    }

    [Fact]
    public void A_token_for_another_issuer_is_refused()
    {
        Assert.Null(Emissor().ValidarAcesso(RealmDeToken.App, Raw("HS256", AppKey, issuer: "outro")));
    }

    [Fact]
    public void A_control_token_is_refused_by_the_app_and_an_app_token_by_control()
    {
        var emissor = Emissor();
        var app = emissor.EmitirAcesso(RealmDeToken.App, Claims);
        var control = emissor.EmitirAcesso(RealmDeToken.Control, Claims);

        Assert.NotNull(emissor.ValidarAcesso(RealmDeToken.App, app));
        Assert.NotNull(emissor.ValidarAcesso(RealmDeToken.Control, control));
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, control));
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.Control, app));
    }

    [Fact]
    public void The_audience_alone_separates_the_realms_even_when_the_same_key_signed_both()
    {
        var emissor = Emissor();

        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, Raw("HS256", AppKey, audience: "control")));
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, Raw("HS256", ControlKey, audience: "control")));
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.App, Raw("HS256", ControlKey, audience: "app")));
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.Control, Raw("HS256", AppKey, audience: "app")));
    }

    [Fact]
    public void Without_a_control_key_the_control_realm_issues_and_accepts_nothing()
    {
        var emissor = Emissor(tweak: o => o.ControlKey = null);

        Assert.Throws<InvalidOperationException>(() => emissor.EmitirAcesso(RealmDeToken.Control, Claims));
        Assert.Null(emissor.ValidarAcesso(RealmDeToken.Control, Raw("HS256", ControlKey, audience: "control")));
    }

    [Fact]
    public void Rotation_new_tokens_use_the_current_key_and_the_previous_key_still_validates_until_removed()
    {
        var before = Emissor(tweak: o => o.AppKey = OutraChave).EmitirAcesso(RealmDeToken.App, Claims);
        var rotated = Emissor(tweak: o =>
        {
            o.AppKey = AppKey;
            o.AppKeyPrevious = OutraChave;
        });
        var dropped = Emissor();

        Assert.NotNull(rotated.ValidarAcesso(RealmDeToken.App, before)); // issued under the previous key
        Assert.Null(dropped.ValidarAcesso(RealmDeToken.App, before)); // previous key removed
        var fresh = rotated.EmitirAcesso(RealmDeToken.App, Claims);
        Assert.NotNull(dropped.ValidarAcesso(RealmDeToken.App, fresh)); // new tokens are signed with the CURRENT key
        Assert.NotEqual(new JsonWebToken(before).Kid, new JsonWebToken(fresh).Kid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curta-demais")]
    [InlineData("0123456789012345678901234567890")] // 31 bytes
    public void A_missing_or_short_key_stops_the_emitter_from_being_built(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => Emissor(tweak: o => o.AppKey = key));
        Assert.Throws<InvalidOperationException>(() => Emissor(tweak: o => o.AppKeyPrevious = "curta"));
    }

    [Fact]
    public void The_token_never_contains_the_key_or_a_hash_or_a_password()
    {
        var token = Emissor().EmitirAcesso(RealmDeToken.App, Claims);
        var decoded = string.Join(' ', token.Split('.').Take(2).Select(p => Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(p))));

        Assert.DoesNotContain(AppKey, decoded, StringComparison.Ordinal);
        Assert.DoesNotContain("argon", decoded, StringComparison.OrdinalIgnoreCase);
    }
}
