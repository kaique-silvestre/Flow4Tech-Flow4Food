using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDoDesconto
{
    Ok,

    Invalido,

    /// <summary>No comanda of the unit with this id that the caller may see.</summary>
    ComandaNaoEncontrada,

    ComandaNaoAberta,

    ItemNaoEncontrado,

    ItemJaCancelado,

    /// <summary>The item is already a courtesy or a gift.</summary>
    ItemNaoNormal,

    /// <summary>The discount is larger than what is still chargeable (the bill never goes below zero).</summary>
    DescontoExcede,

    /// <summary>The bill would fall below what was already received.</summary>
    ValorJaRecebido,

    VersaoDesatualizada,

    /// <summary>The engine refused (not held, or beyond the limit or the daily cap). Answered as 403 without reason.</summary>
    Negado,
}

/// <summary><c>Comanda</c> carries the version after the write; <c>Conta</c> the bill after it; <c>VersaoAtual</c> is set on a stale version.</summary>
public sealed record ResultadoDeDescontoDaComanda(ResultadoDoDesconto Resultado, Comanda? Comanda = null, ItemDaComanda? Item = null, Desconto? Desconto = null, ContaDaComanda? Conta = null, int? VersaoAtual = null);

/// <summary>Raised inside the unit of work when the engine refuses AFTER the version bump: the bump and any daily cap reserved roll back and the caller answers <see cref="ResultadoDoDesconto.Negado"/>.</summary>
public sealed class DecisaoDeDescontoNegadaException : InvalidOperationException
{
    public DecisaoDeDescontoNegadaException()
        : base("Decisao negada pelo motor.")
    {
    }

    public DecisaoDeDescontoNegadaException(string message)
        : base(message)
    {
    }

    public DecisaoDeDescontoNegadaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Whether the caller holds ANY rule for each of the three actions. No limit, no value: only yes or no.</summary>
public sealed record RegrasDeDescontoDoOperador(bool Desconto, bool Cortesia, bool Brinde);

/// <summary>Ends the visibility question's unit of work by rolling it back (a capped rule would reserve zero cents); always carries the answer. Never leaves <see cref="GestaoDeDescontos"/>.</summary>
public sealed class ConsultaDeRegrasConcluidaException : InvalidOperationException
{
    public ConsultaDeRegrasConcluidaException()
        : this(new RegrasDeDescontoDoOperador(false, false, false))
    {
    }

    public ConsultaDeRegrasConcluidaException(string message)
        : base(message)
    {
        Resultado = new RegrasDeDescontoDoOperador(false, false, false);
    }

    public ConsultaDeRegrasConcluidaException(string message, Exception innerException)
        : base(message, innerException)
    {
        Resultado = new RegrasDeDescontoDoOperador(false, false, false);
    }

    public ConsultaDeRegrasConcluidaException(RegrasDeDescontoDoOperador resultado)
        : base("question finished (rolled back on purpose)")
    {
        Resultado = resultado;
    }

    public RegrasDeDescontoDoOperador Resultado { get; }
}

/// <summary>
/// Discounts, courtesies and gifts. The route has already asked for the plain permission; the LIMITED one (<c>comanda:desconto|cortesia|brinde</c>) is asked of the engine here, inside the unit of work and AFTER the
/// comanda row lock (the version bump), with the percent and the value computed from the stored comanda, never from the request. A refusal throws, so the bump and the daily cap the engine reserved roll back together;
/// the denial is then audited on its own transaction (<c>acesso:negar</c>) and answered as 403 without reason. Calculation order and rounding: ADR-018.
/// </summary>
public sealed class GestaoDeDescontos(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IRodadasStore rodadas,
    IDescontosStore descontos,
    IDecisaoDeDesconto decisao,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    GestaoDePagamentos pagamentos,
    IClock clock)
{
    /// <summary>Gives a discount of <paramref name="percentual"/> or of <paramref name="valorCentavos"/> (exactly one), on an item or, with no item, on the whole comanda.</summary>
    public async Task<ResultadoDeDescontoDaComanda> AplicarAsync(IRequestContext request, Guid comandaId, int versao, Guid? itemId, decimal? percentual, long? valorCentavos, string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    var umSo = (percentual is null) != (valorCentavos is null);
                    if (versao < 1 || !umSo || (percentual is { } p && !RegrasDeDesconto.PercentualValido(p)) || (valorCentavos is { } v && !RegrasDeDesconto.ValorValido(v)) || !MotivoValido(motivo, out var texto))
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.Invalido);
                    }

