using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Cardapio.Application;

[PermissionCatalog]
public static class CardapioPermissions
{
    /// <summary>List, search and see the frequent items of the menu (what a waiter needs to launch).</summary>
    public const string Ver = "produto:ver";

    /// <summary>Create and change products and categories, and see inactive ones.</summary>
    public const string Gerenciar = "produto:gerenciar";

    /// <summary>Change a price (base, promotional or per unit) of an existing product. Limited: the engine decides with the percent the price moves, computed on the server.</summary>
    public const string AlterarPreco = "cardapio:preco-alterar";
}

[AuditCatalog]
public static class CardapioAuditActions
{
    public const string ProdutoCriar = "produto:criar";

    public const string ProdutoAlterar = "produto:alterar";

    public const string CategoriaCriar = "categoria:criar";

    public const string CategoriaAlterar = "categoria:alterar";

    public const string PrecoDaUnidadeDefinir = "produto:preco-unidade-definir";

    public const string PrecoDaUnidadeRemover = "produto:preco-unidade-remover";
}

public sealed record ProdutoCriado(Guid EventId, Guid ProdutoId, string Codigo, long PrecoCentavos, int Versao) : IDomainEvent
{
    public const string Type = "cardapio.produto-criado";

    public string EventType => Type;
}

public sealed record ProdutoAlterado(Guid EventId, Guid ProdutoId, string Codigo, long PrecoCentavos, bool Ativo, int Versao, bool Indisponivel = false) : IDomainEvent
{
    public const string Type = "cardapio.produto-alterado";

    public string EventType => Type;
}

public sealed record CategoriaCriada(Guid EventId, Guid CategoriaId, string Nome, Guid? CategoriaPaiId = null) : IDomainEvent
{
    public const string Type = "cardapio.categoria-criada";

    public string EventType => Type;
}

public sealed record CategoriaAlterada(Guid EventId, Guid CategoriaId, string Nome, bool Ativa, int Versao, Guid? CategoriaPaiId = null) : IDomainEvent
{
    public const string Type = "cardapio.categoria-alterada";

    public string EventType => Type;
}

public sealed record PrecoDaUnidadeDefinido(Guid EventId, Guid ProdutoId, Guid UnidadeId, long PrecoCentavos) : IDomainEvent
{
    public const string Type = "cardapio.preco-unidade-definido";

    public string EventType => Type;
}

public sealed record PrecoDaUnidadeRemovido(Guid EventId, Guid ProdutoId, Guid UnidadeId) : IDomainEvent
{
    public const string Type = "cardapio.preco-unidade-removido";

    public string EventType => Type;
}

/// <summary>
/// Port: asks the one authorization engine whether the caller may move a price by this much (<see cref="CardapioPermissions.AlterarPreco"/>, a permission with a percent limit).
/// <paramref name="percentual"/> and <paramref name="deltaCentavos"/> are computed on the server from the stored price, never taken from the request. Runs inside the unit of work.
/// </summary>
public interface IDecisaoDePreco
{
    Task<bool> PodeAlterarAsync(IRequestContext request, decimal percentual, long deltaCentavos, CancellationToken cancellationToken);
}

/// <summary>Port: the menu rows of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface ICardapioStore
{
    Task<IReadOnlyList<Categoria>> ListarCategoriasAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<Categoria?> AcharCategoriaAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    Task InserirCategoriaAsync(Guid organizationId, Categoria categoria, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Optimistic update: false when the stored version is not <paramref name="versaoEsperada"/>.</summary>
    Task<bool> AlterarCategoriaAsync(Guid organizationId, Categoria nova, int versaoEsperada, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> TemSubcategoriasAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    Task<bool> NomeEmUsoAsync(Guid organizationId, string nome, Guid? exceto, CancellationToken cancellationToken);

    Task<IReadOnlyList<Produto>> ListarProdutosAsync(Guid organizationId, Guid unitId, bool incluirInativos, CancellationToken cancellationToken);

    /// <summary>
    /// Active products whose normalized name contains <paramref name="termoNormalizado"/> or whose short code is <paramref name="codigo"/>: exact code first, then names that start with the text,
    /// then the rest, each by name; at most <paramref name="limite"/>, one query (unit prices included).
    /// </summary>
    Task<IReadOnlyList<Produto>> BuscarProdutosAsync(Guid organizationId, Guid unitId, string termoNormalizado, string codigo, int limite, CancellationToken cancellationToken);

    /// <summary>The products the waiter launched most in the unit (active ones only): most launched first, then by name and id.</summary>
    Task<IReadOnlyList<Produto>> FavoritosAsync(Guid organizationId, Guid unitId, Guid garcomId, int limite, CancellationToken cancellationToken);

    Task<IReadOnlyList<Produto>> FrequentesAsync(Guid organizationId, Guid unitId, int limite, CancellationToken cancellationToken);

    Task<Produto?> AcharProdutoAsync(Guid organizationId, Guid unitId, Guid id, CancellationToken cancellationToken);

    Task<bool> CodigoEmUsoAsync(Guid organizationId, string codigo, Guid? exceto, CancellationToken cancellationToken);

    Task InserirProdutoAsync(Guid organizationId, Produto produto, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Optimistic update: false when the stored version is not <paramref name="versaoEsperada"/>.</summary>
    Task<bool> AlterarProdutoAsync(Guid organizationId, Produto novo, int versaoEsperada, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Sets (insert or replace) the price of a product in one unit.</summary>
    Task DefinirPrecoDaUnidadeAsync(Guid organizationId, Guid unitId, Guid produtoId, long precoCentavos, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Removes the unit override; false when there was none.</summary>
    Task<bool> RemoverPrecoDaUnidadeAsync(Guid organizationId, Guid unitId, Guid produtoId, CancellationToken cancellationToken);
}

/// <summary>Port: domain events of the menu, written to the outbox on the unit of work in progress.</summary>
public interface IEventosDoCardapio
{
    Task PublicarAsync(Flow4Tech.Kernel.OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken);
}
