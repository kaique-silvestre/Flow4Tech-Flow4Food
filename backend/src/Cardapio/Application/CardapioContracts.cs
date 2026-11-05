using Flow4Tech.Cardapio.Domain;
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
}

[AuditCatalog]
public static class CardapioAuditActions
{
    public const string ProdutoCriar = "produto:criar";

    public const string ProdutoAlterar = "produto:alterar";

    public const string CategoriaCriar = "categoria:criar";

    public const string CategoriaAlterar = "categoria:alterar";
}

public sealed record ProdutoCriado(Guid EventId, Guid ProdutoId, string Codigo, long PrecoCentavos, int Versao) : IDomainEvent
{
    public const string Type = "cardapio.produto-criado";

    public string EventType => Type;
}

public sealed record ProdutoAlterado(Guid EventId, Guid ProdutoId, string Codigo, long PrecoCentavos, bool Ativo, int Versao) : IDomainEvent
{
    public const string Type = "cardapio.produto-alterado";

    public string EventType => Type;
}

public sealed record CategoriaCriada(Guid EventId, Guid CategoriaId, string Nome) : IDomainEvent
{
    public const string Type = "cardapio.categoria-criada";

    public string EventType => Type;
}

public sealed record CategoriaAlterada(Guid EventId, Guid CategoriaId, string Nome, bool Ativa, int Versao) : IDomainEvent
{
    public const string Type = "cardapio.categoria-alterada";

    public string EventType => Type;
}

/// <summary>Port: the menu rows of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface ICardapioStore
{
    Task<IReadOnlyList<Categoria>> ListarCategoriasAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<Categoria?> AcharCategoriaAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    Task InserirCategoriaAsync(Guid organizationId, Categoria categoria, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Optimistic update: false when the stored version is not <paramref name="versaoEsperada"/>.</summary>
    Task<bool> AlterarCategoriaAsync(Guid organizationId, Categoria nova, int versaoEsperada, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> NomeEmUsoAsync(Guid organizationId, string nome, Guid? exceto, CancellationToken cancellationToken);

    Task<IReadOnlyList<Produto>> ListarProdutosAsync(Guid organizationId, string? busca, bool incluirInativos, CancellationToken cancellationToken);

    Task<IReadOnlyList<Produto>> FrequentesAsync(Guid organizationId, Guid unitId, int limite, CancellationToken cancellationToken);

    Task<Produto?> AcharProdutoAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    Task<bool> CodigoEmUsoAsync(Guid organizationId, string codigo, Guid? exceto, CancellationToken cancellationToken);

    Task InserirProdutoAsync(Guid organizationId, Produto produto, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Optimistic update: false when the stored version is not <paramref name="versaoEsperada"/>.</summary>
    Task<bool> AlterarProdutoAsync(Guid organizationId, Produto novo, int versaoEsperada, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Port: domain events of the menu, written to the outbox on the unit of work in progress.</summary>
public interface IEventosDoCardapio
{
    Task PublicarAsync(Flow4Tech.Kernel.OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken);
}
