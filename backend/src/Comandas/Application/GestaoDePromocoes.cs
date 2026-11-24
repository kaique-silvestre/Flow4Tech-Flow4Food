using System.Text.Json.Nodes;
using Flow4Tech.Cardapio.Application;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Comandas.Application;

[PermissionCatalog]
public static class PromocaoPermissions
{
    /// <summary>See the promotions and combos of the organization (the ones that apply to the active unit).</summary>
    public const string Ver = "promocao:ver";

    /// <summary>Create and change promotions and combos, and turn them on and off. Applying one to an item needs no permission: the server does it by itself when the round is launched.</summary>
    public const string Gerenciar = "promocao:gerenciar";
}

/// <summary>Port: the promotion rules on PostgreSQL. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IPromocoesStore
{
    /// <summary>The rules that apply to the unit (every unit, or that one), newest first; <paramref name="somenteAtivas"/> keeps only the turned on ones.</summary>
    Task<IReadOnlyList<Promocao>> ListarAsync(Guid organizationId, Guid unitId, bool somenteAtivas, CancellationToken cancellationToken);

    Task<Promocao?> AcharAsync(Guid organizationId, Guid promocaoId, CancellationToken cancellationToken);

    Task InserirAsync(Guid organizationId, Promocao promocao, CancellationToken cancellationToken);

    /// <summary>Rewrites the editable fields (not the type or the products) when the stored version is still <c>promocao.Versao - 1</c>; false otherwise.</summary>
    Task<bool> AtualizarAsync(Guid organizationId, Promocao promocao, CancellationToken cancellationToken);
}

/// <summary>Port: the instant on the unit's wall clock (its configured time zone), adapted in Api over the unit's agenda. Days and time windows of a promotion are judged on it.</summary>
public interface IHoraLocalDaUnidade
{
    Task<DateTime> LocalAsync(OrganizationContext organization, DateTimeOffset at, CancellationToken cancellationToken);
}

public enum ResultadoDePromocao
{
    Ok,

    /// <summary>Name, type, amounts, window, days or products that do not make a rule.</summary>
    Invalido,

    /// <summary>A product of the rule does not exist in the menu.</summary>
    ProdutoInvalido,

    NaoEncontrada,

    VersaoDesatualizada,
}

public sealed record ResultadoDePromocaoComDados(ResultadoDePromocao Resultado, Promocao? Promocao = null, int? VersaoAtual = null);

/// <summary>What a client may say about a rule: never the unit (only whether it is every unit or the active one), never a computed amount. <c>Tipo</c> and <c>Produtos</c> are only read when creating.</summary>
public sealed record PromocaoParaSalvar(
    string? Nome,
    string? Tipo,
    decimal? Percentual,
    long? ValorCentavos,
    bool TodasAsUnidades,
    bool Ativo,
    DateTimeOffset? ValidoDe,
    DateTimeOffset? ValidoAte,
    IReadOnlyList<int>? DiasDaSemana,
    string? HoraInicio,
    string? HoraFim,
    IReadOnlyList<ProdutoDaPromocao>? Produtos);

