using System.Text.Json.Nodes;
using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Cardapio.Application;

/// <summary>
/// Option groups and the links to products. Who may call is the route's job (<c>produto:ver</c> to read, <c>produto:gerenciar</c> to change, decided by the one engine). Each change is ONE unit of work:
/// state change, audit record and outbox event commit or roll back together. An option change never reaches an item already launched (name and price are frozen in it).
/// </summary>
public sealed class GestaoDeOpcoes(IUnitOfWork unitOfWork, IOpcoesStore store, ICardapioStore cardapio, IAuditLog audit, IEventosDoCardapio eventos, IClock clock)
{
    public const int LimiteDeFrequentes = 6;

    public Task<IReadOnlyList<GrupoDeOpcao>> ListarGruposAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.ListarGruposAsync(request.Organization.OrganizationId, true, ct), cancellationToken);
    }

    /// <summary>The groups of a product as the launch sees them (inactive groups left out); null when the product does not exist.</summary>
    public Task<IReadOnlyList<GrupoDeOpcao>?> GruposDoProdutoAsync(IRequestContext request, Guid produtoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<GrupoDeOpcao>?>(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                if (await cardapio.AcharProdutoAsync(org, request.Organization.UnitId, produtoId, ct) is null)
                {
                    return null;
                }

                return (await store.GruposDoProdutoAsync(org, produtoId, ct)).Where(g => g.Ativo).ToList();
            },
            cancellationToken);
    }

    /// <summary>The most launched options of a product in the ACTIVE unit, most launched first; null when the product does not exist.</summary>
    public Task<IReadOnlyList<OpcaoFrequente>?> FrequentesAsync(IRequestContext request, Guid produtoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<OpcaoFrequente>?>(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                return await cardapio.AcharProdutoAsync(org, request.Organization.UnitId, produtoId, ct) is null
                    ? null
                    : await store.FrequentesAsync(org, request.Organization.UnitId, produtoId, LimiteDeFrequentes, ct);
            },
            cancellationToken);
    }

    public Task<ResultadoGrupo> CriarGrupoAsync(IRequestContext request, NovoGrupoDeOpcao comando, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(comando);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (Montar(EntityId.New().Value, comando.Nome, comando.Minimo, comando.Maximo, comando.Ativo, 1, comando.Opcoes, []) is not { } grupo)
                {
                    return new ResultadoGrupo(ResultadoDoCardapio.Invalido);
                }

                await store.InserirGrupoAsync(request.Organization.OrganizationId, grupo, clock.UtcNow, ct);
                await audit.RecordAsync(request, new AuditEntry { Action = OpcoesAuditActions.GrupoCriar, ObjectType = "grupo-opcao", ObjectId = grupo.Id.ToString("D"), After = Snapshot(grupo) }, ct);
                await eventos.PublicarAsync(request.Organization, new GrupoDeOpcaoCriado(EntityId.New().Value, grupo.Id, grupo.Nome, grupo.Minimo, grupo.Maximo, grupo.Opcoes.Count), ct);
                return new ResultadoGrupo(ResultadoDoCardapio.Ok, grupo);
            },
            cancellationToken);
    }

    public Task<ResultadoGrupo> AlterarGrupoAsync(IRequestContext request, Guid id, AlteracaoDeGrupoDeOpcao comando, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(comando);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                if (await store.AcharGrupoAsync(org, id, ct) is not { } antes)
                {
                    return new ResultadoGrupo(ResultadoDoCardapio.NaoEncontrado);
                }

                if (antes.Versao != comando.Versao)
                {
                    return new ResultadoGrupo(ResultadoDoCardapio.VersaoDesatualizada);
                }

                if (Montar(id, comando.Nome, comando.Minimo, comando.Maximo, comando.Ativo, antes.Versao + 1, comando.Opcoes, antes.Opcoes) is not { } novo)
                {
                    return new ResultadoGrupo(ResultadoDoCardapio.Invalido);
                }

                if (!await store.AlterarGrupoAsync(org, novo, comando.Versao, clock.UtcNow, ct))
                {
                    return new ResultadoGrupo(ResultadoDoCardapio.VersaoDesatualizada);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = OpcoesAuditActions.GrupoAlterar, ObjectType = "grupo-opcao", ObjectId = id.ToString("D"), Before = Snapshot(antes), After = Snapshot(novo) }, ct);
                await eventos.PublicarAsync(request.Organization, new GrupoDeOpcaoAlterado(EntityId.New().Value, id, novo.Nome, novo.Minimo, novo.Maximo, novo.Ativo, novo.Versao, novo.Opcoes.Count), ct);
                return new ResultadoGrupo(ResultadoDoCardapio.Ok, novo);
            },
            cancellationToken);
    }

    /// <summary>Replaces the groups a product offers (the order is kept). An empty list removes them all.</summary>
    public Task<ResultadoGruposDoProduto> DefinirGruposDoProdutoAsync(IRequestContext request, Guid produtoId, IReadOnlyList<Guid>? grupoIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                var ids = grupoIds ?? [];
                if (ids.Count > 20 || ids.Distinct().Count() != ids.Count || ids.Contains(Guid.Empty))
                {
                    return new ResultadoGruposDoProduto(ResultadoDoCardapio.Invalido);
                }

                if (await cardapio.AcharProdutoAsync(org, request.Organization.UnitId, produtoId, ct) is null)
                {
                    return new ResultadoGruposDoProduto(ResultadoDoCardapio.NaoEncontrado);
                }

                if (ids.Count > 0 && await store.ContarGruposAsync(org, ids, ct) != ids.Count)
                {
                    return new ResultadoGruposDoProduto(ResultadoDoCardapio.Invalido);
                }

                var antes = await store.GruposDoProdutoAsync(org, produtoId, ct);
                await store.DefinirGruposDoProdutoAsync(org, produtoId, ids, ct);
                var depois = await store.GruposDoProdutoAsync(org, produtoId, ct);
                await audit.RecordAsync(
                    request,
                    new AuditEntry { Action = OpcoesAuditActions.GruposDoProdutoDefinir, ObjectType = "produto", ObjectId = produtoId.ToString("D"), Before = Ids(antes), After = Ids(depois) },
                    ct);
                await eventos.PublicarAsync(request.Organization, new GruposDoProdutoDefinidos(EntityId.New().Value, produtoId, depois.Select(g => g.Id).ToList()), ct);
                return new ResultadoGruposDoProduto(ResultadoDoCardapio.Ok, depois);
            },
            cancellationToken);
    }

    /// <summary>The valid group built from the command, or null when anything in it is refused. Listed ids must be options this group already has; unlisted existing options are kept, turned off.</summary>
    private static GrupoDeOpcao? Montar(Guid id, string? nome, int minimo, int maximo, bool ativo, int versao, IReadOnlyList<OpcaoNova>? opcoes, IReadOnlyList<Opcao> existentes)
    {
        if (RegrasDeOpcoes.NomeDoGrupo(nome) is not { } limpo || !RegrasDeOpcoes.LimitesValidos(minimo, maximo))
        {
            return null;
        }

        var lista = new List<Opcao>();
        var vistos = new HashSet<Guid>();
        foreach (var o in opcoes ?? [])
        {
            if (o is null || RegrasDeOpcoes.NomeDaOpcao(o.Nome) is not { } nomeDaOpcao || !RegrasDoCardapio.PrecoValido(o.PrecoCentavos))
            {
                return null;
            }

            if (o.Id is { } existente && (!existentes.Any(e => e.Id == existente) || !vistos.Add(existente)))
            {
                return null;
            }

            lista.Add(new Opcao(o.Id ?? EntityId.New().Value, nomeDaOpcao, o.PrecoCentavos, o.Ativa));
        }

        lista.AddRange(existentes.Where(e => !vistos.Contains(e.Id)).Select(e => e with { Ativa = false }));
        return lista.Count > RegrasDeOpcoes.OpcoesMaximasPorGrupo ? null : new GrupoDeOpcao(id, limpo, minimo, maximo, ativo, versao, lista);
    }

    private static JsonObject Ids(IReadOnlyList<GrupoDeOpcao> grupos) => new() { ["grupoIds"] = new JsonArray(grupos.Select(g => (JsonNode)g.Id.ToString("D")).ToArray()) };

    private static JsonObject Snapshot(GrupoDeOpcao g) => new()
    {
        ["nome"] = g.Nome,
        ["minimo"] = g.Minimo,
        ["maximo"] = g.Maximo,
        ["ativo"] = g.Ativo,
        ["opcoes"] = new JsonArray(g.Opcoes.Select(o => (JsonNode)new JsonObject { ["id"] = o.Id.ToString("D"), ["nome"] = o.Nome, ["precoCentavos"] = o.PrecoCentavos, ["ativa"] = o.Ativa }).ToArray()),
        ["versao"] = g.Versao,
    };
}
