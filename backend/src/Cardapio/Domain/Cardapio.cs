namespace Flow4Tech.Cardapio.Domain;

/// <summary>A menu category; <paramref name="CategoriaPaiId"/> is null at the top level (the tree has at most two levels).</summary>
public sealed record Categoria(Guid Id, string Nome, bool Ativa, int Versao, Guid? CategoriaPaiId = null);

/// <summary>A promotional price valid from <paramref name="Inicio"/> (inclusive) to <paramref name="Fim"/> (exclusive), both UTC.</summary>
public sealed record Promocao(long PrecoCentavos, DateTimeOffset Inicio, DateTimeOffset Fim);

/// <summary>
/// A sellable item. Price is integer cents in <see cref="Moeda"/> (BRL only for now); <see cref="Versao"/> grows on every change. <see cref="Indisponivel"/> is the manual "acabou".
/// <see cref="PrecoDaUnidadeCentavos"/> is the override of the unit being read (null when it has none); it is not part of the product row.
/// </summary>
public sealed record Produto(
    Guid Id,
    Guid CategoriaId,
    string Codigo,
    string Nome,
    long PrecoCentavos,
    string Moeda,
    bool Ativo,
    int Versao,
    bool Indisponivel = false,
    Promocao? Promocao = null,
    long? PrecoDaUnidadeCentavos = null)
{
    /// <summary>Can be launched: on the menu (active) and not marked "acabou".</summary>
    public bool Lancavel => Ativo && !Indisponivel;

    /// <summary>The price to charge at <paramref name="agora"/> in the unit this product was read for.</summary>
    public long PrecoEfetivo(DateTimeOffset agora) => RegrasDePreco.Efetivo(PrecoCentavos, Promocao, PrecoDaUnidadeCentavos, agora);
}

/// <summary>Pure price rules. The effective price is: unit override, else promotional price while valid, else base price.</summary>
public static class RegrasDePreco
{
    public static long Efetivo(long baseCentavos, Promocao? promocao, long? unidadeCentavos, DateTimeOffset agora)
    {
        if (unidadeCentavos is { } unidade)
        {
            return unidade;
        }

        return promocao is not null && promocao.Inicio <= agora && agora < promocao.Fim ? promocao.PrecoCentavos : baseCentavos;
    }

    /// <summary>A promotion is acceptable when its price is valid, strictly below the base price and its window is non-empty.</summary>
    public static bool PromocaoValida(Promocao? promocao, long baseCentavos) =>
        promocao is null || (RegrasDoCardapio.PrecoValido(promocao.PrecoCentavos) && promocao.PrecoCentavos < baseCentavos && promocao.Fim > promocao.Inicio);

    /// <summary>
    /// How far a price moved, in percent of what it was (an absent previous value is compared with <paramref name="referenciaCentavos"/>, the base price). Moving away from zero counts as 100.
    /// Removing a price counts as the move back to the reference. Used to ask the engine for the percent limit of <c>cardapio:preco-alterar</c>.
    /// </summary>
    public static decimal PercentualDaMudanca(long? antesCentavos, long? depoisCentavos, long referenciaCentavos)
    {
        var de = antesCentavos ?? referenciaCentavos;
        var para = depoisCentavos ?? referenciaCentavos;
        if (de == para)
        {
            return 0m;
        }

        return de == 0 ? 100m : Math.Abs(para - de) * 100m / de;
    }
}

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

/// <summary>An option of a group: an add-on with a price, or a removal at price zero. Never deleted (frozen copies live in launched items); turned off with <see cref="Ativa"/>.</summary>
public sealed record Opcao(Guid Id, string Nome, long PrecoCentavos, bool Ativa);

/// <summary>
/// A group of options linked to products. The customer picks between <see cref="Minimo"/> and <see cref="Maximo"/> options of it; <see cref="Obrigatorio"/> when at least one is required.
/// </summary>
public sealed record GrupoDeOpcao(Guid Id, string Nome, int Minimo, int Maximo, bool Ativo, int Versao, IReadOnlyList<Opcao> Opcoes)
{
    public bool Obrigatorio => Minimo >= 1;
}