                    var org = request.Organization;
                    var comanda = await AcharVisivelAsync(request, comandaId, ct);
                    if (comanda is null)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ComandaNaoEncontrada);
                    }

                    if (comanda.Estado != EstadoDaComanda.Aberta)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ComandaNaoAberta);
                    }

                    var itens = await rodadas.ListarItensAsync(org.OrganizationId, comandaId, ct);
                    var dados = await descontos.ListarAsync(org.OrganizationId, comandaId, ct);
                    long baseCentavos;
                    if (itemId is { } alvo)
                    {
                        var item = itens.FirstOrDefault(i => i.Id == alvo);
                        if (item is null)
                        {
                            return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ItemNaoEncontrado);
                        }

                        if (item.Estado == EstadoDoItem.Cancelado)
                        {
                            return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ItemJaCancelado);
                        }

                        baseCentavos = RegrasDeDesconto.BaseDoItem(item, dados);
                    }
                    else
                    {
                        baseCentavos = RegrasDeDesconto.BaseDaComanda(itens.ToList(), dados.ToList());
                    }

                    var aplicado = percentual is { } pedido ? RegrasDeDesconto.DoPercentual(baseCentavos, pedido) : valorCentavos!.Value;
                    if (aplicado < 1 || aplicado > baseCentavos)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.DescontoExcede);
                    }

                    var novo = new Desconto(
                        EntityId.New().Value,
                        comandaId,
                        itemId,
                        percentual is null ? FormaDoDesconto.Valor : FormaDoDesconto.Percentual,
                        percentual,
                        valorCentavos,
                        aplicado,
                        texto!,
                        request.UserId,
                        RegrasDeRodada.Microssegundos(clock.UtcNow));
                    var depois = await ContaDepoisAsync(request, comanda, itens, [.. dados, novo], ct);
                    if (depois.SaldoCentavos < 0)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ValorJaRecebido);
                    }

                    if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                    {
                        return await FalhaDeVersaoAsync(org, comandaId, ct);
                    }

                    // The question comes after the row lock and measures the SERVER's numbers: the percent asked (or, for a value, the real share of the base) and the cents taken.
                    var medido = percentual ?? RegrasDeDesconto.PercentualDoValor(aplicado, baseCentavos);
                    if (!await decisao.PodeAsync(request, ComandaPermissions.Desconto, medido, aplicado, ct))
                    {
                        throw new DecisaoDeDescontoNegadaException();
                    }

                    await descontos.InserirAsync(org.OrganizationId, comanda.UnidadeId, novo, ct);
                    var json = new JsonObject
                    {
                        ["comandaNumero"] = comanda.Numero,
                        ["itemId"] = itemId?.ToString("D"),
                        ["forma"] = RegrasDeDesconto.Palavra(novo.Forma),
                        ["percentual"] = percentual,
                        ["valorCentavos"] = valorCentavos,
                        ["aplicadoCentavos"] = aplicado,
                        ["motivo"] = texto,
                        ["versao"] = versao + 1,
                    };
                    await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.DarDesconto, ObjectType = "comanda", ObjectId = comandaId.ToString("D"), After = json }, ct);
                    await eventos.PublicarAsync(org, new DescontoAplicado(EntityId.New().Value, comandaId, novo.Id, comanda.UnidadeId, itemId, RegrasDeDesconto.Palavra(novo.Forma), percentual, aplicado, texto!, request.UserId), ct);
                    return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.Ok, comanda with { Versao = versao + 1 }, Desconto: novo, Conta: depois);
                },
                cancellationToken);
        }
        catch (DecisaoDeDescontoNegadaException)
        {
            await RegistrarNegacaoAsync(request, comandaId, ComandaPermissions.Desconto, cancellationToken);
            return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.Negado);
        }
    }

    /// <summary>
    /// Does the caller hold ANY rule for discount, courtesy and gift? Asks the engine for the smallest possible operation (zero percent, zero cents): it is allowed exactly when some rule of the permission applies,
    /// whatever its limits. It runs in its own unit of work, rolled back at the end, so a daily cap is never reserved. The answer carries no limit; each real operation is still decided with the real numbers.
    /// </summary>
    public async Task<RegrasDeDescontoDoOperador> RegrasDoOperadorAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await unitOfWork.ExecuteAsync<object?>(
                request,
                async ct =>
                {
                    var desconto = await decisao.PodeAsync(request, ComandaPermissions.Desconto, 0m, 0, ct);
                    var cortesia = await decisao.PodeAsync(request, ComandaPermissions.Cortesia, 0m, 0, ct);
                    var brinde = await decisao.PodeAsync(request, ComandaPermissions.Brinde, 0m, 0, ct);
                    throw new ConsultaDeRegrasConcluidaException(new RegrasDeDescontoDoOperador(desconto, cortesia, brinde));
                },
                cancellationToken);
        }
        catch (ConsultaDeRegrasConcluidaException feito)
        {
            return feito.Resultado;
        }

        throw new InvalidOperationException("The question must end by rolling back.");
    }

    /// <summary>Gives a launched item as a courtesy: charge zero, original price kept, with a reason.</summary>
    public Task<ResultadoDeDescontoDaComanda> DarCortesiaAsync(IRequestContext request, Guid comandaId, Guid itemId, int versao, string? motivo, CancellationToken cancellationToken = default) =>
        DarComoAsync(request, comandaId, itemId, versao, motivo, TipoDoItem.Cortesia, cancellationToken);

    /// <summary>Gives a launched item as a gift: charge zero, original price kept, with a reason.</summary>
    public Task<ResultadoDeDescontoDaComanda> DarBrindeAsync(IRequestContext request, Guid comandaId, Guid itemId, int versao, string? motivo, CancellationToken cancellationToken = default) =>
        DarComoAsync(request, comandaId, itemId, versao, motivo, TipoDoItem.Brinde, cancellationToken);

    /// <summary>The discounts of a comanda in the order they were given, for the caller who may see it; null when it is not theirs to see.</summary>
    public Task<IReadOnlyList<Desconto>?> ListarAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<Desconto>?>(
            request,
            async ct => await AcharVisivelAsync(request, comandaId, ct) is null ? null : await descontos.ListarAsync(request.Organization.OrganizationId, comandaId, ct),
            cancellationToken);
    }

    private async Task<ResultadoDeDescontoDaComanda> DarComoAsync(IRequestContext request, Guid comandaId, Guid itemId, int versao, string? motivo, TipoDoItem tipo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var permissao = tipo == TipoDoItem.Cortesia ? ComandaPermissions.Cortesia : ComandaPermissions.Brinde;
        try
        {
            return await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    if (versao < 1 || !MotivoValido(motivo, out var texto))
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.Invalido);
                    }

                    var org = request.Organization;
                    var comanda = await AcharVisivelAsync(request, comandaId, ct);
                    if (comanda is null)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ComandaNaoEncontrada);
                    }

                    var itens = await rodadas.ListarItensAsync(org.OrganizationId, comandaId, ct);
                    var item = itens.FirstOrDefault(i => i.Id == itemId);
                    if (item is null)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ItemNaoEncontrado);
                    }

                    if (item.Estado == EstadoDoItem.Cancelado)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ItemJaCancelado);
                    }

                    if (comanda.Estado != EstadoDaComanda.Aberta)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ComandaNaoAberta);
                    }

                    if (item.Tipo != TipoDoItem.Normal)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ItemNaoNormal);
                    }

                    var dados = await descontos.ListarAsync(org.OrganizationId, comandaId, ct);
                    var novoItem = item with { Tipo = tipo, MotivoDoTipo = texto };
                    var depois = await ContaDepoisAsync(request, comanda, itens.Select(i => i.Id == itemId ? novoItem : i).ToList(), dados, ct);
                    if (depois.SaldoCentavos < 0)
                    {
                        return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ValorJaRecebido);
                    }

                    if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                    {
                        return await FalhaDeVersaoAsync(org, comandaId, ct);
                    }

                    // Giving the whole line away is 100 percent of what it would have charged.
                    if (!await decisao.PodeAsync(request, permissao, RegrasDeDesconto.PercentualMaximo, item.ValorDeTabelaCentavos, ct))
                    {
                        throw new DecisaoDeDescontoNegadaException();
                    }

                    var quando = RegrasDeRodada.Microssegundos(clock.UtcNow);
                    if (!await rodadas.DefinirTipoAsync(org.OrganizationId, itemId, tipo, texto!, request.UserId, quando, ct))
                    {
                        throw new InvalidOperationException("O item mudou durante a operacao.");
                    }

                    var palavra = RegrasDeDesconto.Palavra(tipo);
                    var antes = new JsonObject { ["tipo"] = "normal", ["versao"] = versao };
                    var json = new JsonObject
                    {
                        ["comandaNumero"] = comanda.Numero,
                        ["produto"] = item.ProdutoNome,
                        ["quantidade"] = item.Quantidade,
                        ["valorDeTabelaCentavos"] = item.ValorDeTabelaCentavos,
                        ["tipo"] = palavra,
                        ["motivo"] = texto,
                        ["versao"] = versao + 1,
                    };
                    await audit.RecordAsync(request, new AuditEntry { Action = tipo == TipoDoItem.Cortesia ? ComandasAuditActions.DarCortesia : ComandasAuditActions.DarBrinde, ObjectType = "item-comanda", ObjectId = itemId.ToString("D"), Before = antes, After = json }, ct);
                    IDomainEvent evento = tipo == TipoDoItem.Cortesia
                        ? new ItemCortesia(EntityId.New().Value, comandaId, itemId, comanda.UnidadeId, item.ProdutoId, item.ProdutoNome, item.Quantidade, item.ValorDeTabelaCentavos, texto!, request.UserId)
                        : new ItemBrinde(EntityId.New().Value, comandaId, itemId, comanda.UnidadeId, item.ProdutoId, item.ProdutoNome, item.Quantidade, item.ValorDeTabelaCentavos, texto!, request.UserId);
                    await eventos.PublicarAsync(org, evento, ct);
                    return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.Ok, comanda with { Versao = versao + 1 }, novoItem, Conta: depois);
                },
                cancellationToken);
        }
        catch (DecisaoDeDescontoNegadaException)
        {
            await RegistrarNegacaoAsync(request, comandaId, permissao, cancellationToken);
            return new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.Negado);
        }
    }

    private static bool MotivoValido(string? motivo, out string? texto) => RegrasDeRodada.TentarObservacao(motivo, out texto) && texto is not null;

    /// <summary>The bill after the change, for the "balance never below what was paid" check and for the answer.</summary>
    private async Task<ContaDaComanda> ContaDepoisAsync(IRequestContext request, Comanda comanda, IReadOnlyList<ItemDaComanda> itens, IReadOnlyList<Desconto> dados, CancellationToken ct)
    {
        var pago = await pagamentos.SomaAplicadaAsync(request, comanda.Id, ct);
        return RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens), comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida, pago, RegrasDeDesconto.Calcular(itens, dados).TotalCentavos);
    }

    /// <summary>A refusal by the engine is a security fact: it is written on its own transaction after the unit of work rolled back (<c>acesso:negar</c>, result <c>negado</c>, the permission only, never the reason).</summary>
    private Task RegistrarNegacaoAsync(IRequestContext request, Guid comandaId, string permissao, CancellationToken ct) =>
        audit.RecordDetachedAsync(
            request,
            new AuditEntry
            {
                Action = PlatformAuditActions.AcessoNegar,
                Result = AuditResult.Negado,
                ObjectType = "comanda",
                ObjectId = comandaId.ToString("D"),
                After = new JsonObject { ["permissao"] = permissao },
            },
            ct);

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

    private async Task<ResultadoDeDescontoDaComanda> FalhaDeVersaoAsync(OrganizationContext org, Guid comandaId, CancellationToken ct)
    {
        var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
        return atual is null || atual.Estado != EstadoDaComanda.Aberta
            ? new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.ComandaNaoAberta)
            : new ResultadoDeDescontoDaComanda(ResultadoDoDesconto.VersaoDesatualizada, VersaoAtual: atual.Versao);
    }
}
