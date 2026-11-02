using System.Text;

namespace Flow4Tech.Identidade.Domain;

public enum ProblemaDeSenha
{
    Vazia,
    Curta,
    Longa,
    IgualAoIdentificador,
    IgualASenhaAtual,
}

/// <summary>
/// Password policy (spec 5.2.2, S6): a minimum length, NO rules about symbols or capitals, no absurd maximum. The maximum only bounds the work of the
/// hash. Text is Unicode-normalized (NFKC) before it is measured, hashed or compared, so the same characters typed on two keyboards are the same password.
/// The leaked-password check is a separate port (it needs the network or a list).
/// </summary>
public static class PoliticaDeSenha
{
    public const int MinimoCaracteres = 10;

    public const int MaximoCaracteres = 256;

    public static string Normalizar(string senha)
    {
        ArgumentNullException.ThrowIfNull(senha);
        return senha.Normalize(NormalizationForm.FormKC);
    }

    public static IReadOnlyList<ProblemaDeSenha> Avaliar(string? senha, params string?[] identificadores)
    {
        if (string.IsNullOrWhiteSpace(senha))
        {
            return [ProblemaDeSenha.Vazia];
        }

        var normalizada = Normalizar(senha);
        var problemas = new List<ProblemaDeSenha>();
        if (normalizada.Length < MinimoCaracteres)
        {
            problemas.Add(ProblemaDeSenha.Curta);
        }

        if (normalizada.Length > MaximoCaracteres)
        {
            problemas.Add(ProblemaDeSenha.Longa);
        }

        if (identificadores.Any(i => !string.IsNullOrWhiteSpace(i) && string.Equals(Normalizar(i.Trim()), normalizada, StringComparison.OrdinalIgnoreCase)))
        {
            problemas.Add(ProblemaDeSenha.IgualAoIdentificador);
        }

        return problemas;
    }
}
