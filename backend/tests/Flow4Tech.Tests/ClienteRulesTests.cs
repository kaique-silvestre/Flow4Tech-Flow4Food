using Flow4Tech.Clientes.Application;
using Flow4Tech.Clientes.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0147: the pure rules of the customers (normalization, validation, masks), no database.</summary>
public sealed class ClienteRulesTests
{
    [Theory]
    [InlineData("(11) 98765-4321", "11987654321")]
    [InlineData("11 3456-7890", "1134567890")]
    [InlineData("+55 11 98765-4321", "11987654321")]
    [InlineData("5511987654321", "11987654321")]
    public void A_phone_is_stored_as_its_digits_without_the_country_code(string texto, string esperado) => Assert.Equal(esperado, RegrasDeCliente.Telefone(texto));

    [Theory]
    [InlineData("12345")]
    [InlineData("123456789")]
    [InlineData("119876543210")]
    [InlineData("telefone")]
    [InlineData("")]
    public void A_text_that_is_not_a_brazilian_phone_is_refused(string texto) => Assert.Null(RegrasDeCliente.Telefone(texto));

    [Theory]
    [InlineData("  Maria@Exemplo.COM ", "maria@exemplo.com")]
    [InlineData("a@b.co", "a@b.co")]
    public void An_email_is_trimmed_and_lower_cased(string texto, string esperado) => Assert.Equal(esperado, RegrasDeCliente.Email(texto));

    [Theory]
    [InlineData("sem-arroba")]
    [InlineData("@sem-usuario.com")]
    [InlineData("sem-dominio@")]
    [InlineData("dois@@x.com")]
    [InlineData("a@b")]
    [InlineData("com espaco@x.com")]
    [InlineData("")]
    public void A_malformed_email_is_refused(string texto) => Assert.Null(RegrasDeCliente.Email(texto));

    [Fact]
    public void An_email_over_254_characters_is_refused() => Assert.Null(RegrasDeCliente.Email(new string('a', 250) + "@b.com"));

    [Theory]
    [InlineData("123.456.789-01", "12345678901")]
    [InlineData("12.345.678/0001-90", "12345678000190")]
    public void A_document_is_stored_as_its_11_or_14_digits(string texto, string esperado) => Assert.Equal(esperado, RegrasDeCliente.Documento(texto));

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890")]
    [InlineData("123456789012")]
    public void A_document_with_another_number_of_digits_is_refused(string texto) => Assert.Null(RegrasDeCliente.Documento(texto));

    [Fact]
    public void A_name_is_trimmed_and_refused_when_empty_too_long_or_with_control_characters()
    {
        Assert.Equal("Maria Souza", RegrasDeCliente.NomeValido("  Maria Souza "));
        Assert.Null(RegrasDeCliente.NomeValido("   "));
        Assert.Null(RegrasDeCliente.NomeValido(null));
        Assert.Null(RegrasDeCliente.NomeValido(new string('x', 121)));
        Assert.Equal(new string('x', 120), RegrasDeCliente.NomeValido(new string('x', 120)));
        Assert.Null(RegrasDeCliente.NomeValido("Maria\u0007"));
    }

    [Theory]
    [InlineData("relacionamento", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("Relacionamento", false)]
    [InlineData("com espaco", false)]
    [InlineData("eyJhbGciOi.payload.assinatura", false)]
    [InlineData(null, false)]
    public void A_purpose_is_a_short_lower_case_code(string? finalidade, bool valida) => Assert.Equal(valida, RegrasDeCliente.FinalidadeValida(finalidade));

    [Theory]
    [InlineData("v1", true)]
    [InlineData("2026.11-a_1", true)]
    [InlineData("", false)]
    [InlineData("versao com espaco", false)]
    [InlineData("012345678901234567890", false)]
    [InlineData(null, false)]
    public void A_text_version_is_a_short_token(string? versao, bool valida) => Assert.Equal(valida, RegrasDeCliente.VersaoValida(versao));

    [Fact]
    public void Phone_and_document_masks_keep_only_the_last_two_digits_and_the_email_only_its_first_letter()
    {
        Assert.Equal("***-**21", RegrasDeCliente.MascararDigitos("11987654321"));
        Assert.Equal("***-**90", RegrasDeCliente.MascararDigitos("12345678000190"));
        Assert.Equal("m***@***", RegrasDeCliente.MascararEmail("maria@exemplo.com"));
    }

    [Fact]
    public void A_customer_shown_without_the_permission_has_its_contacts_masked_but_not_its_name_and_with_it_nothing_is_masked()
    {
        var cliente = new Cliente(Guid.NewGuid(), "Maria Souza", "11987654321", "maria@exemplo.com", "12345678901", new Consentimento(true, DateTimeOffset.UnixEpoch, "relacionamento", "v1", Guid.NewGuid(), null), EstadoDoCliente.Ativo, DateTimeOffset.UnixEpoch);

        var mascarado = ClienteExibido.De(cliente, verDados: false);
        Assert.Equal(("Maria Souza", "***-**21", "m***@***", "***-**01", true), (mascarado.Nome, mascarado.Telefone, mascarado.Email, mascarado.Documento, mascarado.DadosMascarados));

        var aberto = ClienteExibido.De(cliente, verDados: true);
        Assert.Equal(("11987654321", "maria@exemplo.com", "12345678901", false), (aberto.Telefone, aberto.Email, aberto.Documento, aberto.DadosMascarados));

        var semContatos = ClienteExibido.De(cliente with { Telefone = null, Email = null, Documento = null }, verDados: false);
        Assert.Equal((null, null, null), (semContatos.Telefone, semContatos.Email, semContatos.Documento));
    }

    [Fact]
    public void The_consent_is_in_force_only_when_granted_and_not_revoked()
    {
        Assert.True(new Consentimento(true, DateTimeOffset.UnixEpoch, "x", "v1", null, null).Vigente);
        Assert.False(new Consentimento(true, DateTimeOffset.UnixEpoch, "x", "v1", null, DateTimeOffset.UnixEpoch).Vigente);
        Assert.False(new Consentimento(false, null, null, null, null, null).Vigente);
    }
}
