using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Pagamentos.Application;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDoComprovante
{
    Ok,

    /// <summary>The bill was already requested: the stored mark is answered and nothing is written.</summary>
    Repetida,

    /// <summary>Not in the unit of the session (or another organization's), or not the caller's to see.</summary>
    ComandaNaoEncontrada,

    /// <summary>The pre-bill and the request of the bill are for an open comanda.</summary>
    ComandaNaoAberta,

    /// <summary>The receipt is for a closed comanda (an open, cancelled or merged one has none).</summary>
    ComandaNaoFechada,
}

/// <summary>The document (<see cref="Comprovante"/> is null on a refusal) or the comanda with its mark (<see cref="Comanda"/>, for the bill request).</summary>
public sealed record ResultadoDoComprovanteCompleto(ResultadoDoComprovante Resultado, Comprovante? Comprovante = null, Comanda? Comanda = null);

/// <summary>
/// The customer's bill: the pre-bill of an open comanda, the second copy of the receipt of a closed one (both data and plain text, no fiscal document, no printing) and the "bill requested" mark that puts the
/// table in "waiting for the bill". The documents are built by the pure <see cref="RegrasDeComprovante"/> from the stored comanda; the route has already asked the engine for its permission. The mark is ONE
/// unit of work (the row, the audit record and the outbox event); it is idempotent, changes no bill and no version, and the comanda leaves it when it closes or is cancelled. A copy of the receipt is audited.
/// </summary>
public sealed class GestaoDoComprovante(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IRodadasStore rodadas,
    IDescontosStore descontos,
    IDivisoesStore divisoes,
    GestaoDePagamentos pagamentos,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    IClock clock)
{
    public Task<ResultadoDoComprovanteCompleto> PreContaAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.ComandaNaoEncontrada);
                }

                return comanda.Estado == EstadoDaComanda.Aberta
                    ? new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.Ok, await MontarAsync(request, TipoDeComprovante.PreConta, comanda, ct))
                    : new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.ComandaNaoAberta);
            },
            cancellationToken);
    }

    /// <summary>The second copy of the receipt of a closed comanda, with one audit record for the copy.</summary>
    public Task<ResultadoDoComprovanteCompleto> ReciboAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.ComandaNaoEncontrada);
                }

                if (comanda.Estado != EstadoDaComanda.Fechada)
                {
                    return new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.ComandaNaoFechada);
                }

                var comprovante = await MontarAsync(request, TipoDeComprovante.Recibo, comanda, ct);
                var json = new JsonObject { ["comandaNumero"] = comanda.Numero, ["totalCentavos"] = comprovante.Conta.TotalCentavos };
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.ReimprimirRecibo, ObjectType = "comanda", ObjectId = comandaId.ToString("D"), After = json }, ct);
                return new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.Ok, comprovante);
            },
            cancellationToken);
    }

    /// <summary>Marks the open comanda as "bill requested". Asking again answers the stored mark and writes nothing (not even when two requests race: the loser re-reads what the winner wrote).</summary>
    public Task<ResultadoDoComprovanteCompleto> PedirContaAsync(IRequestContext request, Guid comandaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.ComandaNaoEncontrada);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.ComandaNaoAberta);
                }

                if (comanda.ContaPedidaEm is not null)
                {
                    return new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.Repetida, Comanda: comanda);
                }

                var quando = RegrasDeRodada.Microssegundos(clock.UtcNow);
                if (!await comandas.MarcarContaPedidaAsync(org.OrganizationId, comandaId, request.UserId, quando, ct))
                {
                    // Lost a race: the other request marked it (repeat) or closed it first.
                    var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
                    return atual is { Estado: EstadoDaComanda.Aberta, ContaPedidaEm: not null }
                        ? new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.Repetida, Comanda: atual)
                        : new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.ComandaNaoAberta);
                }

                var json = new JsonObject { ["comandaNumero"] = comanda.Numero, ["contaPedidaEm"] = quando.ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
                await audit.RecordAsync(request, new AuditEntry { Action = ComandasAuditActions.PedirConta, ObjectType = "comanda", ObjectId = comandaId.ToString("D"), After = json }, ct);
                await eventos.PublicarAsync(org, new ContaPedida(EntityId.New().Value, comandaId, comanda.UnidadeId, comanda.Numero, comanda.MesaId, request.UserId), ct);
                return new ResultadoDoComprovanteCompleto(ResultadoDoComprovante.Ok, Comanda: comanda with { ContaPedidaEm = quando });
            },
            cancellationToken);
    }

    private async Task<Comprovante> MontarAsync(IRequestContext request, TipoDeComprovante tipo, Comanda comanda, CancellationToken ct)
    {
        var org = request.Organization;
        var itens = await rodadas.ListarItensAsync(org.OrganizationId, comanda.Id, ct);
        var dados = await descontos.ListarAsync(org.OrganizationId, comanda.Id, ct);
        var pagos = (await pagamentos.ListarDaComandaAsync(request, comanda.Id, ct))
            .Where(p => p.Estado == "confirmado")
            .Select(p => new PagamentoDoComprovante(p.MetodoNome, p.AplicadoCentavos, p.TrocoCentavos, p.GorjetaCentavos, p.RegistradoEm, p.DivisaoId, p.DivisaoParte))
            .ToList();
        var divisao = await divisoes.UltimaDaComandaAsync(org.OrganizationId, comanda.Id, ct);
        var vigente = divisao is not null && divisao.Assinatura == RegrasDeDivisao.Assinatura(itens, dados, comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida);
        return RegrasDeComprovante.Montar(tipo, comanda, itens, dados, pagos, vigente ? divisao!.Partes : null, vigente ? divisao!.Id : null, RegrasDeRodada.Microssegundos(clock.UtcNow));
    }

    /// <summary>The comanda when the caller may see it (their own, or any with <c>comanda:ver-comandas-de-outros</c>); null otherwise, so every answer is as for a comanda that does not exist.</summary>
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
