namespace Flow4Tech.Clientes.Domain;

public enum EstadoDoCliente
{
    Ativo,

    /// <summary>"Forgotten" (LGPD): the personal data is gone, the id and the history that points to it stay.</summary>
    Anonimizado,
}

/// <summary>The LGPD consent of a customer: granted or not, when, for which purpose (a code) and which version of the text, who registered it, and when it was revoked (if it was).</summary>
public sealed record Consentimento(bool Concedido, DateTimeOffset? Quando, string? Finalidade, string? VersaoDoTexto, Guid? RegistradoPor, DateTimeOffset? RevogadoEm)
{
    /// <summary>Granted and not revoked: the only state in which the data may be used.</summary>
    public bool Vigente => Concedido && RevogadoEm is null;
}

/// <summary>A customer. Phone and document are digits only, the e-mail is lower case (normalized by <see cref="RegrasDeCliente"/>): the stored form is also the form compared for duplicates.</summary>
public sealed record Cliente(Guid Id, string Nome, string? Telefone, string? Email, string? Documento, Consentimento Consentimento, EstadoDoCliente Estado, DateTimeOffset CriadoEm, DateTimeOffset? AnonimizadoEm = null);

/// <summary>Pure rules of the customers: normalization, validation and the masks of the personal data. No I/O.</summary>
public static class RegrasDeCliente
{
    public const int NomeMaximo = 120;

    public const int EmailMaximo = 254;

    public const string NomeAnonimizado = "Cliente anonimizado";

    /// <summary>The name trimmed, or null when empty, too long or with control characters.</summary>
    public static string? NomeValido(string? nome)
    {
        var limpo = nome?.Trim();
        return string.IsNullOrEmpty(limpo) || limpo.Length > NomeMaximo || limpo.Any(char.IsControl) ? null : limpo;
    }

    /// <summary>Digits of a Brazilian phone (area code plus 8 or 9 digits; a leading 55 is dropped), or null when it is not one. Blank input is "absent", not invalid: use <see cref="Ausente"/> first.</summary>
    public static string? Telefone(string? texto)
    {
        var digitos = Digitos(texto);
        if (digitos.Length is 12 or 13 && digitos.StartsWith("55", StringComparison.Ordinal))
        {
            digitos = digitos[2..];
        }

        return digitos.Length is 10 or 11 ? digitos : null;
    }

    /// <summary>The e-mail in lower case, or null when it has no single '@' with text on both sides, spaces, or is too long. Not a full address validation: the contact is confirmed by use.</summary>
    public static string? Email(string? texto)
    {
        var limpo = texto is null ? null : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToLower(texto.Trim());
        if (string.IsNullOrEmpty(limpo) || limpo.Length > EmailMaximo || limpo.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        var arroba = limpo.IndexOf('@', StringComparison.Ordinal);
        return arroba > 0 && arroba == limpo.LastIndexOf('@') && arroba < limpo.Length - 1 && limpo[(arroba + 1)..].Contains('.', StringComparison.Ordinal) ? limpo : null;
    }

    /// <summary>The 11 (CPF) or 14 (CNPJ) digits of a document, or null otherwise. The check digits are not verified.</summary>
    public static string? Documento(string? texto)
    {
        var digitos = Digitos(texto);
        return digitos.Length is 11 or 14 ? digitos : null;
    }

    /// <summary>True for null, empty or blank text: the field was not filled.</summary>
    public static bool Ausente(string? texto) => string.IsNullOrWhiteSpace(texto);

    /// <summary>A purpose code: lower-case letters, digits and hyphens, 3 to 40 characters (a fixed alphabet, so it is safe to store and to show).</summary>
    public static bool FinalidadeValida(string? finalidade) => finalidade is { Length: >= 3 and <= 40 } && finalidade.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>A text version: letters, digits, dot, underscore and hyphen, 1 to 20 characters.</summary>
    public static bool VersaoValida(string? versao) => versao is { Length: >= 1 and <= 20 } && versao.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Phone and document: stars and the last two digits (<c>***-**12</c>).</summary>
    public static string MascararDigitos(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        return "***-**" + (valor.Length >= 2 ? valor[^2..] : valor);
    }

    /// <summary>E-mail: the first character, stars, and nothing of the domain (<c>j***@***</c>).</summary>
    public static string MascararEmail(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        return valor.Length == 0 ? "***@***" : valor[0] + "***@***";
    }

    private static string Digitos(string? texto) => new([.. (texto ?? "").Where(char.IsAsciiDigit)]);
}
