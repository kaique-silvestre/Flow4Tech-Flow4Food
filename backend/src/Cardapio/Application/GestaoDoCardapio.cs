using System.Text.Json.Nodes;
using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Cardapio.Application;

public enum ResultadoDoCardapio
{
    Ok,

    /// <summary>Unknown id, or one of another organization (RLS makes both the same).</summary>
    NaoEncontrado,

    Invalido,

    /// <summary>The short code or the category name is already taken in the organization.</summary>
    EmUso,

    /// <summary>The stored version is not the one the editor saw.</summary>
    VersaoDesatualizada,

    CategoriaInexistente,
}

public sealed record ResultadoProduto(ResultadoDoCardapio Resultado, Produto? Produto = null);

public sealed record ResultadoCategoria(ResultadoDoCardapio Resultado, Categoria? Categoria = null);

public sealed record NovoProduto(string? Codigo, string? Nome, long PrecoCentavos, Guid CategoriaId, bool Ativo);

public sealed record AlteracaoDeProduto(string? Codigo, string? Nome, long PrecoCentavos, Guid CategoriaId, bool Ativo, int Versao);

/// <summary>
/// Reading and changing the menu. Who may call is the route's job (<c>produto:ver</c>, <c>produto:gerenciar</c>, decided by the one engine). Each change is ONE unit of work:
/// state change, audit record and outbox event commit or roll back together. A price change never touches a comanda already launched: items freeze name and price when launched.
/// </summary>
public sealed class GestaoDoCardapio(IUnitOfWork unitOfWork, ICardapioStore store, IAuditLog audit, IEventosDoCardapio eventos, IClock clock)
{
    public const int LimiteDeFrequentes = 12;

