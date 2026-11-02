using System.Text.Json.Nodes;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Audit.Tests;

/// <summary>The secret rule (spec 5.6.3/5.6.6): forbidden keys and secret-looking values. The audit REJECTS, the technical log MASKS.</summary>
public sealed class SensitiveDataTests
{
    [Theory]
    [InlineData("senha")]
    [InlineData("Senha")]
    [InlineData("novaSenha")]
    [InlineData("senha_hash")]
    [InlineData("password")]
    [InlineData("passwordHash")]
    [InlineData("PASSWORD-HASH")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("refresh_token")]
    [InlineData("secret")]
    [InlineData("clientSecret")]
    [InlineData("segredo")]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("credencial_temporaria")]
    [InlineData("credential")]
    [InlineData("hash")]
    [InlineData("privateKey")]
    [InlineData("pass")]
    [InlineData("userPass")]
    [InlineData("pass_confirmacao")]
    [InlineData("passcode")]
    [InlineData("pin")]
    [InlineData("PIN")]
    [InlineData("userPin")]
    [InlineData("pin_code")]
    [InlineData("otp")]
    [InlineData("OTP")]
    [InlineData("otpCode")]
    [InlineData("sms-otp")]
    [InlineData("accessKey")]
    [InlineData("access_key_id")]
    [InlineData("jwt")]
    [InlineData("idJwt")]
    [InlineData("JWT")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghijk")]
    [InlineData("Bearer abcdef0123456789abcdef")]
    [InlineData("$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy")]
    public void Keys_that_name_a_secret_are_sensitive(string key) => Assert.True(SensitiveData.IsSensitiveKey(key), key);

    [Theory]
    [InlineData("nome")]
    [InlineData("unidadeId")]
    [InlineData("quantidade")]
    [InlineData("perfil")]
    [InlineData("email")]
    [InlineData("ativo")]
    [InlineData("passageiro")]
    [InlineData("bypass")]
    [InlineData("shipping")]
    [InlineData("mapping")]
    [InlineData("opinion")]
    [InlineData("hotpink")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_keys_are_not_sensitive(string? key) => Assert.False(SensitiveData.IsSensitiveKey(key), key);

    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [InlineData("$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=4$c29tZXNhbHQ$RdescudvJCsgt3ub+b+dWRWJTmaaJObG")]
    [InlineData("AQAAAAIAAYagAAAAEJ0pFZk6aXk3m3m9kSg0yQ3GQe4c5FVQ6mGZB0Q1m1w6YjP8lQ4m1ZBq7m2m6w==")]
    [InlineData("Bearer abcdef0123456789abcdef")]
    [InlineData("usou o header Authorization: Bearer abcdef0123456789abcdef ontem")]
    public void Values_that_look_like_a_secret_are_detected(string value) => Assert.True(SensitiveData.LooksLikeSecret(value), value);

    [Theory]
    [InlineData("Centro")]
    [InlineData("Troca de unidade para Praia")]
    [InlineData("0197a000-0000-7000-8000-0000000000a1")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_values_are_not_secrets(string? value) => Assert.False(SensitiveData.LooksLikeSecret(value), value);

    [Fact]
    public void Scan_flags_a_key_that_is_itself_a_secret_without_echoing_it()
    {
        var node = JsonNode.Parse("""{ "ok": 1, "Bearer abcdef0123456789abcdef": "x" }""");

        var findings = SensitiveData.Scan(node);

        Assert.Single(findings);
        Assert.DoesNotContain("abcdef0123456789abcdef", findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_reports_the_json_path_of_every_finding_and_never_the_value()
    {
        var node = JsonNode.Parse("""
            {
              "nome": "ok",
              "usuario": { "email": "a@b.c", "senha": "S3nh@Secreta!" },
              "itens": [ { "refreshToken": "x" }, { "obs": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghijk" } ]
            }
            """);

        var findings = SensitiveData.Scan(node);

        Assert.Equal(["$.itens[0].refreshToken", "$.itens[1].obs", "$.usuario.senha"], findings.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(findings, f => f.Contains("S3nh@Secreta!", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_of_clean_or_absent_data_is_empty()
    {
        Assert.Empty(SensitiveData.Scan(null));
        Assert.Empty(SensitiveData.Scan(new JsonObject { ["nome"] = "Centro", ["quantidade"] = 3, ["ativo"] = true, ["nulo"] = null }));
    }

    [Fact]
    public void MaskText_replaces_secret_fragments_and_keeps_the_rest()
    {
        var masked = SensitiveData.MaskText("login falhou, enviou Bearer abcdef0123456789abcdef e o hash $2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy fim");

        Assert.DoesNotContain("abcdef0123456789abcdef", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy", masked, StringComparison.Ordinal);
        Assert.StartsWith("login falhou, enviou", masked, StringComparison.Ordinal);
        Assert.EndsWith("fim", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Action_names_follow_resource_colon_action()
    {
        Assert.True(AuditActionName.IsValid("sessao:trocar-unidade"));
        Assert.True(AuditActionName.IsValid("usuario:redefinir_acesso"));
        Assert.False(AuditActionName.IsValid("sessao"));
        Assert.False(AuditActionName.IsValid("Sessao:Trocar"));
        Assert.False(AuditActionName.IsValid(""));
        Assert.False(AuditActionName.IsValid(null));
        Assert.False(AuditActionName.IsValid("a:b:c"));
    }
}