/// <summary>
/// The promotion and combo rules. Who may call is the route's job (<c>promocao:ver</c> to read, <c>promocao:gerenciar</c> to change, decided by the one engine). Every write is one unit of work with its audit
/// record. A rule is never deleted (turn it off); its type and products are fixed once created (a different combo is a new rule); an edit carries the version it read.
/// </summary>
public sealed class GestaoDePromocoes(IUnitOfWork unitOfWork, IPromocoesStore promocoes, ICardapioStore cardapio, IAuditLog audit, IClock clock)
{
    public Task<IReadOnlyList<Promocao>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => promocoes.ListarAsync(request.Organization.OrganizationId, request.Organization.UnitId, false, ct), cancellationToken);
    }

    public Task<Promocao?> AcharAsync(IRequestContext request, Guid promocaoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, async ct => Visivel(request, await promocoes.AcharAsync(request.Organization.OrganizationId, promocaoId, ct)), cancellationToken);
    }

    public Task<ResultadoDePromocaoComDados> CriarAsync(IRequestContext request, PromocaoParaSalvar dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dados);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                if (!RegrasDePromocao.TentarTipo(dados.Tipo, out var tipo) || Montar(request, dados, tipo, Guid.Empty, 1, ProdutosValidos(tipo, dados.Produtos)) is not { } nova)
                {
                    return new ResultadoDePromocaoComDados(ResultadoDePromocao.Invalido);
                }

                foreach (var produto in nova.Produtos)
                {
                    if (await cardapio.AcharProdutoAsync(org.OrganizationId, org.UnitId, produto.ProdutoId, ct) is null)
                    {
                        return new ResultadoDePromocaoComDados(ResultadoDePromocao.ProdutoInvalido);
                    }
                }

                var agora = clock.UtcNow;
                var gravada = nova with { Id = EntityId.New().Value, CriadoPor = request.UserId, CriadoEm = agora, AtualizadoPor = request.UserId, AtualizadoEm = agora };
                await promocoes.InserirAsync(org.OrganizationId, gravada, ct);
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.CriarPromocao, ObjectType = "promocao", ObjectId = gravada.Id.ToString("D"), After = Json(gravada) }, ct);
                return new ResultadoDePromocaoComDados(ResultadoDePromocao.Ok, gravada);
            },
            cancellationToken);
    }

    /// <summary>Changes name, amounts, scope, window and on/off of a rule. <paramref name="versao"/> is the version the caller read; a different one changes nothing and answers the current one.</summary>
    public Task<ResultadoDePromocaoComDados> AlterarAsync(IRequestContext request, Guid promocaoId, int versao, PromocaoParaSalvar dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dados);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                var antes = Visivel(request, await promocoes.AcharAsync(org.OrganizationId, promocaoId, ct));
                if (antes is null)
                {
                    return new ResultadoDePromocaoComDados(ResultadoDePromocao.NaoEncontrada);
                }

                if (versao < 1 || Montar(request, dados, antes.Tipo, antes.Id, antes.Versao + 1, antes.Produtos) is not { } nova)
                {
                    return new ResultadoDePromocaoComDados(ResultadoDePromocao.Invalido);
                }

                if (versao != antes.Versao)
                {
                    return new ResultadoDePromocaoComDados(ResultadoDePromocao.VersaoDesatualizada, VersaoAtual: antes.Versao);
                }

                var depois = nova with { CriadoPor = antes.CriadoPor, CriadoEm = antes.CriadoEm, AtualizadoPor = request.UserId, AtualizadoEm = clock.UtcNow };
                if (!await promocoes.AtualizarAsync(org.OrganizationId, depois, ct))
                {
                    var atual = await promocoes.AcharAsync(org.OrganizationId, promocaoId, ct);
                    return new ResultadoDePromocaoComDados(ResultadoDePromocao.VersaoDesatualizada, VersaoAtual: atual?.Versao);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.AlterarPromocao, ObjectType = "promocao", ObjectId = promocaoId.ToString("D"), Before = Json(antes), After = Json(depois) }, ct);
                return new ResultadoDePromocaoComDados(ResultadoDePromocao.Ok, depois);
            },
            cancellationToken);
    }

    /// <summary>A rule restricted to another unit does not exist for this session.</summary>
    private static Promocao? Visivel(IRequestContext request, Promocao? promocao) =>
        promocao is not null && RegrasDePromocao.AplicaNaUnidade(promocao, request.Organization.UnitId) ? promocao : null;

    private static IReadOnlyList<ProdutoDaPromocao>? ProdutosValidos(TipoDePromocao tipo, IReadOnlyList<ProdutoDaPromocao>? produtos)
    {
        if (produtos is null || produtos.Count is < 1 or > RegrasDePromocao.ProdutosMaximo || produtos.Any(p => p is null || p.Quantidade is < 1 or > RegrasDePromocao.QuantidadeMaxima || p.ProdutoId == Guid.Empty))
        {
            return null;
        }

        var distintos = produtos.Select(p => p.ProdutoId).Distinct().Count() == produtos.Count;
        var unidades = produtos.Sum(p => p.Quantidade);
        var formato = tipo == TipoDePromocao.Combo ? unidades >= 2 : produtos.All(p => p.Quantidade == 1);
        return distintos && formato ? produtos : null;
    }

    /// <summary>The rule the data describe, or null when it does not make one. Id, author and instants are filled by the caller.</summary>
    private static Promocao? Montar(IRequestContext request, PromocaoParaSalvar d, TipoDePromocao tipo, Guid id, int versao, IReadOnlyList<ProdutoDaPromocao>? produtos)
    {
        var unidade = request.Organization.UnitId;
        var mascara = RegrasDePromocao.MascaraDeDias(d.DiasDaSemana);
        if (produtos is null || RegrasDePromocao.NomeValido(d.Nome) is not { } nome || mascara is null || (!d.TodasAsUnidades && unidade == Guid.Empty))
        {
            return null;
        }

        var amounts = tipo switch
        {
            TipoDePromocao.Percentual => RegrasDeDesconto.PercentualValido(d.Percentual ?? 0m) && d.ValorCentavos is null,
            _ => d.Percentual is null && d.ValorCentavos is >= 1,
        };
        if (!amounts || (d.ValidoDe is { } de && d.ValidoAte is { } ate && de >= ate))
        {
            return null;
        }

        TimeOnly? inicio = null;
        TimeOnly? fim = null;
        if (d.HoraInicio is not null || d.HoraFim is not null)
        {
            if (!RegrasDePromocao.TentarHora(d.HoraInicio, out var i) || !RegrasDePromocao.TentarHora(d.HoraFim, out var f) || i == f)
            {
                return null;
            }

            (inicio, fim) = (i, f);
        }

        return new Promocao(id, d.TodasAsUnidades ? null : unidade, nome, tipo, tipo == TipoDePromocao.Percentual ? d.Percentual : null, tipo == TipoDePromocao.Percentual ? null : d.ValorCentavos, d.Ativo, d.ValidoDe, d.ValidoAte, mascara.Value, inicio, fim, versao, produtos, Guid.Empty, default, Guid.Empty, default);
    }

    private static JsonObject Json(Promocao p) => new()
    {
        ["nome"] = p.Nome,
        ["tipo"] = RegrasDePromocao.Palavra(p.Tipo),
        ["percentual"] = p.Percentual,
        ["valorCentavos"] = p.ValorCentavos,
        ["ativo"] = p.Ativo,
        ["todasAsUnidades"] = p.UnidadeId is null,
        ["validoDe"] = p.ValidoDe,
        ["validoAte"] = p.ValidoAte,
        ["dias"] = new JsonArray([.. RegrasDePromocao.DiasDaMascara(p.DiasDaSemana).Select(d => JsonValue.Create(d))]),
        ["horaInicio"] = p.HoraInicio is { } i ? RegrasDePromocao.Texto(i) : null,
        ["horaFim"] = p.HoraFim is { } f ? RegrasDePromocao.Texto(f) : null,
        ["produtos"] = new JsonArray([.. p.Produtos.Select(x => (JsonNode)new JsonObject { ["produtoId"] = x.ProdutoId.ToString("D"), ["quantidade"] = x.Quantidade })]),
        ["versao"] = p.Versao,
    };
}