    public Task<IReadOnlyList<Categoria>> ListarCategoriasAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.ListarCategoriasAsync(request.Organization.OrganizationId, ct), cancellationToken);
    }

    /// <summary>Active products, by name; <paramref name="busca"/> keeps those whose name contains it or whose code contains it (case insensitive). <paramref name="incluirInativos"/> is for the manager.</summary>
    public Task<IReadOnlyList<Produto>> ListarProdutosAsync(IRequestContext request, string? busca, bool incluirInativos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var termo = string.IsNullOrWhiteSpace(busca) ? null : busca.Trim();
        return unitOfWork.ExecuteAsync(request, ct => store.ListarProdutosAsync(request.Organization.OrganizationId, termo, incluirInativos, ct), cancellationToken);
    }

    /// <summary>The active unit's most launched active products, most launched first. Empty when there is no history.</summary>
    public Task<IReadOnlyList<Produto>> FrequentesAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.FrequentesAsync(request.Organization.OrganizationId, request.Organization.UnitId, LimiteDeFrequentes, ct), cancellationToken);
    }

    public Task<ResultadoCategoria> CriarCategoriaAsync(IRequestContext request, string? nome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                if (RegrasDoCardapio.NomeDeCategoria(nome) is not { } limpo)
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.Invalido);
                }

                var categoria = new Categoria(EntityId.New().Value, limpo, true, 1);
                if (await store.NomeEmUsoAsync(org, limpo, null, ct))
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.EmUso);
                }

                await store.InserirCategoriaAsync(org, categoria, clock.UtcNow, ct);

                await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.CategoriaCriar, ObjectType = "categoria", ObjectId = categoria.Id.ToString("D"), After = Snapshot(categoria) }, ct);
                await eventos.PublicarAsync(request.Organization, new CategoriaCriada(EntityId.New().Value, categoria.Id, categoria.Nome), ct);
                return new ResultadoCategoria(ResultadoDoCardapio.Ok, categoria);
            },
            cancellationToken);
    }

    public Task<ResultadoCategoria> AlterarCategoriaAsync(IRequestContext request, Guid id, string? nome, bool ativa, int versao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                if (RegrasDoCardapio.NomeDeCategoria(nome) is not { } limpo)
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.Invalido);
                }

                if (await store.AcharCategoriaAsync(org, id, ct) is not { } antes)
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.NaoEncontrado);
                }

                if (antes.Versao != versao)
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.VersaoDesatualizada);
                }

                if (await store.NomeEmUsoAsync(org, limpo, id, ct))
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.EmUso);
                }

                var nova = antes with { Nome = limpo, Ativa = ativa, Versao = antes.Versao + 1 };
                if (!await store.AlterarCategoriaAsync(org, nova, versao, clock.UtcNow, ct))
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.VersaoDesatualizada);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.CategoriaAlterar, ObjectType = "categoria", ObjectId = id.ToString("D"), Before = Snapshot(antes), After = Snapshot(nova) }, ct);
                await eventos.PublicarAsync(request.Organization, new CategoriaAlterada(EntityId.New().Value, id, nova.Nome, nova.Ativa, nova.Versao), ct);
                return new ResultadoCategoria(ResultadoDoCardapio.Ok, nova);
            },
            cancellationToken);
    }

    public Task<ResultadoProduto> CriarProdutoAsync(IRequestContext request, NovoProduto comando, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(comando);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                if (RegrasDoCardapio.Codigo(comando.Codigo) is not { } codigo || RegrasDoCardapio.NomeDeProduto(comando.Nome) is not { } nome || !RegrasDoCardapio.PrecoValido(comando.PrecoCentavos))
                {
                    return new ResultadoProduto(ResultadoDoCardapio.Invalido);
                }

                if (await store.AcharCategoriaAsync(org, comando.CategoriaId, ct) is null)
                {
                    return new ResultadoProduto(ResultadoDoCardapio.CategoriaInexistente);
                }

                if (await store.CodigoEmUsoAsync(org, codigo, null, ct))
                {
                    return new ResultadoProduto(ResultadoDoCardapio.EmUso);
                }

                var produto = new Produto(EntityId.New().Value, comando.CategoriaId, codigo, nome, comando.PrecoCentavos, RegrasDoCardapio.MoedaPadrao, comando.Ativo, 1);
                await store.InserirProdutoAsync(org, produto, clock.UtcNow, ct);
                await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.ProdutoCriar, ObjectType = "produto", ObjectId = produto.Id.ToString("D"), After = Snapshot(produto) }, ct);
                await eventos.PublicarAsync(request.Organization, new ProdutoCriado(EntityId.New().Value, produto.Id, produto.Codigo, produto.PrecoCentavos, produto.Versao), ct);
                return new ResultadoProduto(ResultadoDoCardapio.Ok, produto);
            },
            cancellationToken);
    }

    public Task<ResultadoProduto> AlterarProdutoAsync(IRequestContext request, Guid id, AlteracaoDeProduto comando, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(comando);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                if (RegrasDoCardapio.Codigo(comando.Codigo) is not { } codigo || RegrasDoCardapio.NomeDeProduto(comando.Nome) is not { } nome || !RegrasDoCardapio.PrecoValido(comando.PrecoCentavos))
                {
                    return new ResultadoProduto(ResultadoDoCardapio.Invalido);
                }

                if (await store.AcharProdutoAsync(org, id, ct) is not { } antes)
                {
                    return new ResultadoProduto(ResultadoDoCardapio.NaoEncontrado);
                }

                if (antes.Versao != comando.Versao)
                {
                    return new ResultadoProduto(ResultadoDoCardapio.VersaoDesatualizada);
                }

                if (await store.AcharCategoriaAsync(org, comando.CategoriaId, ct) is null)
                {
                    return new ResultadoProduto(ResultadoDoCardapio.CategoriaInexistente);
                }

                if (await store.CodigoEmUsoAsync(org, codigo, id, ct))
                {
                    return new ResultadoProduto(ResultadoDoCardapio.EmUso);
                }

                var novo = antes with { Codigo = codigo, Nome = nome, PrecoCentavos = comando.PrecoCentavos, CategoriaId = comando.CategoriaId, Ativo = comando.Ativo, Versao = antes.Versao + 1 };
                if (!await store.AlterarProdutoAsync(org, novo, comando.Versao, clock.UtcNow, ct))
                {
                    return new ResultadoProduto(ResultadoDoCardapio.VersaoDesatualizada);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.ProdutoAlterar, ObjectType = "produto", ObjectId = id.ToString("D"), Before = Snapshot(antes), After = Snapshot(novo) }, ct);
                await eventos.PublicarAsync(request.Organization, new ProdutoAlterado(EntityId.New().Value, id, novo.Codigo, novo.PrecoCentavos, novo.Ativo, novo.Versao), ct);
                return new ResultadoProduto(ResultadoDoCardapio.Ok, novo);
            },
            cancellationToken);
    }

    private static JsonObject Snapshot(Produto p) => new()
    {
        ["codigo"] = p.Codigo,
        ["nome"] = p.Nome,
        ["precoCentavos"] = p.PrecoCentavos,
        ["moeda"] = p.Moeda,
        ["categoriaId"] = p.CategoriaId.ToString("D"),
        ["ativo"] = p.Ativo,
        ["versao"] = p.Versao,
    };

    private static JsonObject Snapshot(Categoria c) => new()
    {
        ["nome"] = c.Nome,
        ["ativa"] = c.Ativa,
        ["versao"] = c.Versao,
    };
}
