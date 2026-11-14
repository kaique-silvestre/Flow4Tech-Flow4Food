using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Cardapio.Application;

[AuditCatalog]
public static class OpcoesAuditActions
{
    public const string GrupoCriar = "grupo-opcao:criar";

    public const string GrupoAlterar = "grupo-opcao:alterar";

    public const string GruposDoProdutoDefinir = "produto:grupos-opcao-definir";
}

public sealed record GrupoDeOpcaoCriado(Guid EventId, Guid GrupoId, string Nome, int Minimo, int Maximo, int Opcoes) : IDomainEvent
{
    public const string Type = "cardapio.grupo-opcao-criado";

    public string EventType => Type;
}

public sealed record GrupoDeOpcaoAlterado(Guid EventId, Guid GrupoId, string Nome, int Minimo, int Maximo, bool Ativo, int Versao, int Opcoes) : IDomainEvent
{
    public const string Type = "cardapio.grupo-opcao-alterado";

    public string EventType => Type;
}

public sealed record GruposDoProdutoDefinidos(Guid EventId, Guid ProdutoId, IReadOnlyList<Guid> GrupoIds) : IDomainEvent
{
    public const string Type = "cardapio.produto-grupos-opcao-definidos";

    public string EventType => Type;
}

/// <summary>An option as written by the manager. <c>Id</c> null creates one; an existing id updates it; an existing option left out of the list is turned off (never deleted).</summary>
public sealed record OpcaoNova(Guid? Id, string? Nome, long PrecoCentavos, bool Ativa = true);

public sealed record NovoGrupoDeOpcao(string? Nome, int Minimo, int Maximo, bool Ativo, IReadOnlyList<OpcaoNova>? Opcoes);

public sealed record AlteracaoDeGrupoDeOpcao(string? Nome, int Minimo, int Maximo, bool Ativo, int Versao, IReadOnlyList<OpcaoNova>? Opcoes);

public sealed record ResultadoGrupo(ResultadoDoCardapio Resultado, GrupoDeOpcao? Grupo = null);

public sealed record ResultadoGruposDoProduto(ResultadoDoCardapio Resultado, IReadOnlyList<GrupoDeOpcao>? Grupos = null);

/// <summary>One option with how many times it was launched with this product in the unit (feeds "most used add-ons").</summary>
public sealed record OpcaoFrequente(Guid GrupoId, string GrupoNome, Opcao Opcao, long Lancamentos);

/// <summary>Port: option groups of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IOpcoesStore
{
    Task<IReadOnlyList<GrupoDeOpcao>> ListarGruposAsync(Guid organizationId, bool incluirInativos, CancellationToken cancellationToken);

    Task<GrupoDeOpcao?> AcharGrupoAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    /// <summary>The groups linked to a product, in the order of the link, with all their options (active or not).</summary>
    Task<IReadOnlyList<GrupoDeOpcao>> GruposDoProdutoAsync(Guid organizationId, Guid produtoId, CancellationToken cancellationToken);

    Task InserirGrupoAsync(Guid organizationId, GrupoDeOpcao grupo, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Optimistic update of the group and its options (upsert by id; options not listed are turned off). False when the stored version is not <paramref name="versaoEsperada"/>.</summary>
    Task<bool> AlterarGrupoAsync(Guid organizationId, GrupoDeOpcao novo, int versaoEsperada, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>How many of <paramref name="ids"/> exist as groups.</summary>
    Task<int> ContarGruposAsync(Guid organizationId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The group ids linked to every product that has any, in the order of the link (the import compares a sheet with them).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> VinculosDosProdutosAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Replaces the links of a product, keeping the order of <paramref name="grupoIds"/>.</summary>
    Task DefinirGruposDoProdutoAsync(Guid organizationId, Guid produtoId, IReadOnlyList<Guid> grupoIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<OpcaoFrequente>> FrequentesAsync(Guid organizationId, Guid unitId, Guid produtoId, int limite, CancellationToken cancellationToken);
}

public enum RecusaDeOpcoes
{
    Nenhuma,

    /// <summary>Unknown to the product, repeated, below the minimum or above the maximum of a group.</summary>
    Invalida,

    /// <summary>The option, or its group, is turned off.</summary>
    Inativa,
}

/// <summary>An option chosen for an item, with the name and price to freeze into it (taken from the menu on the server).</summary>
public sealed record OpcaoParaItem(Guid OpcaoId, Guid GrupoId, string Nome, long PrecoCentavos);

public sealed record EscolhaParaItem(RecusaDeOpcoes Recusa, IReadOnlyList<OpcaoParaItem> Opcoes);

/// <summary>What other modules use to check a launch against the option groups of a product (the rule itself lives in the menu's domain).</summary>
public static class OpcoesDoProduto
{
    /// <summary>Checks <paramref name="opcaoIds"/> against the groups linked to the product, including required groups when none was sent. Runs inside a unit of work.</summary>
    public static async Task<EscolhaParaItem> EscolherAsync(IOpcoesStore store, Guid organizationId, Guid produtoId, IReadOnlyList<Guid> opcaoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(opcaoIds);
        var grupos = await store.GruposDoProdutoAsync(organizationId, produtoId, cancellationToken);
        var escolha = RegrasDeOpcoes.Escolher(grupos, opcaoIds);
        return escolha.Recusa switch
        {
            RecusaDeEscolha.Nenhuma => new EscolhaParaItem(RecusaDeOpcoes.Nenhuma, escolha.Escolhidas.Select(e => new OpcaoParaItem(e.Opcao.Id, e.GrupoId, e.Opcao.Nome, e.Opcao.PrecoCentavos)).ToList()),
            RecusaDeEscolha.OpcaoInativa => new EscolhaParaItem(RecusaDeOpcoes.Inativa, []),
            _ => new EscolhaParaItem(RecusaDeOpcoes.Invalida, []),
        };
    }
}
