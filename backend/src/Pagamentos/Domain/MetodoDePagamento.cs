namespace Flow4Tech.Pagamentos.Domain;

public enum TipoDeMetodo
{
    /// <summary>Cash: the only method with change, and the only one that enters the expected cash of the shift.</summary>
    Dinheiro,

    /// <summary>Any other method: recorded without cash movement, never with change.</summary>
    Outro,
}

/// <summary>A payment method of an organization. <see cref="Chave"/> is the stable key of a factory method (null for one the organization created); the name is the organization's to change.</summary>
public sealed record MetodoDePagamento(Guid Id, string? Chave, string Nome, TipoDeMetodo Tipo, bool Ativo, int Ordem);

/// <summary>A factory method every organization starts with.</summary>
public sealed record MetodoDeFabrica(string Chave, string Nome, TipoDeMetodo Tipo, bool AtivoNoInicio, int Ordem);

/// <summary>Pure rules of the payment methods.</summary>
public static class RegrasDeMetodo
{
    public const int NomeMaximo = 40;

    public const string Personalizado = "personalizado";

    /// <summary>Factory methods. Voucher and credit account start inactive and stay so until phase 2B gives them their rules (they cannot be turned on before).</summary>
    public static IReadOnlyList<MetodoDeFabrica> Fabrica { get; } =
    [
        new("dinheiro", "Dinheiro", TipoDeMetodo.Dinheiro, true, 1),
        new("credito", "Crédito", TipoDeMetodo.Outro, true, 2),
        new("debito", "Débito", TipoDeMetodo.Outro, true, 3),
        new("pix", "Pix", TipoDeMetodo.Outro, true, 4),
        new("vale-refeicao", "Vale-refeição", TipoDeMetodo.Outro, true, 5),
        new("vale", "Vale", TipoDeMetodo.Outro, false, 6),
        new("conta-credito", "Conta crédito", TipoDeMetodo.Outro, false, 7),
    ];

    /// <summary>False for the factory methods that wait for phase 2B (voucher and credit account).</summary>
    public static bool LiberadoNestaFase(string? chave) => chave is not ("vale" or "conta-credito");

    /// <summary>The name trimmed, or null when empty or longer than <see cref="NomeMaximo"/> or with control characters.</summary>
    public static string? NomeValido(string? nome)
    {
        var limpo = nome?.Trim();
        return string.IsNullOrEmpty(limpo) || limpo.Length > NomeMaximo || limpo.Any(char.IsControl) ? null : limpo;
    }

    /// <summary>The key stored on a payment: the factory key, or <see cref="Personalizado"/> for a method the organization created.</summary>
    public static string ChaveDoPagamento(string? chave) => chave ?? Personalizado;
}
