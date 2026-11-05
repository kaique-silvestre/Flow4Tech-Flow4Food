namespace Flow4Tech.Cardapio.Domain;

public sealed record Categoria(Guid Id, string Nome, bool Ativa, int Versao);

/// <summary>A sellable item. Price is integer cents in <see cref="Moeda"/> (BRL only for now); <see cref="Versao"/> grows on every change.</summary>
public sealed record Produto(Guid Id, Guid CategoriaId, string Codigo, string Nome, long PrecoCentavos, string Moeda, bool Ativo, int Versao);

/// <summary>Pure input rules of the menu. Return null when the input is not acceptable; the caller never stores what these refuse.</summary>
public static class RegrasDoCardapio
{
    public const int NomeMaximo = 120;

    public const int CategoriaNomeMaximo = 80;

    public const int CodigoMaximo = 20;

    /// <summary>Upper limit of a price: R$ 1.000.000,00 (a typo guard, not a business rule).</summary>
    public const long PrecoMaximoCentavos = 100_000_000;

    public const string MoedaPadrao = "BRL";

    public static string? NomeDeProduto(string? nome) => Texto(nome, NomeMaximo);

    public static string? NomeDeCategoria(string? nome) => Texto(nome, CategoriaNomeMaximo);

    /// <summary>Short code: 1 to 20 of A-Z, 0-9 and hyphen, not starting with a hyphen; stored upper case.</summary>
    public static string? Codigo(string? codigo)
    {
        var text = codigo?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(text) || text.Length > CodigoMaximo || text[0] == '-')
        {
            return null;
        }

        return text.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-') ? text : null;
    }

    public static bool PrecoValido(long centavos) => centavos is >= 0 and <= PrecoMaximoCentavos;

    private static string? Texto(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > max || text.Any(char.IsControl) ? null : text;
    }
}
