using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDoMovimento
{
    Ok,

    Invalido,

    /// <summary>One of the comandas is not in the unit of the session (or another organization's) or is not the caller's to see.</summary>
    ComandaNaoEncontrada,

    /// <summary>One of the comandas is not open.</summary>
    ComandaNaoAberta,

    ItemNaoEncontrado,

    ItemJaCancelado,

    ItemComDesconto,

    ComDesconto,

    ValorJaRecebido,

    /// <summary>One of the two versions is not the current one; <c>ComandaId</c> and <c>VersaoAtual</c> say which and what it is now.</summary>
    VersaoDesatualizada,
}

/// <summary>
/// <c>Origem</c> and <c>Destino</c> are the two comandas after the write (new versions). On a stale version <c>ComandaId</c> is the one that was stale and <c>VersaoAtual</c> its current version.
/// <c>ItensMovidos</c> is how many items changed comanda.
/// </summary>
public sealed record ResultadoDoMovimentoDeItens(ResultadoDoMovimento Resultado, Comanda? Origem = null, Comanda? Destino = null, int ItensMovidos = 0, Guid? ComandaId = null, int? VersaoAtual = null);

/// <summary>Thrown inside the unit of work to undo the version bumps already made when a rule refuses the move; the public methods turn it back into the result. Never leaves <see cref="GestaoDaTransferenciaDeItens"/>.</summary>
public sealed class MovimentoRecusadoException : InvalidOperationException
{
    public MovimentoRecusadoException()
        : this(new ResultadoDoMovimentoDeItens(ResultadoDoMovimento.Invalido))
    {
    }

    public MovimentoRecusadoException(string message)
        : base(message)
    {
        Resultado = new ResultadoDoMovimentoDeItens(ResultadoDoMovimento.Invalido);
    }

    public MovimentoRecusadoException(string message, Exception innerException)
        : base(message, innerException)
    {
        Resultado = new ResultadoDoMovimentoDeItens(ResultadoDoMovimento.Invalido);
    }

    public MovimentoRecusadoException(ResultadoDoMovimentoDeItens resultado)
        : base("Movimento recusado.")
    {
        Resultado = resultado;
    }

    public ResultadoDoMovimentoDeItens Resultado { get; }
}

