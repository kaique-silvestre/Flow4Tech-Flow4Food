using Flow4Tech.Identidade.Domain;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.1/5.2.2: user name, e-mail and password rules, pure.</summary>
public sealed class DomainRulesTests
{
    [Theory]
    [InlineData("  Maria.Souza ", "maria.souza")]
    [InlineData("garcom_01", "garcom_01")]
    [InlineData("ＭＡＲＩＡ", "maria")] // full-width letters fold to the same name (NFKC)
    public void An_operational_user_name_is_trimmed_folded_and_lower_cased(string raw, string expected)
    {
        Assert.True(NomeDeUsuario.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a b c")]
    [InlineData("-comeca-com-hifen")]
    [InlineData("joão")]
    [InlineData("maria@bar.com")]
    [InlineData("012345678901234567890123456789012")] // 33
    public void An_invalid_user_name_is_refused(string? raw)
    {
        Assert.False(NomeDeUsuario.TryNormalize(raw, out _));
    }

    [Theory]
    [InlineData(" Ana@Exemplo.COM ", "ana@exemplo.com")]
    [InlineData("dono+bar@exemplo.com.br", "dono+bar@exemplo.com.br")]
    public void An_email_is_trimmed_and_lower_cased(string raw, string expected)
    {
        Assert.True(EmailDeConta.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ana")]
    [InlineData("ana@")]
    [InlineData("@exemplo.com")]
    [InlineData("ana@exemplo")]
    [InlineData("a na@exemplo.com")]
    [InlineData("a@b@exemplo.com")]
    [InlineData("a\0@exemplo.com")]
    [InlineData("a\u0007@exemplo.com")]
    public void An_invalid_email_is_refused(string? raw)
    {
        Assert.False(EmailDeConta.TryNormalize(raw, out _));
    }

    [Fact]
    public void An_overlong_email_is_refused()
    {
        Assert.False(EmailDeConta.TryNormalize(new string('a', 250) + "@x.com", out _));
    }

    [Fact]
    public void A_password_needs_ten_characters_and_no_symbols()
    {
        Assert.Equal([ProblemaDeSenha.Curta], PoliticaDeSenha.Avaliar("123456789"));
        Assert.Empty(PoliticaDeSenha.Avaliar("abcdefghij")); // no symbol, digit or capital required
        Assert.Empty(PoliticaDeSenha.Avaliar("uma frase longa com espacos"));
    }

    [Fact]
    public void There_is_no_absurdly_low_maximum()
    {
        Assert.Empty(PoliticaDeSenha.Avaliar(new string('a', 128)));
        Assert.Empty(PoliticaDeSenha.Avaliar(new string('a', PoliticaDeSenha.MaximoCaracteres)));
        Assert.Equal([ProblemaDeSenha.Longa], PoliticaDeSenha.Avaliar(new string('a', PoliticaDeSenha.MaximoCaracteres + 1)));
        Assert.True(PoliticaDeSenha.MaximoCaracteres >= 128);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("          ")]
    public void A_blank_password_is_empty(string? senha)
    {
        Assert.Equal([ProblemaDeSenha.Vazia], PoliticaDeSenha.Avaliar(senha));
    }

    [Fact]
    public void A_password_equal_to_the_account_identifier_is_refused_ignoring_case()
    {
        Assert.Equal([ProblemaDeSenha.IgualAoIdentificador], PoliticaDeSenha.Avaliar("Maria.Souza.01", "maria.souza.01"));
        Assert.Empty(PoliticaDeSenha.Avaliar("outra-senha-longa", "maria.souza.01", null));
    }

    [Fact]
    public void Normalization_makes_composed_and_decomposed_text_the_same_password()
    {
        var composed = "Sénha-forte-123";
        var decomposed = "Sénha-forte-123";
        Assert.NotEqual(composed, decomposed);
        Assert.Equal(PoliticaDeSenha.Normalizar(composed), PoliticaDeSenha.Normalizar(decomposed));
        Assert.Equal("fi", PoliticaDeSenha.Normalizar("ﬁ")); // compatibility ligature
    }
}