/// <summary>
/// The promotions of one round: loads the rules that apply to the unit, keeps those in force at the instant (the unit's wall clock for days and time window) and lets <see cref="RegrasDePromocao.Avaliar"/>
/// decide. Runs inside the launch's unit of work.
/// </summary>
public sealed class AvaliadorDePromocoes(IPromocoesStore promocoes, IHoraLocalDaUnidade hora)
{
    public async Task<IReadOnlyList<PromocaoAplicada>> AvaliarAsync(OrganizationContext organization, Guid unitId, IReadOnlyList<LinhaParaPromocao> linhas, DateTimeOffset em, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(linhas);
        var ativas = await promocoes.ListarAsync(organization.OrganizationId, unitId, true, cancellationToken);
        if (ativas.Count == 0)
        {
            return [];
        }

        var local = await hora.LocalAsync(organization, em, cancellationToken);
        var produtos = linhas.Select(l => l.ProdutoId).ToHashSet();
        var vigentes = ativas.Where(p => RegrasDePromocao.AplicaNaUnidade(p, unitId) && p.Produtos.Any(x => produtos.Contains(x.ProdutoId)) && RegrasDePromocao.VigenteEm(p, em, local)).ToList();
        return vigentes.Count == 0 ? [] : RegrasDePromocao.Avaliar(linhas, vigentes);
    }
}
