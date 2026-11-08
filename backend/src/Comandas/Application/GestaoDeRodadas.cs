using System.Text.Json.Nodes;
using Flow4Tech.Cardapio.Application;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDeRodada
{
    Ok,

    /// <summary>The round id was already stored: the stored result is answered and nothing is written.</summary>
    Repetida,

    Invalido,

    ComandaNaoEncontrada,

    ComandaNaoAberta,

    ProdutoInativo,

    /// <summary>The chosen options do not fit the product's groups (unknown, repeated, below the minimum or above the maximum): the whole round is refused.</summary>
    OpcaoInvalida,

    /// <summary>A chosen option, or its group, is turned off: the whole round is refused.</summary>
    OpcaoInativa,

    /// <summary>The write carried a version other than the current one (the caller learns the current one).</summary>
    VersaoDesatualizada,

    RodadaNaoEncontrada,

    SomenteQuemEnviou,

    JanelaExpirada,

    RodadaJaDesfeita,

    RodadaComItemCancelado,

    ItemNaoEncontrado,

    ItemJaCancelado,

    /// <summary>Taking this away would put the bill below what was already received.</summary>
    ValorJaRecebido,

    /// <summary>"Repeat the last round" on a comanda that has no live item to repeat.</summary>
    SemRodadaAnterior,

    /// <summary>Every item of the previous round is now unavailable: nothing is written, the reasons are reported.</summary>
    NadaParaRepetir,
}

/// <summary>Why an item of the previous round was not repeated.</summary>
public static class MotivoDeItemIgnorado
{
    /// <summary>The product is turned off or "acabou".</summary>
    public const string ProdutoInativo = "produto-inativo";

    /// <summary>One of its add-ons (or its group) is turned off.</summary>
    public const string OpcaoInativa = "opcao-inativa";

    /// <summary>The add-ons no longer fit the product's groups (for example a new required group).</summary>
    public const string OpcaoInvalida = "opcao-invalida";
}

/// <summary>An item of the previous round that was left out of the repeat, with the reason (never dropped silently).</summary>
public sealed record ItemIgnorado(Guid ProdutoId, string ProdutoNome, int Quantidade, string Motivo);

/// <summary><c>Versao</c> is the comanda version after the write (or the one the round produced, on a repeat); <c>VersaoAtual</c> is set on a stale version.</summary>
public sealed record ResultadoDeEscrita(ResultadoDeRodada Resultado, RodadaComItens? Rodada = null, ItemDaComanda? Item = null, int? Versao = null, int? VersaoAtual = null, IReadOnlyList<ItemIgnorado>? Ignorados = null);

/// <summary><c>OpcaoIds</c> are ids only: name and price come from the menu on the server, never from the client.</summary>
public sealed record ItemParaLancar(Guid ProdutoId, int Quantidade, string? Observacao, IReadOnlyList<Guid>? OpcaoIds = null);

/// <summary>Raised inside the unit of work when an undo is refused AFTER the comanda row lock and version bump: the bump rolls back and the caller answers with <see cref="Resultado"/>.</summary>
public sealed class RecusaDoDesfazer : InvalidOperationException
{
    public RecusaDoDesfazer()
        : this(ResultadoDeRodada.Invalido)
    {
    }

    public RecusaDoDesfazer(string message)
        : base(message)
    {
    }