/// <summary>An option picked in a launch, with the group it belongs to.</summary>
public sealed record OpcaoEscolhida(Guid GrupoId, Opcao Opcao);

public enum RecusaDeEscolha
{
    Nenhuma,

    /// <summary>The id is not an option of any group linked to the product.</summary>
    OpcaoDesconhecida,

    /// <summary>The option, or its group, is turned off.</summary>
    OpcaoInativa,

    OpcaoRepetida,

    AbaixoDoMinimo,

    AcimaDoMaximo,
}

public sealed record EscolhaDeOpcoes(RecusaDeEscolha Recusa, IReadOnlyList<OpcaoEscolhida> Escolhidas);

/// <summary>Pure rules of option groups. The caller never stores or charges what these refuse.</summary>
public static class RegrasDeOpcoes
{
    public const int NomeMaximo = 80;

    public const int MaximoDeEscolhas = 50;

    public const int OpcoesMaximasPorGrupo = 50;

    public static string? NomeDoGrupo(string? nome) => Texto(nome);

    public static string? NomeDaOpcao(string? nome) => Texto(nome);

    /// <summary>0 &lt;= min &lt;= max, and max between 1 and <see cref="MaximoDeEscolhas"/>.</summary>
    public static bool LimitesValidos(int minimo, int maximo) => minimo >= 0 && maximo >= 1 && maximo <= MaximoDeEscolhas && minimo <= maximo;

    /// <summary>
    /// Checks the chosen option ids against the groups of the product. An inactive group requires nothing and its options are refused; a group's minimum never exceeds the options still active in it
    /// (an option marked off must not block the service). Chosen options come back in the order they were sent.
    /// </summary>
    public static EscolhaDeOpcoes Escolher(IReadOnlyList<GrupoDeOpcao> gruposDoProduto, IReadOnlyList<Guid> escolhidas)
    {
        ArgumentNullException.ThrowIfNull(gruposDoProduto);
        ArgumentNullException.ThrowIfNull(escolhidas);
        var porOpcao = new Dictionary<Guid, (GrupoDeOpcao Grupo, Opcao Opcao)>();
        foreach (var grupo in gruposDoProduto)
        {
            foreach (var opcao in grupo.Opcoes)
            {
                porOpcao[opcao.Id] = (grupo, opcao);
            }
        }

        var lista = new List<OpcaoEscolhida>(escolhidas.Count);
        var vistas = new HashSet<Guid>();
        foreach (var id in escolhidas)
        {
            if (!porOpcao.TryGetValue(id, out var achada))
            {
                return Recusa(RecusaDeEscolha.OpcaoDesconhecida);
            }

            if (!achada.Grupo.Ativo || !achada.Opcao.Ativa)
            {
                return Recusa(RecusaDeEscolha.OpcaoInativa);
            }

            if (!vistas.Add(id))
            {
                return Recusa(RecusaDeEscolha.OpcaoRepetida);
            }

            lista.Add(new OpcaoEscolhida(achada.Grupo.Id, achada.Opcao));
        }

        foreach (var grupo in gruposDoProduto.Where(g => g.Ativo))
        {
            var escolhidasDoGrupo = lista.Count(e => e.GrupoId == grupo.Id);
            if (escolhidasDoGrupo > grupo.Maximo)
            {
                return Recusa(RecusaDeEscolha.AcimaDoMaximo);
            }

            if (escolhidasDoGrupo < Math.Min(grupo.Minimo, grupo.Opcoes.Count(o => o.Ativa)))
            {
                return Recusa(RecusaDeEscolha.AbaixoDoMinimo);
            }
        }

        return new EscolhaDeOpcoes(RecusaDeEscolha.Nenhuma, lista);
    }

    private static EscolhaDeOpcoes Recusa(RecusaDeEscolha recusa) => new(recusa, []);

    private static string? Texto(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > NomeMaximo || text.Any(char.IsControl) ? null : text;
    }
}
