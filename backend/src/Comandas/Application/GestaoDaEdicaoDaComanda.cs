using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDaEdicao
{
    Ok,

    Invalido,

    /// <summary>No comanda of the unit with this id that the caller may see.</summary>
    ComandaNaoEncontrada,

    ComandaNaoAberta,

    ItemNaoEncontrado,

    ItemJaCancelado,

    /// <summary>The write carried a version other than the current one (the caller learns the current one).</summary>
    VersaoDesatualizada,

    /// <summary>Taking this away would put the bill below what was already received.</summary>
    ValorJaRecebido,

    /// <summary>The chosen waiter is not an active account of the unit (or is the one who already holds the comanda).</summary>
    GarcomInvalido,

    /// <summary>A comanda with a payment is never cancelled: money was received.</summary>
    ComPagamentos,
}

/// <summary><c>Comanda</c> is the comanda after the write (with its new version); <c>Item</c> is set by an item edit; <c>VersaoAtual</c> is set on a stale version.</summary>
public sealed record ResultadoDaEdicaoDaComanda(ResultadoDaEdicao Resultado, Comanda? Comanda = null, ItemDaComanda? Item = null, int? VersaoAtual = null, int ItensCancelados = 0);

/// <summary>
/// Everything a waiter or manager changes on an open comanda without receiving money: an item's quantity or note, the name, the responsible waiter, the head count and the general note, and cancelling
/// the whole comanda while it has no payment. The route has already asked the engine for the permission of the action. Every write is ONE unit of work: the version check, the change, the audit record
/// and the outbox event commit or roll back together, and a stale version changes nothing. Closing is a write of the same row against the same version, so a launch racing a close (or a cancel) loses the
/// version check: nothing is ever launched into a comanda that is closing. A waiter reaches only comandas they opened unless they hold <c>comanda:ver-comandas-de-outros</c>.
/// </summary>
public sealed class GestaoDaEdicaoDaComanda(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IRodadasStore rodadas,
    IGarconsDaUnidade garcons,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    GestaoDePagamentos pagamentos,
    IClock clock)
{
    /// <summary>Changes the quantity (1 or more; to remove an item cancel it) and the note of a launched item. The frozen price and options never change.</summary>
    public Task<ResultadoDaEdicaoDaComanda> AlterarItemAsync(IRequestContext request, Guid comandaId, Guid itemId, int versao, int quantidade, string? observacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (versao < 1 || !RegrasDeRodada.QuantidadeValida(quantidade) || !RegrasDeRodada.TentarObservacao(observacao, out var nota))
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.Invalido);
                }

                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ComandaNaoEncontrada);
                }

                var item = await rodadas.AcharItemAsync(org.OrganizationId, itemId, ct);
                if (item is null || item.ComandaId != comandaId)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ItemNaoEncontrado);
                }

                if (item.Estado == EstadoDoItem.Cancelado)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ItemJaCancelado);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ComandaNaoAberta);
                }

                var novo = item with { Quantidade = quantidade, Observacao = nota };
                if (quantidade < item.Quantidade && await DeixariaAbaixoDoRecebidoAsync(request, comanda, item, novo, ct))
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ValorJaRecebido);
                }

                if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                {
                    return await FalhaDeVersaoAsync(org, comandaId, ct);
                }

                if (!await rodadas.AlterarItemAsync(org.OrganizationId, itemId, quantidade, nota, ct))
                {
                    throw new InvalidOperationException("O item mudou durante a alteracao.");
                }

                var antes = new JsonObject { ["produto"] = item.ProdutoNome, ["quantidade"] = item.Quantidade, ["observacao"] = item.Observacao };
                var depois = new JsonObject { ["comandaNumero"] = comanda.Numero, ["produto"] = item.ProdutoNome, ["quantidade"] = quantidade, ["observacao"] = nota, ["versao"] = versao + 1 };
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.AlterarItem, ObjectType = "item-comanda", ObjectId = itemId.ToString("D"), Before = antes, After = depois }, ct);
                await eventos.PublicarAsync(org, new ItemAlterado(EntityId.New().Value, comandaId, itemId, comanda.UnidadeId, item.Quantidade, quantidade, request.UserId), ct);
                return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.Ok, comanda with { Versao = versao + 1 }, novo);
            },
            cancellationToken);
    }

    public Task<ResultadoDaEdicaoDaComanda> RenomearAsync(IRequestContext request, Guid comandaId, int versao, string? identificacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nome = RegrasDeComanda.Identificacao(identificacao);
        return EditarAsync(
            request,
            comandaId,
            versao,
            nome is null,
            (comanda, ct) => comandas.RenomearAsync(request.Organization.OrganizationId, comandaId, nome!, ct),
            comanda => (comanda with { Identificacao = nome! }, ComandasAuditActions.RenomearComanda, new JsonObject { ["identificacao"] = comanda.Identificacao }, new JsonObject { ["identificacao"] = nome }),
            cancellationToken);
    }

    public Task<ResultadoDaEdicaoDaComanda> TrocarGarcomAsync(IRequestContext request, Guid comandaId, int versao, Guid garcomId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EditarAsync(
            request,
            comandaId,
            versao,
            garcomId == Guid.Empty,
            (comanda, ct) => comandas.TrocarGarcomAsync(request.Organization.OrganizationId, comandaId, garcomId, ct),
            comanda => (comanda with { GarcomId = garcomId }, ComandasAuditActions.TrocarGarcom, new JsonObject { ["garcomId"] = comanda.GarcomId.ToString("D") }, new JsonObject { ["garcomId"] = garcomId.ToString("D") }),
            cancellationToken,
            async (comanda, ct) => comanda.GarcomId != garcomId && await garcons.PodeAtenderAsync(request.Organization.OrganizationId, comanda.UnidadeId, garcomId, ct));
    }

    /// <summary>Replaces the head count and the general note together (null clears each); the screen sends both.</summary>
    public Task<ResultadoDaEdicaoDaComanda> AlterarDadosAsync(IRequestContext request, Guid comandaId, int versao, int? pessoas, string? observacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var notaValida = RegrasDeComanda.TentarObservacao(observacao, out var nota);
        return EditarAsync(
            request,
            comandaId,
            versao,
            !notaValida || !RegrasDeComanda.PessoasValidas(pessoas),
            (comanda, ct) => comandas.AtualizarDadosAsync(request.Organization.OrganizationId, comandaId, pessoas, nota, ct),
            comanda => (comanda with { Pessoas = pessoas, Observacao = nota }, ComandasAuditActions.AlterarDados, new JsonObject { ["pessoas"] = comanda.Pessoas, ["observacao"] = comanda.Observacao }, new JsonObject { ["pessoas"] = pessoas, ["observacao"] = nota }),
            cancellationToken);
    }

    /// <summary>
    /// Cancels an open comanda with no payment: every item still launched is cancelled with the same reason and the comanda leaves the open state. The reason is one of the standard list
    /// ('outro' carries free text). A comanda with a payment answers <see cref="ResultadoDaEdicao.ComPagamentos"/>.
    /// </summary>
    public Task<ResultadoDaEdicaoDaComanda> CancelarAsync(IRequestContext request, Guid comandaId, int versao, string? motivo, string? texto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var palavra = RegrasDeRodada.MotivoDeItem(motivo, texto);
                if (palavra is null || versao < 1)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.Invalido);
                }

                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ComandaNaoEncontrada);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ComandaNaoAberta);
                }

                // A payment changes the comanda version, so a payment that committed after the caller last saw the comanda also fails the version check below.
                if (await pagamentos.SomaAplicadaAsync(request, comandaId, ct) > 0)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ComPagamentos);
                }

                if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                {
                    return await FalhaDeVersaoAsync(org, comandaId, ct);
                }

                var quando = RegrasDeRodada.Microssegundos(clock.UtcNow);
                var itens = await rodadas.CancelarItensVivosAsync(org.OrganizationId, comandaId, palavra, request.UserId, quando, ct);
                if (!await comandas.CancelarAsync(org.OrganizationId, comandaId, palavra, request.UserId, quando, ct))
                {
                    throw new InvalidOperationException("A comanda mudou durante o cancelamento.");
                }

                var depois = new JsonObject { ["comandaNumero"] = comanda.Numero, ["motivo"] = palavra, ["itensCancelados"] = itens, ["versao"] = versao + 1 };
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.CancelarComanda, ObjectType = "comanda", ObjectId = comandaId.ToString("D"), After = depois }, ct);
                await eventos.PublicarAsync(org, new ComandaCancelada(EntityId.New().Value, comandaId, comanda.UnidadeId, comanda.Numero, palavra, itens, request.UserId), ct);
                return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.Ok, comanda with { Estado = EstadoDaComanda.Cancelada, FechadaEm = quando, MotivoCancelamento = palavra, Versao = versao + 1 }, ItensCancelados: itens);
            },
            cancellationToken);
    }

    /// <summary>The shared shape of the small edits: validate, find, open, optional extra check, version bump, write, audit.</summary>
    private Task<ResultadoDaEdicaoDaComanda> EditarAsync(
        IRequestContext request,
        Guid comandaId,
        int versao,
        bool entradaInvalida,
        Func<Comanda, CancellationToken, Task<bool>> escrever,
        Func<Comanda, (Comanda Depois, string Acao, JsonObject Antes, JsonObject Apos)> descrever,
        CancellationToken cancellationToken,
        Func<Comanda, CancellationToken, Task<bool>>? permitido = null)
    {
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (entradaInvalida || versao < 1)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.Invalido);
                }

                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ComandaNaoEncontrada);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ComandaNaoAberta);
                }

                if (permitido is not null && !await permitido(comanda, ct))
                {
                    return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.GarcomInvalido);
                }

                if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                {
                    return await FalhaDeVersaoAsync(org, comandaId, ct);
                }

                if (!await escrever(comanda, ct))
                {
                    throw new InvalidOperationException("A comanda mudou durante a edicao.");
                }

                var (depois, acao, antes, apos) = descrever(comanda);
                antes["versao"] = versao;
                apos["comandaNumero"] = comanda.Numero;
                apos["versao"] = versao + 1;
                await audit.RecordAsync(request, new AuditEntry { Action = acao, ObjectType = "comanda", ObjectId = comandaId.ToString("D"), Before = antes, After = apos }, ct);
                return new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.Ok, depois with { Versao = versao + 1 });
            },
            cancellationToken);
    }

    /// <summary>True when the edited item would leave the bill below what the payments already applied.</summary>
    private async Task<bool> DeixariaAbaixoDoRecebidoAsync(IRequestContext request, Comanda comanda, ItemDaComanda antes, ItemDaComanda depois, CancellationToken ct)
    {
        var pago = await pagamentos.SomaAplicadaAsync(request, comanda.Id, ct);
        if (pago == 0)
        {
            return false;
        }

        var itens = (await rodadas.ListarItensAsync(request.Organization.OrganizationId, comanda.Id, ct)).Select(i => i.Id == antes.Id ? depois : i);
        return RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens), comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida, pago).SaldoCentavos < 0;
    }

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

    private async Task<ResultadoDaEdicaoDaComanda> FalhaDeVersaoAsync(OrganizationContext org, Guid comandaId, CancellationToken ct)
    {
        var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
        return atual is null || atual.Estado != EstadoDaComanda.Aberta
            ? new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.ComandaNaoAberta)
            : new ResultadoDaEdicaoDaComanda(ResultadoDaEdicao.VersaoDesatualizada, VersaoAtual: atual.Versao);
    }
}