    public RecusaDoDesfazer(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RecusaDoDesfazer(ResultadoDeRodada resultado)
        : base("Desfazer recusado: " + resultado)
    {
        Resultado = resultado;
    }

    public ResultadoDeRodada Resultado { get; }
}

/// <summary>
/// Launching rounds, undoing them and cancelling items. Who may call is the route's job (decided by the one engine). Every write is ONE unit of work: the version bump, the rows, the audit record and
/// the outbox event commit or roll back together, and a stale version changes nothing. A waiter reaches only comandas they opened unless they hold <c>comanda:ver-comandas-de-outros</c>.
/// </summary>
public sealed class GestaoDeRodadas(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IRodadasStore rodadas,
    ICardapioStore cardapio,
    IOpcoesStore opcoes,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    GestaoDePagamentos pagamentos,
    ISettings settings,
    IClock clock)
{
    public Task<ResultadoDeEscrita> LancarAsync(IRequestContext request, Guid comandaId, Guid rodadaId, int versao, IReadOnlyList<ItemParaLancar> itens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(itens);
        return unitOfWork.ExecuteAsync(request, ct => LancarNaUnidadeAsync(request, comandaId, rodadaId, versao, itens, null, ct), cancellationToken);
    }

    /// <summary>
    /// "Repeat the last round": a NEW round (id from the client, so a resend is a repeat) with the live items of the comanda's latest round that still has some. Prices are read again now,
    /// never copied from the old round. Items whose product is off or "acabou", or whose add-ons no longer fit, are not launched and come back in <see cref="ResultadoDeEscrita.Ignorados"/>;
    /// when nothing is left the answer is <see cref="ResultadoDeRodada.NadaParaRepetir"/> and nothing is written. A resend answers the stored round (the skipped list is not rebuilt).
    /// </summary>
    public Task<ResultadoDeEscrita> RepetirUltimaAsync(IRequestContext request, Guid comandaId, Guid rodadaId, int versao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (rodadaId == Guid.Empty || versao < 1)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.Invalido);
                }

                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.ComandaNaoEncontrada);
                }

                var existente = await rodadas.AcharRodadaAsync(org.OrganizationId, rodadaId, ct);
                if (existente is not null)
                {
                    return Repetida(existente, comandaId);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.ComandaNaoAberta);
                }

                var vivos = (await rodadas.ListarItensAsync(org.OrganizationId, comandaId, ct)).Where(i => i.Estado == EstadoDoItem.Lancado).ToList();
                if (vivos.Count == 0)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.SemRodadaAnterior);
                }

                var anterior = vivos[^1].RodadaId;
                var lancaveis = new List<ItemParaLancar>();
                var ignorados = new List<ItemIgnorado>();
                var produtos = new Dictionary<Guid, bool>();
                foreach (var item in vivos.Where(i => i.RodadaId == anterior))
                {
                    if (!produtos.TryGetValue(item.ProdutoId, out var lancavel))
                    {
                        lancavel = (await cardapio.AcharProdutoAsync(org.OrganizationId, comanda.UnidadeId, item.ProdutoId, ct))?.Lancavel ?? false;
                        produtos[item.ProdutoId] = lancavel;
                    }

                    if (!lancavel)
                    {
                        ignorados.Add(new ItemIgnorado(item.ProdutoId, item.ProdutoNome, item.Quantidade, MotivoDeItemIgnorado.ProdutoInativo));
                        continue;
                    }

                    var opcaoIds = item.Opcoes.Select(o => o.OpcaoId).ToList();
                    var escolha = await OpcoesDoProduto.EscolherAsync(opcoes, org.OrganizationId, item.ProdutoId, opcaoIds, ct);
                    if (escolha.Recusa != RecusaDeOpcoes.Nenhuma)
                    {
                        ignorados.Add(new ItemIgnorado(item.ProdutoId, item.ProdutoNome, item.Quantidade, escolha.Recusa == RecusaDeOpcoes.Inativa ? MotivoDeItemIgnorado.OpcaoInativa : MotivoDeItemIgnorado.OpcaoInvalida));
                        continue;
                    }

                    lancaveis.Add(new ItemParaLancar(item.ProdutoId, item.Quantidade, item.Observacao, opcaoIds));
                }

                if (lancaveis.Count == 0)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.NadaParaRepetir, Ignorados: ignorados);
                }

                var resultado = await LancarNaUnidadeAsync(request, comandaId, rodadaId, versao, lancaveis, anterior, ct);
                return resultado.Resultado == ResultadoDeRodada.Ok ? resultado with { Ignorados = ignorados } : resultado;
            },
            cancellationToken);
    }

    private async Task<ResultadoDeEscrita> LancarNaUnidadeAsync(IRequestContext request, Guid comandaId, Guid rodadaId, int versao, IReadOnlyList<ItemParaLancar> itens, Guid? repeticaoDe, CancellationToken ct)
    {
        if (rodadaId == Guid.Empty || versao < 1 || itens.Count is < 1 or > RegrasDeRodada.ItensMaximoPorRodada)
        {
            return new ResultadoDeEscrita(ResultadoDeRodada.Invalido);
        }

        var observacoes = new string?[itens.Count];
        for (var i = 0; i < itens.Count; i++)
        {
            if (itens[i] is null || !RegrasDeRodada.QuantidadeValida(itens[i].Quantidade) || !RegrasDeRodada.TentarObservacao(itens[i].Observacao, out observacoes[i]))
            {
                return new ResultadoDeEscrita(ResultadoDeRodada.Invalido);
            }
        }

        var org = request.Organization;
        var comanda = await AcharVisivelAsync(request, comandaId, ct);
        if (comanda is null)
        {
            return new ResultadoDeEscrita(ResultadoDeRodada.ComandaNaoEncontrada);
        }

        var existente = await rodadas.AcharRodadaAsync(org.OrganizationId, rodadaId, ct);
        if (existente is not null)
        {
            return Repetida(existente, comandaId);
        }

        if (comanda.Estado != EstadoDaComanda.Aberta)
        {
            return new ResultadoDeEscrita(ResultadoDeRodada.ComandaNaoAberta);
        }

        // The price charged is the one in force for the comanda's unit at this instant (unit override, else promotion while valid, else base), frozen into the item.
        var precoEm = clock.UtcNow;
        var produtos = new Dictionary<Guid, (Guid Id, string Nome, long PrecoCentavos)>();
        foreach (var produtoId in itens.Select(i => i.ProdutoId).Distinct())
        {
            var produto = await cardapio.AcharProdutoAsync(org.OrganizationId, comanda.UnidadeId, produtoId, ct);
            if (produto is null)
            {
                return new ResultadoDeEscrita(ResultadoDeRodada.Invalido);
            }

            if (!produto.Lancavel)
            {
                return new ResultadoDeEscrita(ResultadoDeRodada.ProdutoInativo);
            }

            produtos[produtoId] = (produto.Id, produto.Nome, produto.PrecoEfetivo(precoEm));
        }

        // Options: each item is checked against the groups of ITS product; one bad choice refuses the whole round (nothing is written).
        var escolhas = new IReadOnlyList<OpcaoParaItem>[itens.Count];
        for (var i = 0; i < itens.Count; i++)
        {
            var escolha = await OpcoesDoProduto.EscolherAsync(opcoes, org.OrganizationId, itens[i].ProdutoId, itens[i].OpcaoIds ?? [], ct);
            if (escolha.Recusa != RecusaDeOpcoes.Nenhuma)
            {
                return new ResultadoDeEscrita(escolha.Recusa == RecusaDeOpcoes.Inativa ? ResultadoDeRodada.OpcaoInativa : ResultadoDeRodada.OpcaoInvalida);
            }

            escolhas[i] = escolha.Opcoes;
        }

        if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
        {
            // Lost the race on the row lock (or the caller is stale). A concurrent repeat of THIS round id is not a conflict: it gets the stored result.
            var agora = await rodadas.AcharRodadaAsync(org.OrganizationId, rodadaId, ct);
            return agora is not null ? Repetida(agora, comandaId) : await FalhaDeVersaoAsync(org, comandaId, ct);
        }

        var enviadaEm = RegrasDeRodada.Microssegundos(clock.UtcNow);
        var novaVersao = versao + 1;
        var rodada = new Rodada(rodadaId, comandaId, comanda.UnidadeId, request.UserId, enviadaEm, null, novaVersao);
        var lista = new List<ItemDaComanda>(itens.Count);
        for (var i = 0; i < itens.Count; i++)
        {
            var produto = produtos[itens[i].ProdutoId];
            lista.Add(new ItemDaComanda(EntityId.New().Value, comandaId, rodadaId, i + 1, produto.Id, produto.Nome, produto.PrecoCentavos, itens[i].Quantidade, observacoes[i], EstadoDoItem.Lancado, null, null, null, escolhas[i].Select(e => new OpcaoDoItem(e.OpcaoId, e.GrupoId, e.Nome, e.PrecoCentavos)).ToList()));
        }

        var resultado = new RodadaComItens(rodada, lista);
        await rodadas.InserirRodadaAsync(org.OrganizationId, resultado, ct);
        await rodadas.RegistrarFrequenciaAsync(org.OrganizationId, comanda.UnidadeId, request.UserId, produtos.Keys.Order().ToList(), ct);
        var usosDeOpcao = lista.SelectMany(i => i.Opcoes.Select(o => (i.ProdutoId, o.OpcaoId))).Distinct().Order().ToList();
        if (usosDeOpcao.Count > 0)
        {
            await rodadas.RegistrarFrequenciaDeOpcoesAsync(org.OrganizationId, comanda.UnidadeId, usosDeOpcao, ct);
        }

        var valor = RegrasDeRodada.Consumo(lista);
        var depois = new JsonObject
        {
            ["comandaNumero"] = comanda.Numero,
            ["itens"] = lista.Count,
            ["valorCentavos"] = valor,
            ["versao"] = novaVersao,
        };
        if (repeticaoDe is { } origem)
        {
            depois["repeticaoDe"] = origem.ToString("D");
        }

        await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.LancarRodada, ObjectType = "rodada", ObjectId = rodadaId.ToString("D"), After = depois }, ct);
        await eventos.PublicarAsync(org, new RodadaEnviada(EntityId.New().Value, comandaId, rodadaId, comanda.UnidadeId, lista.Count, valor, request.UserId), ct);
        return new ResultadoDeEscrita(ResultadoDeRodada.Ok, resultado, Versao: novaVersao);
    }

    /// <summary><paramref name="versao"/> must still be a valid version (the contract keeps the field) but is not compared with the current one.</summary>
    public async Task<ResultadoDeEscrita> DesfazerAsync(IRequestContext request, Guid comandaId, Guid rodadaId, int versao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await DesfazerNaUnidadeAsync(request, comandaId, rodadaId, versao, cancellationToken);
        }
        catch (RecusaDoDesfazer recusa)
        {
            // Refused after the lock was taken: the unit of work rolled the version bump back, nothing changed.
            return new ResultadoDeEscrita(recusa.Resultado);
        }
    }

    private Task<ResultadoDeEscrita> DesfazerNaUnidadeAsync(IRequestContext request, Guid comandaId, Guid rodadaId, int versao, CancellationToken cancellationToken)
    {
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (versao < 1)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.Invalido);
                }

                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                var rodada = comanda is null ? null : await rodadas.AcharRodadaAsync(org.OrganizationId, rodadaId, ct);
                if (comanda is null || rodada is null || rodada.Rodada.ComandaId != comandaId)
                {
                    return new ResultadoDeEscrita(comanda is null ? ResultadoDeRodada.ComandaNaoEncontrada : ResultadoDeRodada.RodadaNaoEncontrada);
                }

                if (rodada.Rodada.EnviadaPor != request.UserId)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.SomenteQuemEnviou);
                }

                if (rodada.Rodada.DesfeitaEm is not null)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.RodadaJaDesfeita);
                }

                var agora = RegrasDeRodada.Microssegundos(clock.UtcNow);
                var janela = (int)(await settings.ResolveAsync(ComandaSettings.JanelaDesfazerSegundos, org.OrganizationId, org.UnitId, agora, ct)).AsInteger();
                if (!RegrasDeRodada.DentroDaJanela(rodada.Rodada.EnviadaEm, agora, janela))
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.JanelaExpirada);
                }

                // Undo is a write by the SENDER inside a short window, not an edit of what they last saw: it does not compare versions, so a second device launching meanwhile cannot turn it into 409.
                // The bump takes the comanda row lock FIRST; everything that depends on the items and payments is checked on data read after the lock, so a concurrent cancel or payment cannot slip through.
                if (!await comandas.AvancarVersaoSemConferirAsync(org.OrganizationId, comandaId, ct))
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.ComandaNaoAberta);
                }

                var atual = await rodadas.AcharRodadaAsync(org.OrganizationId, rodadaId, ct);
                var comandaAtual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
                if (atual is null || comandaAtual is null)
                {
                    throw new RecusaDoDesfazer(ResultadoDeRodada.RodadaNaoEncontrada);
                }

                if (atual.Rodada.DesfeitaEm is not null)
                {
                    throw new RecusaDoDesfazer(ResultadoDeRodada.RodadaJaDesfeita);
                }

                if (atual.Itens.Any(i => i.Estado == EstadoDoItem.Cancelado))
                {
                    throw new RecusaDoDesfazer(ResultadoDeRodada.RodadaComItemCancelado);
                }

                if (await DeixariaAbaixoDoRecebidoAsync(request, comandaAtual, atual.Itens, ct))
                {
                    throw new RecusaDoDesfazer(ResultadoDeRodada.ValorJaRecebido);
                }

                if (!await rodadas.DesfazerRodadaAsync(org.OrganizationId, rodadaId, RegrasDeRodada.MotivoDesfeito, request.UserId, agora, ct))
                {
                    throw new RecusaDoDesfazer(ResultadoDeRodada.RodadaJaDesfeita);
                }

                var novaVersao = (await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct))!.Versao;
                var depois = await rodadas.AcharRodadaAsync(org.OrganizationId, rodadaId, ct);
                var audit_ = new JsonObject { ["itens"] = rodada.Itens.Count, ["motivo"] = RegrasDeRodada.MotivoDesfeito, ["versao"] = novaVersao };
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.DesfazerRodada, ObjectType = "rodada", ObjectId = rodadaId.ToString("D"), After = audit_ }, ct);
                await eventos.PublicarAsync(org, new RodadaDesfeita(EntityId.New().Value, comandaId, rodadaId, comanda.UnidadeId, request.UserId), ct);
                return new ResultadoDeEscrita(ResultadoDeRodada.Ok, depois, Versao: novaVersao);
            },
            cancellationToken);
    }

    public Task<ResultadoDeEscrita> CancelarItemAsync(IRequestContext request, Guid comandaId, Guid itemId, int versao, string? motivo, string? texto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var palavra = RegrasDeRodada.MotivoDeItem(motivo, texto);
                if (palavra is null || versao < 1)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.Invalido);
                }

                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.ComandaNaoEncontrada);
                }

                var item = await rodadas.AcharItemAsync(org.OrganizationId, itemId, ct);
                if (item is null || item.ComandaId != comandaId)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.ItemNaoEncontrado);
                }

                if (item.Estado == EstadoDoItem.Cancelado)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.ItemJaCancelado);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.ComandaNaoAberta);
                }

                if (await DeixariaAbaixoDoRecebidoAsync(request, comanda, [item], ct))
                {
                    return new ResultadoDeEscrita(ResultadoDeRodada.ValorJaRecebido);
                }

                if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                {
                    return await FalhaDeVersaoAsync(org, comandaId, ct);
                }

                var quando = RegrasDeRodada.Microssegundos(clock.UtcNow);
                if (!await rodadas.CancelarItemAsync(org.OrganizationId, itemId, palavra, request.UserId, quando, ct))
                {
                    throw new InvalidOperationException("O item mudou durante o cancelamento.");
                }

                var depois = new JsonObject { ["produto"] = item.ProdutoNome, ["quantidade"] = item.Quantidade, ["motivo"] = palavra, ["versao"] = versao + 1 };
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.CancelarItem, ObjectType = "item-comanda", ObjectId = itemId.ToString("D"), After = depois }, ct);
                await eventos.PublicarAsync(org, new ItemCancelado(EntityId.New().Value, comandaId, itemId, comanda.UnidadeId, palavra, request.UserId), ct);
                var cancelado = item with { Estado = EstadoDoItem.Cancelado, MotivoCancelamento = palavra, CanceladoPor = request.UserId, CanceladoEm = quando };
                return new ResultadoDeEscrita(ResultadoDeRodada.Ok, Item: cancelado, Versao: versao + 1);
            },
            cancellationToken);
    }

    /// <summary>Items of one comanda (all states), for the caller who may see it; null when the comanda is not theirs to see.</summary>
    public Task<IReadOnlyList<ItemDaComanda>?> ItensAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<ItemDaComanda>?>(
            request,
            async ct => await AcharVisivelAsync(request, comandaId, ct) is null ? null : await rodadas.ListarItensAsync(request.Organization.OrganizationId, comandaId, ct),
            cancellationToken);
    }

    /// <summary>True when cancelling <paramref name="retirados"/> would leave the bill below what the payments already applied (the balance would go negative and the comanda could never close).</summary>
    private async Task<bool> DeixariaAbaixoDoRecebidoAsync(IRequestContext request, Comanda comanda, IReadOnlyList<ItemDaComanda> retirados, CancellationToken ct)
    {
        var pago = await pagamentos.SomaAplicadaAsync(request, comanda.Id, ct);
        if (pago == 0)
        {
            return false;
        }

        var itens = await rodadas.ListarItensAsync(request.Organization.OrganizationId, comanda.Id, ct);
        var ids = retirados.Select(i => i.Id).ToHashSet();
        var depois = RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens.Where(i => !ids.Contains(i.Id))), comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida, pago);
        return depois.SaldoCentavos < 0;
    }

    private static ResultadoDeEscrita Repetida(RodadaComItens existente, Guid comandaId) =>
        existente.Rodada.ComandaId == comandaId
            ? new ResultadoDeEscrita(ResultadoDeRodada.Repetida, existente, Versao: existente.Rodada.ComandaVersao)
            : new ResultadoDeEscrita(ResultadoDeRodada.Invalido);

    private async Task<Comanda?> AcharVisivelAsync(IRequestContext request, Guid comandaId, CancellationToken ct)
    {
        var org = request.Organization;
        var comanda = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
        if (comanda is null || comanda.GarcomId == request.UserId)
        {
            return comanda;
        }

        return await permissoes.PodeAsync(request, ComandaPermissions.VerComandasDeOutros, ct) ? comanda : null;
    }

    private async Task<ResultadoDeEscrita> FalhaDeVersaoAsync(OrganizationContext org, Guid comandaId, CancellationToken ct)
    {
        var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
        return atual is null || atual.Estado != EstadoDaComanda.Aberta
            ? new ResultadoDeEscrita(ResultadoDeRodada.ComandaNaoAberta)
            : new ResultadoDeEscrita(ResultadoDeRodada.VersaoDesatualizada, VersaoAtual: atual.Versao);
    }
}
