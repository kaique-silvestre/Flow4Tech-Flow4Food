using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDaDivisaoDaConta
{
    Ok,

    /// <summary>The request is malformed for its mode, or the ids are empty.</summary>
    Invalido,

    /// <summary>Free values do not add up to the bill, or percents do not add up to 100.</summary>
    SomaDiferente,

    /// <summary>In the item mode some launched item is in no part.</summary>
    ItensForaDasPartes,

    /// <summary>Not in the unit of the session (or another organization's), or not the caller's to see.</summary>
    ComandaNaoEncontrada,

    ComandaNaoAberta,

    VersaoDesatualizada,

    /// <summary>The comanda already received a payment: the bill was not split before charging.</summary>
    ValorJaRecebido,

    /// <summary>The client id of the split already belongs to another comanda.</summary>
    IdEmUso,

    /// <summary>The comanda has no recorded split.</summary>
    SemDivisao,

    /// <summary>The comanda changed after the split: it no longer matches the bill and must be done again.</summary>
    DivisaoInvalida,
}

/// <summary>A split as computed (preview) or recorded: <see cref="Divisao"/> is null for a preview. <see cref="Vigente"/> says the recorded split still matches the comanda.</summary>
public sealed record ResultadoDaDivisaoDaContaCompleto(
    ResultadoDaDivisaoDaConta Resultado,
    ContaDaComanda? Conta = null,
    IReadOnlyList<ParteDaDivisao>? Partes = null,
    DivisaoGravada? Divisao = null,
    bool Vigente = true,
    int? ComandaVersao = null,
    int? VersaoAtual = null,
    bool Repetida = false);

/// <summary>Thrown inside the unit of work to undo the version bump when a rule refuses the split; the public method turns it back into the result.</summary>
public sealed class DivisaoRecusadaException : InvalidOperationException
{
    public DivisaoRecusadaException()
        : this(new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.Invalido))
    {
    }

    public DivisaoRecusadaException(string message)
        : base(message)
    {
        Resultado = new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.Invalido);
    }

    public DivisaoRecusadaException(string message, Exception innerException)
        : base(message, innerException)
    {
        Resultado = new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.Invalido);
    }

    public DivisaoRecusadaException(ResultadoDaDivisaoDaContaCompleto resultado)
        : base("Divisao recusada.")
    {
        Resultado = resultado;
    }

    public ResultadoDaDivisaoDaContaCompleto Resultado { get; }
}

