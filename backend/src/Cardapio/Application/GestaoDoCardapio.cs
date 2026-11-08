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

    /// <summary>The category tree would pass two levels (parent is itself a child, or the category already has children), or a category is its own parent.</summary>
    NivelInvalido,

    /// <summary>The engine refused the price change (<c>cardapio:preco-alterar</c>, beyond the limit or not held). Answered as 403 without reason.</summary>
    Negado,
}

public sealed record ResultadoProduto(ResultadoDoCardapio Resultado, Produto? Produto = null);

public sealed record ResultadoCategoria(ResultadoDoCardapio Resultado, Categoria? Categoria = null);

public sealed record NovoProduto(string? Codigo, string? Nome, long PrecoCentavos, Guid CategoriaId, bool Ativo, bool Indisponivel = false, Promocao? Promocao = null);

public sealed record AlteracaoDeProduto(string? Codigo, string? Nome, long PrecoCentavos, Guid CategoriaId, bool Ativo, int Versao, bool Indisponivel = false, Promocao? Promocao = null);

/// <summary>
/// Reading and changing the menu. Who may call is the route's job (<c>produto:ver</c>, <c>produto:gerenciar</c>, decided by the one engine). Each change is ONE unit of work:
/// state change, audit record and outbox event commit or roll back together. A price change never touches a comanda already launched: items freeze name and price when launched.
/// Changing the price of an EXISTING product (base, promotion, unit override) also needs <c>cardapio:preco-alterar</c>, asked of the engine with the percent the price moves.
/// </summary>
public sealed class GestaoDoCardapio(IUnitOfWork unitOfWork, ICardapioStore store, IAuditLog audit, IEventosDoCardapio eventos, IDecisaoDePreco precos, IClock clock)
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
        return unitOfWork.ExecuteAsync(request, ct => store.ListarProdutosAsync(request.Organization.OrganizationId, request.Organization.UnitId, termo, incluirInativos, ct), cancellationToken);
    }

    /// <summary>The active unit's most launched active products, most launched first. Empty when there is no history.</summary>
    public Task<IReadOnlyList<Produto>> FrequentesAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.FrequentesAsync(request.Organization.OrganizationId, request.Organization.UnitId, LimiteDeFrequentes, ct), cancellationToken);
    }

    public Task<ResultadoCategoria> CriarCategoriaAsync(IRequestContext request, string? nome, Guid? categoriaPaiId = null, CancellationToken cancellationToken = default)
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

                if (categoriaPaiId is { } paiNovo)
                {
                    if (await store.AcharCategoriaAsync(org, paiNovo, ct) is not { } pai)
                    {
                        return new ResultadoCategoria(ResultadoDoCardapio.CategoriaInexistente);
                    }

                    if (pai.CategoriaPaiId is not null)
                    {
                        return new ResultadoCategoria(ResultadoDoCardapio.NivelInvalido);
                    }
                }

                var categoria = new Categoria(EntityId.New().Value, limpo, true, 1, categoriaPaiId);
                if (await store.NomeEmUsoAsync(org, limpo, null, ct))
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.EmUso);
                }

                await store.InserirCategoriaAsync(org, categoria, clock.UtcNow, ct);

                await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.CategoriaCriar, ObjectType = "categoria", ObjectId = categoria.Id.ToString("D"), After = Snapshot(categoria) }, ct);
                await eventos.PublicarAsync(request.Organization, new CategoriaCriada(EntityId.New().Value, categoria.Id, categoria.Nome, categoria.CategoriaPaiId), ct);
                return new ResultadoCategoria(ResultadoDoCardapio.Ok, categoria);
            },
            cancellationToken);
    }

    public Task<ResultadoCategoria> AlterarCategoriaAsync(IRequestContext request, Guid id, string? nome, bool ativa, int versao, Guid? categoriaPaiId = null, CancellationToken cancellationToken = default)
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

                if (categoriaPaiId is { } paiNovo)
                {
                    if (paiNovo == id)
                    {
                        return new ResultadoCategoria(ResultadoDoCardapio.NivelInvalido);
                    }

                    if (await store.AcharCategoriaAsync(org, paiNovo, ct) is not { } pai)
                    {
                        return new ResultadoCategoria(ResultadoDoCardapio.CategoriaInexistente);
                    }

                    if (pai.CategoriaPaiId is not null || (paiNovo != antes.CategoriaPaiId && await store.TemSubcategoriasAsync(org, id, ct)))
                    {
                        return new ResultadoCategoria(ResultadoDoCardapio.NivelInvalido);
                    }
                }

                var nova = antes with { Nome = limpo, Ativa = ativa, Versao = antes.Versao + 1, CategoriaPaiId = categoriaPaiId };
                if (!await store.AlterarCategoriaAsync(org, nova, versao, clock.UtcNow, ct))
                {
                    return new ResultadoCategoria(ResultadoDoCardapio.VersaoDesatualizada);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.CategoriaAlterar, ObjectType = "categoria", ObjectId = id.ToString("D"), Before = Snapshot(antes), After = Snapshot(nova) }, ct);
                await eventos.PublicarAsync(request.Organization, new CategoriaAlterada(EntityId.New().Value, id, nova.Nome, nova.Ativa, nova.Versao, nova.CategoriaPaiId), ct);
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
                if (RegrasDoCardapio.Codigo(comando.Codigo) is not { } codigo || RegrasDoCardapio.NomeDeProduto(comando.Nome) is not { } nome || !RegrasDoCardapio.PrecoValido(comando.PrecoCentavos) || !RegrasDePreco.PromocaoValida(comando.Promocao, comando.PrecoCentavos))
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

                var produto = new Produto(EntityId.New().Value, comando.CategoriaId, codigo, nome, comando.PrecoCentavos, RegrasDoCardapio.MoedaPadrao, comando.Ativo, 1, comando.Indisponivel, comando.Promocao);
                await store.InserirProdutoAsync(org, produto, clock.UtcNow, ct);
                await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.ProdutoCriar, ObjectType = "produto", ObjectId = produto.Id.ToString("D"), After = Snapshot(produto) }, ct);
                await eventos.PublicarAsync(request.Organization, new ProdutoCriado(EntityId.New().Value, produto.Id, produto.Codigo, produto.PrecoCentavos, produto.Versao), ct);
                return new ResultadoProduto(ResultadoDoCardapio.Ok, produto);
            },
            cancellationToken);
    }

    public async Task<ResultadoProduto> AlterarProdutoAsync(IRequestContext request, Guid id, AlteracaoDeProduto comando, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(comando);
        try
        {
            return await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    var org = request.Organization.OrganizationId;
                    if (RegrasDoCardapio.Codigo(comando.Codigo) is not { } codigo || RegrasDoCardapio.NomeDeProduto(comando.Nome) is not { } nome
                        || !RegrasDoCardapio.PrecoValido(comando.PrecoCentavos) || !RegrasDePreco.PromocaoValida(comando.Promocao, comando.PrecoCentavos))
                    {
                        return new ResultadoProduto(ResultadoDoCardapio.Invalido);
                    }

                    if (await store.AcharProdutoAsync(org, request.Organization.UnitId, id, ct) is not { } antes)
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

                    var novo = antes with
                    {
                        Codigo = codigo,
                        Nome = nome,
                        PrecoCentavos = comando.PrecoCentavos,
                        CategoriaId = comando.CategoriaId,
                        Ativo = comando.Ativo,
                        Indisponivel = comando.Indisponivel,
                        Promocao = comando.Promocao,
                        Versao = antes.Versao + 1,
                    };
                    if (!await store.AlterarProdutoAsync(org, novo, comando.Versao, clock.UtcNow, ct))
                    {
                        return new ResultadoProduto(ResultadoDoCardapio.VersaoDesatualizada);
                    }

                    // The row lock above serializes editors; the price question comes after it and, when refused, throws so the change (and any cap the engine reserved) rolls back.
                    if (antes.PrecoCentavos != novo.PrecoCentavos || antes.Promocao != novo.Promocao)
                    {
                        // Measured against the price in force now (what a launch would charge), not the base: a promotion or an override moves that price.
                        var agora = clock.UtcNow;
                        var vigenteAntes = antes.PrecoEfetivo(agora);
                        var vigenteDepois = novo.PrecoEfetivo(agora);
                        var percentual = Math.Max(
                            RegrasDePreco.PercentualDaMudanca(antes.PrecoCentavos, novo.PrecoCentavos, antes.PrecoCentavos),
                            RegrasDePreco.PercentualDaMudanca(vigenteAntes, vigenteDepois, vigenteAntes));
                        var delta = Math.Max(Math.Abs(novo.PrecoCentavos - antes.PrecoCentavos), Math.Abs(vigenteDepois - vigenteAntes));
                        if (!await precos.PodeAlterarAsync(request, percentual, delta, ct))
                        {
                            throw new PrecoNegadoException();
                        }
                    }

                    await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.ProdutoAlterar, ObjectType = "produto", ObjectId = id.ToString("D"), Before = Snapshot(antes), After = Snapshot(novo) }, ct);
                    await eventos.PublicarAsync(request.Organization, new ProdutoAlterado(EntityId.New().Value, id, novo.Codigo, novo.PrecoCentavos, novo.Ativo, novo.Versao, novo.Indisponivel), ct);
                    return new ResultadoProduto(ResultadoDoCardapio.Ok, novo);
                },
                cancellationToken);
        }
        catch (PrecoNegadoException)
        {
            return new ResultadoProduto(ResultadoDoCardapio.Negado);
        }
    }

    /// <summary>Sets the price of a product in the ACTIVE unit of the session (never a unit from the client). Needs <c>cardapio:preco-alterar</c> for the percent it moves from the price in force there.</summary>
    public Task<ResultadoProduto> DefinirPrecoDaUnidadeAsync(IRequestContext request, Guid produtoId, long precoCentavos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AlterarPrecoDaUnidadeAsync(request, produtoId, precoCentavos, cancellationToken);
    }

    /// <summary>Removes the unit override: the unit goes back to the promotion in force or the base price. 404 when the unit has none.</summary>
    public Task<ResultadoProduto> RemoverPrecoDaUnidadeAsync(IRequestContext request, Guid produtoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AlterarPrecoDaUnidadeAsync(request, produtoId, null, cancellationToken);
    }

    private Task<ResultadoProduto> AlterarPrecoDaUnidadeAsync(IRequestContext request, Guid produtoId, long? precoCentavos, CancellationToken cancellationToken)
    {
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                var unidade = request.Organization.UnitId;
                if (unidade == Guid.Empty || (precoCentavos is { } p && !RegrasDoCardapio.PrecoValido(p)))
                {
                    return new ResultadoProduto(ResultadoDoCardapio.Invalido);
                }

                if (await store.AcharProdutoAsync(org, unidade, produtoId, ct) is not { } antes || (precoCentavos is null && antes.PrecoDaUnidadeCentavos is null))
                {
                    return new ResultadoProduto(ResultadoDoCardapio.NaoEncontrado);
                }

                var agora = clock.UtcNow;
                var vigenteAntes = antes.PrecoEfetivo(agora);
                var vigenteDepois = (antes with { PrecoDaUnidadeCentavos = precoCentavos }).PrecoEfetivo(agora);
                var percentual = RegrasDePreco.PercentualDaMudanca(vigenteAntes, vigenteDepois, vigenteAntes);
                var delta = Math.Abs(vigenteDepois - vigenteAntes);
                if (!await precos.PodeAlterarAsync(request, percentual, delta, ct))
                {
                    return new ResultadoProduto(ResultadoDoCardapio.Negado);
                }

                if (precoCentavos is { } novoPreco)
                {
                    await store.DefinirPrecoDaUnidadeAsync(org, unidade, produtoId, novoPreco, clock.UtcNow, ct);
                    await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.PrecoDaUnidadeDefinir, ObjectType = "produto", ObjectId = produtoId.ToString("D"), Before = PrecoDaUnidade(antes.PrecoDaUnidadeCentavos), After = PrecoDaUnidade(novoPreco) }, ct);
                    await eventos.PublicarAsync(request.Organization, new PrecoDaUnidadeDefinido(EntityId.New().Value, produtoId, unidade, novoPreco), ct);
                }
                else
                {
                    await store.RemoverPrecoDaUnidadeAsync(org, unidade, produtoId, ct);
                    await audit.RecordAsync(request, new AuditEntry { Action = CardapioAuditActions.PrecoDaUnidadeRemover, ObjectType = "produto", ObjectId = produtoId.ToString("D"), Before = PrecoDaUnidade(antes.PrecoDaUnidadeCentavos), After = PrecoDaUnidade(null) }, ct);
                    await eventos.PublicarAsync(request.Organization, new PrecoDaUnidadeRemovido(EntityId.New().Value, produtoId, unidade), ct);
                }

                return new ResultadoProduto(ResultadoDoCardapio.Ok, antes with { PrecoDaUnidadeCentavos = precoCentavos });
            },
            cancellationToken);
    }

    private static JsonObject PrecoDaUnidade(long? centavos) => new() { ["precoDaUnidadeCentavos"] = centavos };

    private static JsonObject Snapshot(Produto p) => new()
    {
        ["codigo"] = p.Codigo,
        ["nome"] = p.Nome,
        ["precoCentavos"] = p.PrecoCentavos,
        ["moeda"] = p.Moeda,
        ["categoriaId"] = p.CategoriaId.ToString("D"),
        ["ativo"] = p.Ativo,
        ["indisponivel"] = p.Indisponivel,
        ["precoPromocionalCentavos"] = p.Promocao?.PrecoCentavos,
        ["promocaoInicio"] = p.Promocao?.Inicio.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["promocaoFim"] = p.Promocao?.Fim.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["versao"] = p.Versao,
    };

    private static JsonObject Snapshot(Categoria c) => new()
    {
        ["nome"] = c.Nome,
        ["ativa"] = c.Ativa,
        ["categoriaPaiId"] = c.CategoriaPaiId?.ToString("D"),
        ["versao"] = c.Versao,
    };
}

/// <summary>Signal: the engine refused a price change after the row was written, so the unit of work must roll back (a return value would not).</summary>
public sealed class PrecoNegadoException : Exception
{
    public PrecoNegadoException()
        : base("Price change refused.")
    {
    }

    public PrecoNegadoException(string message)
        : base(message)
    {
    }

    public PrecoNegadoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