/// <summary>The accounts of the active unit a comanda can be handed to (the route asks for <c>comanda:trocar-garcom</c>).</summary>
public sealed class ConsultaDeGarcons(IUnitOfWork unitOfWork, IGarconsDaUnidade garcons)
{
    public Task<IReadOnlyList<GarcomDaUnidade>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => garcons.ListarAsync(request.Organization.OrganizationId, request.Organization.UnitId, ct), cancellationToken);
    }
}

/// <summary>The timeline of a comanda for the caller who may see it, oldest first (the audit trail behind it, with who did it and when). Null when the comanda is not theirs to see.</summary>
public sealed class ConsultaDoHistoricoDaComanda(IUnitOfWork unitOfWork, IComandasStore comandas, IHistoricoDaComanda historico, IPermissaoDaComanda permissoes)
{
    public const int Limite = 500;

    public Task<IReadOnlyList<EventoDoHistorico>?> ListarAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<EventoDoHistorico>?>(
            request,
            async ct =>
            {
                var org = request.Organization;
                var comanda = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
                if (comanda is null || (comanda.GarcomId != request.UserId && !await permissoes.PodeAsync(request, ComandaPermissions.VerComandasDeOutros, ct)))
                {
                    return null;
                }

                return await historico.ListarAsync(org.OrganizationId, comandaId, Limite, ct);
            },
            cancellationToken);
    }
}