/// <summary>
/// Splitting the bill of an open comanda (the route has already asked the engine for <c>comanda:dividir</c>). The preview only reads: it computes the parts with the pure <see cref="RegrasDeDivisao"/> and writes
/// nothing. Recording is ONE unit of work: the comanda version is bumped against what the screen saw (the lock), the state is read after it, the split and its parts are written, with the audit and the event.
/// The client id makes it idempotent (a repeat answers the recorded split, no new effect). A split is valid only while the comanda still matches the fingerprint taken at recording
/// (<see cref="RegrasDeDivisao.Assinatura"/>); a later change of items, discounts or fee rule invalidates it. A comanda that already received a payment is not split.
/// </summary>
public sealed class GestaoDaDivisaoDaConta(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IRodadasStore rodadas,
    IDescontosStore descontos,
    IDivisoesStore divisoes,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    GestaoDePagamentos pagamentos,
    IClock clock)
{
    /// <summary>The parts the request would give, with nothing written (repeating it gives the same answer while the comanda is the same).</summary>
    public Task<ResultadoDaDivisaoDaContaCompleto> PreviaAsync(IRequestContext request, Guid comandaId, PedidoDeDivisao pedido, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.ComandaNaoEncontrada);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.ComandaNaoAberta);
                }

                var (itens, dados, pago) = await EstadoAsync(request, comandaId, ct);
                var (resultado, conta, partes) = Calcular(comanda, itens, dados, pago, pedido);
                return resultado == ResultadoDaDivisaoDaConta.Ok
                    ? new ResultadoDaDivisaoDaContaCompleto(resultado, conta, partes, ComandaVersao: comanda.Versao)
                    : new ResultadoDaDivisaoDaContaCompleto(resultado);
            },
            cancellationToken);
    }

    /// <summary>Records the split. <paramref name="divisaoId"/> is generated by the client; a repeat of it answers what was recorded.</summary>
    public async Task<ResultadoDaDivisaoDaContaCompleto> DividirAsync(IRequestContext request, Guid comandaId, Guid divisaoId, int versao, PedidoDeDivisao pedido, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await unitOfWork.ExecuteAsync(request, ct => GravarAsync(request, comandaId, divisaoId, versao, pedido, ct), cancellationToken);
        }
        catch (DivisaoRecusadaException recusada)
        {
            return recusada.Resultado;
        }
    }

    /// <summary>The latest split of the comanda: <c>SemDivisao</c> when none, <c>DivisaoInvalida</c> (with the stored split) when the comanda changed since.</summary>
    public Task<ResultadoDaDivisaoDaContaCompleto> VigenteAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.ComandaNaoEncontrada);
                }

                var ultima = await divisoes.UltimaDaComandaAsync(request.Organization.OrganizationId, comandaId, ct);
                if (ultima is null)
                {
                    return new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.SemDivisao);
                }

                var (itens, dados, _) = await EstadoAsync(request, comandaId, ct);
                var vigente = ultima.Assinatura == RegrasDeDivisao.Assinatura(itens, dados, comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida);
                return new ResultadoDaDivisaoDaContaCompleto(vigente ? ResultadoDaDivisaoDaConta.Ok : ResultadoDaDivisaoDaConta.DivisaoInvalida, Divisao: ultima, Partes: ultima.Partes, Vigente: vigente, ComandaVersao: comanda.Versao);
            },
            cancellationToken);
    }

    private static (ResultadoDaDivisaoDaConta Resultado, ContaDaComanda Conta, IReadOnlyList<ParteDaDivisao> Partes) Calcular(Comanda comanda, IReadOnlyList<ItemDaComanda> itens, IReadOnlyList<Desconto> dados, long pago, PedidoDeDivisao pedido)
    {
        var descontoTotal = RegrasDeDesconto.Calcular(itens, dados).TotalCentavos;
        var conta = RegrasDeComanda.Conta(RegrasDeRodada.Consumo(itens), comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida, pago, descontoTotal);
        if (pago > 0)
        {
            return (ResultadoDaDivisaoDaConta.ValorJaRecebido, conta, []);
        }

        var divisao = RegrasDeDivisao.Dividir(pedido, itens, conta);
        var resultado = divisao.Recusa switch
        {
            RecusaDeDivisao.Nenhuma => ResultadoDaDivisaoDaConta.Ok,
            RecusaDeDivisao.SomaDiferente => ResultadoDaDivisaoDaConta.SomaDiferente,
            RecusaDeDivisao.ItensForaDasPartes => ResultadoDaDivisaoDaConta.ItensForaDasPartes,
            _ => ResultadoDaDivisaoDaConta.Invalido,
        };
        return (resultado, conta, divisao.Partes);
    }

    private async Task<ResultadoDaDivisaoDaContaCompleto> GravarAsync(IRequestContext request, Guid comandaId, Guid divisaoId, int versao, PedidoDeDivisao pedido, CancellationToken ct)
    {
        if (divisaoId == Guid.Empty || versao < 1 || pedido is null)
        {
            return new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.Invalido);
        }

        var org = request.Organization;
        var comanda = await AcharVisivelAsync(request, comandaId, ct);
        if (comanda is null)
        {
            return new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.ComandaNaoEncontrada);
        }

        var existente = await divisoes.AcharAsync(org.OrganizationId, divisaoId, ct);
        if (existente is not null)
        {
            return Repetida(existente, comanda);
        }

        if (comanda.Estado != EstadoDaComanda.Aberta)
        {
            return new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.ComandaNaoAberta);
        }

        if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
        {
            // Lost the race on the row lock (or the caller is stale). A concurrent repeat of THIS id is not a conflict: it gets the stored split.
            var gravada = await divisoes.AcharAsync(org.OrganizationId, divisaoId, ct);
            if (gravada is not null)
            {
                return Repetida(gravada, comanda);
            }

            var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
            return atual is null || atual.Estado != EstadoDaComanda.Aberta
                ? new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.ComandaNaoAberta)
                : new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.VersaoDesatualizada, VersaoAtual: atual.Versao);
        }

        var (itens, dados, pago) = await EstadoAsync(request, comandaId, ct);
        var (resultado, conta, partes) = Calcular(comanda, itens, dados, pago, pedido);
        if (resultado != ResultadoDaDivisaoDaConta.Ok)
        {
            throw new DivisaoRecusadaException(new ResultadoDaDivisaoDaContaCompleto(resultado));
        }

        var nova = new DivisaoGravada(
            divisaoId,
            comandaId,
            comanda.UnidadeId,
            pedido.Modo,
            versao + 1,
            RegrasDeDivisao.Assinatura(itens, dados, comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida),
            conta.TotalCentavos,
            conta.TaxaServicoCentavos,
            pedido.GorjetaCentavos,
            request.UserId,
            RegrasDeRodada.Microssegundos(clock.UtcNow),
            partes);
        if (!await divisoes.InserirAsync(org.OrganizationId, nova, ct))
        {
            throw new DivisaoRecusadaException(new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.IdEmUso));
        }

        await audit.RecordAsync(
            request,
            new AuditEntry
            {
                Action = ComandasAuditActions.DividirConta,
                ObjectType = "comanda",
                ObjectId = comandaId.ToString("D"),
                Before = new JsonObject { ["versao"] = versao },
                After = new JsonObject
                {
                    ["comandaNumero"] = comanda.Numero,
                    ["divisaoId"] = divisaoId.ToString("D"),
                    ["modo"] = RegrasDeDivisao.Palavra(pedido.Modo),
                    ["partes"] = partes.Count,
                    ["totalCentavos"] = conta.TotalCentavos,
                    ["gorjetaCentavos"] = pedido.GorjetaCentavos,
                    ["versao"] = versao + 1,
                },
            },
            ct);
        await eventos.PublicarAsync(org, new ComandaDividida(EntityId.New().Value, comandaId, comanda.UnidadeId, comanda.Numero, divisaoId, RegrasDeDivisao.Palavra(pedido.Modo), partes.Count, conta.TotalCentavos, pedido.GorjetaCentavos, request.UserId), ct);
        return new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.Ok, conta, partes, nova, true, versao + 1);
    }

    /// <summary>A repeat of a recorded id answers the stored split (never a second one); the id of ANOTHER comanda is refused.</summary>
    private static ResultadoDaDivisaoDaContaCompleto Repetida(DivisaoGravada gravada, Comanda comanda) =>
        gravada.ComandaId == comanda.Id
            ? new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.Ok, Partes: gravada.Partes, Divisao: gravada, ComandaVersao: gravada.ComandaVersao, Repetida: true)
            : new ResultadoDaDivisaoDaContaCompleto(ResultadoDaDivisaoDaConta.IdEmUso);

    private async Task<(IReadOnlyList<ItemDaComanda> Itens, IReadOnlyList<Desconto> Descontos, long Pago)> EstadoAsync(IRequestContext request, Guid comandaId, CancellationToken ct)
    {
        var org = request.Organization;
        return (await rodadas.ListarItensAsync(org.OrganizationId, comandaId, ct), await descontos.ListarAsync(org.OrganizationId, comandaId, ct), await pagamentos.SomaAplicadaAsync(request, comandaId, ct));
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
}