/// <summary>
/// Items moving between comandas: transfer to another open comanda of the unit, merge one comanda into another, separate selected items into a NEW comanda. The route has already asked the engine for
/// <c>comanda:transferir-item</c> or <c>comanda:juntar</c>. Each operation is ONE unit of work. The two comandas are locked in id order (the version bump is the lock) so two opposite moves queue instead of
/// deadlocking; both versions go up; a stale version on either changes nothing and answers with the current one. The rules (what may move) are read AFTER the locks, from the state nobody else can change
/// now. Unit and organization come from the session: a comanda of another unit or organization is simply not found. The caller must be able to see both comandas.
/// </summary>
public sealed class GestaoDaTransferenciaDeItens(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IRodadasStore rodadas,
    IDescontosStore descontos,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    IDiaOperacional dias,
    GestaoDePagamentos pagamentos,
    IClock clock)
{
    /// <summary>Moves the selected launched items from the origin to another open comanda of the unit.</summary>
    public Task<ResultadoDoMovimentoDeItens> TransferirAsync(IRequestContext request, Guid origemId, int origemVersao, Guid destinoId, int destinoVersao, IReadOnlyCollection<Guid>? itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecutarAsync(
            request,
            async ct =>
            {
                if (origemVersao < 1 || destinoVersao < 1 || origemId == destinoId || !RegrasDeTransferencia.SelecaoValida(itemIds))
                {
                    return Falha(ResultadoDoMovimento.Invalido);
                }

                var (origem, destino, erro) = await AcharAbertasAsync(request, origemId, destinoId, ct);
                if (erro is not null)
                {
                    return erro;
                }

                await TravarAsync(request, [(origemId, origemVersao), (destinoId, destinoVersao)], ct);
                var (itens, dados, pago) = await EstadoAsync(request, origemId, ct);
                Recusar(RegrasDeTransferencia.Itens(itemIds!, itens, dados, origem!.TaxaServicoPercentual, origem.TaxaServicoRemovida, pago));

                var movidos = await MoverAsync(request, itemIds!, destinoId, destinoVersao + 1, ct);
                var valor = itens.Where(i => itemIds!.Contains(i.Id)).Sum(i => i.ValorDeTabelaCentavos);
                await RegistrarAsync(request, ComandasAuditActions.TransferirItem, origem, origemVersao, destino!, destinoVersao, movidos, ct);
                await eventos.PublicarAsync(request.Organization, new ItensTransferidos(EntityId.New().Value, origem.UnidadeId, origemId, origem.Numero, destinoId, destino!.Numero, false, [.. itemIds!], valor, request.UserId), ct);
                return new ResultadoDoMovimentoDeItens(ResultadoDoMovimento.Ok, origem with { Versao = origemVersao + 1 }, destino with { Versao = destinoVersao + 1 }, movidos);
            },
            cancellationToken);
    }

    /// <summary>Merges <paramref name="origemId"/> into <paramref name="destinoId"/>: its launched items move there and it closes as merged, with no balance.</summary>
    public Task<ResultadoDoMovimentoDeItens> JuntarAsync(IRequestContext request, Guid destinoId, int destinoVersao, Guid origemId, int origemVersao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecutarAsync(
            request,
            async ct =>
            {
                if (origemVersao < 1 || destinoVersao < 1 || origemId == destinoId)
                {
                    return Falha(ResultadoDoMovimento.Invalido);
                }

                var (origem, destino, erro) = await AcharAbertasAsync(request, origemId, destinoId, ct);
                if (erro is not null)
                {
                    return erro;
                }

                await TravarAsync(request, [(origemId, origemVersao), (destinoId, destinoVersao)], ct);
                var (itens, dados, pago) = await EstadoAsync(request, origemId, ct);
                Recusar(RegrasDeTransferencia.Juncao(itens, dados, pago));

                var ids = RegrasDeTransferencia.IdsParaJuntar(itens);
                var movidos = ids.Count == 0 ? 0 : await MoverAsync(request, ids, destinoId, destinoVersao + 1, ct);
                if (!await comandas.JuntarAsync(request.Organization.OrganizationId, origemId, destinoId, request.UserId, RegrasDeRodada.Microssegundos(clock.UtcNow), ct))
                {
                    throw new InvalidOperationException("A comanda mudou durante a juncao.");
                }

                await RegistrarAsync(request, ComandasAuditActions.JuntarComanda, origem!, origemVersao, destino!, destinoVersao, movidos, ct);
                await eventos.PublicarAsync(request.Organization, new ComandaJuntada(EntityId.New().Value, origem!.UnidadeId, origemId, origem.Numero, destinoId, destino!.Numero, movidos, request.UserId), ct);
                return new ResultadoDoMovimentoDeItens(ResultadoDoMovimento.Ok, origem with { Estado = EstadoDaComanda.Juntada, Versao = origemVersao + 1 }, destino with { Versao = destinoVersao + 1 }, movidos);
            },
            cancellationToken);
    }

    /// <summary>
    /// Separates the selected items of the origin into a NEW comanda of the same unit, table, waiter and service fee, named <paramref name="identificacao"/> (null: "Comanda N"). The new comanda is born at
    /// version 2 (opened, then received the items), the origin goes up by one.
    /// </summary>
    public Task<ResultadoDoMovimentoDeItens> SepararAsync(IRequestContext request, Guid origemId, int origemVersao, IReadOnlyCollection<Guid>? itemIds, string? identificacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecutarAsync(
            request,
            async ct =>
            {
                var nome = identificacao is null ? null : RegrasDeComanda.Identificacao(identificacao);
                if (origemVersao < 1 || !RegrasDeTransferencia.SelecaoValida(itemIds) || (identificacao is not null && nome is null))
                {
                    return Falha(ResultadoDoMovimento.Invalido);
                }

                var org = request.Organization;
                var origem = await AcharVisivelAsync(request, origemId, ct);
                if (origem is null)
                {
                    return Falha(ResultadoDoMovimento.ComandaNaoEncontrada, origemId);
                }

                if (origem.Estado != EstadoDaComanda.Aberta)
                {
                    return Falha(ResultadoDoMovimento.ComandaNaoAberta, origemId);
                }

                await TravarAsync(request, [(origemId, origemVersao)], ct);
                var (itens, dados, pago) = await EstadoAsync(request, origemId, ct);
                Recusar(RegrasDeTransferencia.Itens(itemIds!, itens, dados, origem.TaxaServicoPercentual, origem.TaxaServicoRemovida, pago));

                var dia = await dias.AtualAsync(org, ct);
                var numero = await comandas.ProximoNumeroAsync(org.OrganizationId, org.UnitId, dia, ct);
                var nova = new Comanda(
                    EntityId.New().Value,
                    origem.UnidadeId,
                    dia,
                    numero,
                    nome ?? RegrasDeComanda.IdentificacaoPadrao(numero),
                    origem.MesaId,
                    origem.Canal,
                    origem.GarcomId,
                    null,
                    EstadoDaComanda.Aberta,
                    RegrasDeRodada.Microssegundos(clock.UtcNow),
                    null,
                    origem.TaxaServicoPercentual,
                    origem.TaxaServicoRemovida,
                    2);
                await comandas.InserirAsync(org.OrganizationId, nova, ct);
                var movidos = await MoverAsync(request, itemIds!, nova.Id, nova.Versao, ct);
                var valor = itens.Where(i => itemIds!.Contains(i.Id)).Sum(i => i.ValorDeTabelaCentavos);

                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.AbrirComanda, ObjectType = "comanda", ObjectId = nova.Id.ToString("D"), After = new JsonObject { ["numero"] = numero, ["identificacao"] = nova.Identificacao, ["separadaDaComandaId"] = origemId.ToString("D") } }, ct);
                await RegistrarAsync(request, ComandasAuditActions.TransferirItem, origem, origemVersao, nova, 1, movidos, ct);
                await eventos.PublicarAsync(org, new ComandaAberta(EntityId.New().Value, nova.Id, nova.UnidadeId, numero, nova.Identificacao, nova.MesaId, nova.GarcomId), ct);
                await eventos.PublicarAsync(org, new ItensTransferidos(EntityId.New().Value, origem.UnidadeId, origemId, origem.Numero, nova.Id, numero, true, [.. itemIds!], valor, request.UserId), ct);
                return new ResultadoDoMovimentoDeItens(ResultadoDoMovimento.Ok, origem with { Versao = origemVersao + 1 }, nova, movidos);
            },
            cancellationToken);
    }

    private async Task<ResultadoDoMovimentoDeItens> ExecutarAsync(IRequestContext request, Func<CancellationToken, Task<ResultadoDoMovimentoDeItens>> trabalho, CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteAsync(request, trabalho, cancellationToken);
        }
        catch (MovimentoRecusadoException recusado)
        {
            return recusado.Resultado;
        }
    }

    private static ResultadoDoMovimentoDeItens Falha(ResultadoDoMovimento resultado, Guid? comandaId = null) => new(resultado, ComandaId: comandaId);

    /// <summary>Both comandas of the unit, visible to the caller and open; otherwise the answer that says which one is the problem. Reads without locking.</summary>
    private async Task<(Comanda? Origem, Comanda? Destino, ResultadoDoMovimentoDeItens? Erro)> AcharAbertasAsync(IRequestContext request, Guid origemId, Guid destinoId, CancellationToken ct)
    {
        var origem = await AcharVisivelAsync(request, origemId, ct);
        if (origem is null)
        {
            return (null, null, Falha(ResultadoDoMovimento.ComandaNaoEncontrada, origemId));
        }

        var destino = await AcharVisivelAsync(request, destinoId, ct);
        if (destino is null)
        {
            return (null, null, Falha(ResultadoDoMovimento.ComandaNaoEncontrada, destinoId));
        }

        if (origem.Estado != EstadoDaComanda.Aberta)
        {
            return (null, null, Falha(ResultadoDoMovimento.ComandaNaoAberta, origemId));
        }

        return destino.Estado != EstadoDaComanda.Aberta ? (null, null, Falha(ResultadoDoMovimento.ComandaNaoAberta, destinoId)) : (origem, destino, null);
    }

    /// <summary>Locks the comandas in id order by bumping their versions against what the caller saw; the first stale one undoes everything and answers with its current version.</summary>
    private async Task TravarAsync(IRequestContext request, (Guid Id, int Versao)[] alvos, CancellationToken ct)
    {
        var org = request.Organization;
        foreach (var (id, versao) in alvos.OrderBy(a => a.Id))
        {
            if (await comandas.AvancarVersaoAsync(org.OrganizationId, id, versao, ct))
            {
                continue;
            }

            var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, id, ct);
            throw new MovimentoRecusadoException(atual is null || atual.Estado != EstadoDaComanda.Aberta
                ? Falha(ResultadoDoMovimento.ComandaNaoAberta, id)
                : new ResultadoDoMovimentoDeItens(ResultadoDoMovimento.VersaoDesatualizada, ComandaId: id, VersaoAtual: atual.Versao));
        }
    }

    private async Task<(IReadOnlyList<ItemDaComanda> Itens, IReadOnlyList<Desconto> Descontos, long Pago)> EstadoAsync(IRequestContext request, Guid comandaId, CancellationToken ct)
    {
        var org = request.Organization;
        return (await rodadas.ListarItensAsync(org.OrganizationId, comandaId, ct), await descontos.ListarAsync(org.OrganizationId, comandaId, ct), await pagamentos.SomaAplicadaAsync(request, comandaId, ct));
    }

    private static void Recusar(RecusaDeMovimento recusa)
    {
        var resultado = recusa switch
        {
            RecusaDeMovimento.Nenhuma => (ResultadoDoMovimento?)null,
            RecusaDeMovimento.ItemNaoEncontrado => ResultadoDoMovimento.ItemNaoEncontrado,
            RecusaDeMovimento.ItemJaCancelado => ResultadoDoMovimento.ItemJaCancelado,
            RecusaDeMovimento.ItemComDesconto => ResultadoDoMovimento.ItemComDesconto,
            RecusaDeMovimento.ComDesconto => ResultadoDoMovimento.ComDesconto,
            RecusaDeMovimento.ValorJaRecebido => ResultadoDoMovimento.ValorJaRecebido,
            _ => ResultadoDoMovimento.Invalido,
        };
        if (resultado is { } r)
        {
            throw new MovimentoRecusadoException(Falha(r));
        }
    }

    private async Task<int> MoverAsync(IRequestContext request, IReadOnlyCollection<Guid> itemIds, Guid destinoId, int destinoVersao, CancellationToken ct)
    {
        var movidos = await rodadas.MoverItensAsync(request.Organization.OrganizationId, itemIds, destinoId, destinoVersao, ct);
        return movidos == itemIds.Count ? movidos : throw new InvalidOperationException("Um item mudou durante a movimentacao.");
    }

    /// <summary>One audit record on EACH comanda (so each timeline tells it), in the same unit of work.</summary>
    private async Task RegistrarAsync(IRequestContext request, string acao, Comanda origem, int origemVersao, Comanda destino, int destinoVersao, int itens, CancellationToken ct)
    {
        await audit.RecordAsync(request, Entrada(acao, origem, origemVersao, destino, itens, "origem"), ct);
        await audit.RecordAsync(request, Entrada(acao, destino, destinoVersao, origem, itens, "destino"), ct);
    }

    private static AuditEntry Entrada(string acao, Comanda esta, int versao, Comanda outra, int itens, string papel) => new()
    {
        Action = acao,
        ObjectType = "comanda",
        ObjectId = esta.Id.ToString("D"),
        Before = new JsonObject { ["versao"] = versao },
        After = new JsonObject { ["comandaNumero"] = esta.Numero, ["papel"] = papel, ["outraComandaId"] = outra.Id.ToString("D"), ["outraComandaNumero"] = outra.Numero, ["itens"] = itens, ["versao"] = versao + 1 },
    };

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
}
