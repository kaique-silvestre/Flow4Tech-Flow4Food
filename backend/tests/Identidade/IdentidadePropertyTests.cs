using Flow4Tech.Identidade.Domain;
using FsCheck.Xunit;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Property tests (F4-0105) of the identity normalization and the password policy: they hold for every string, not only the samples.</summary>
public sealed class IdentidadePropertyTests
{
    [Property]
    public bool A_normalized_user_name_is_stable_lower_case_and_inside_the_format(string? raw)
    {
        if (!NomeDeUsuario.TryNormalize(raw, out var nome))
        {
            return nome.Length == 0;
        }

        return nome.Length is >= 3 and <= 32
            && nome.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            && NomeDeUsuario.TryNormalize(nome, out var denovo)
            && denovo == nome;
    }

    [Property]
    public bool Case_and_outer_spaces_never_change_the_user_name(string? raw)
    {
        if (raw is null || !NomeDeUsuario.TryNormalize(raw, out var base1))
        {
            return true;
        }

        return NomeDeUsuario.TryNormalize("  " + raw.ToUpperInvariant() + "\t", out var base2) && base2 == base1;
    }

    [Property]
    public bool A_normalized_email_is_stable_short_and_has_one_at_sign(string? raw)
    {
        if (!EmailDeConta.TryNormalize(raw, out var email))
        {
            return email.Length == 0;
        }

        return email.Length <= EmailDeConta.MaximoCaracteres
            && email.Count(c => c == '@') == 1
            && !email.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            && EmailDeConta.TryNormalize(email, out var denovo)
            && denovo == email;
    }

    [Property]
    public bool The_password_policy_never_throws_and_reports_by_length(string? senha, string? id)
    {
        var problemas = PoliticaDeSenha.Avaliar(senha, id);
        if (string.IsNullOrWhiteSpace(senha))
        {
            return problemas.Count == 1 && problemas[0] == ProblemaDeSenha.Vazia;
        }

        var length = PoliticaDeSenha.Normalizar(senha).Length;
        return problemas.Contains(ProblemaDeSenha.Curta) == (length < PoliticaDeSenha.MinimoCaracteres)
            && problemas.Contains(ProblemaDeSenha.Longa) == (length > PoliticaDeSenha.MaximoCaracteres)
            && !problemas.Contains(ProblemaDeSenha.Vazia);
    }

    [Property]
    public bool A_password_equal_to_an_identifier_is_always_refused(string? id)
    {
        // Spaces on the edge are part of a password (only the identifier is trimmed), so compare ids that have none.
        if (string.IsNullOrWhiteSpace(id) || id != id.Trim())
        {
            return true;
        }

        return PoliticaDeSenha.Avaliar(id, id).Contains(ProblemaDeSenha.IgualAoIdentificador);
    }

    [Property]
    public bool Normalizing_a_password_twice_changes_nothing(string? senha)
    {
        if (senha is null)
        {
            return true;
        }

        var once = PoliticaDeSenha.Normalizar(senha);
        return PoliticaDeSenha.Normalizar(once) == once;
    }
}
