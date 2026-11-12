using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDaTrocaDeMesa
{
    Ok,

    Invalido,

    /// <summary>The unit does not use tables.</summary>
    MesasDesligadas,

    /// <summary>No comanda of the unit with this id that the caller may see.</summary>
    ComandaNaoEncontrada,

    ComandaNaoAberta,

    /// <summary>No active table with this id in the unit (also the answer for a table of another unit).</summary>
    MesaNaoEncontrada,

    /// <summary>The comanda already holds that table.</summary>
    MesmaMesa,

    VersaoDesatualizada,
}

/// <summary><c>Comanda</c> is the comanda after the move (with its new version); <c>VersaoAtual</c> is set on a stale version.</summary>
public sealed record ResultadoDaTroca(ResultadoDaTrocaDeMesa Resultado, Comanda? Comanda = null, int? VersaoAtual = null);

/// <summary>
/// Moving an open comanda to another table of the unit. The route has already asked the engine for <c>comanda:transferir-mesa</c>. ONE unit of work: the version check, the move, the audit record
/// and the outbox event commit or roll back together, and a stale version changes nothing. Table and unit come from the session and the database, never from the client.
/// </summary>
public sealed class GestaoDaTrocaDeMesa(
    IUnitOfWork unitOfWork,
    IComandasStore comandas,
    IMesasStore mesas,
    IAuditLog audit,
    IEventosDeComandas eventos,
    IPermissaoDaComanda permissoes,
    ISettings settings,
    IClock clock)
{
    public Task<ResultadoDaTroca> TrocarAsync(IRequestContext request, Guid comandaId, Guid mesaId, int versao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (mesaId == Guid.Empty || versao < 1)
                {
                    return new ResultadoDaTroca(ResultadoDaTrocaDeMesa.Invalido);
                }

                var org = request.Organization;
                if ((await settings.ResolveAsync(OperacaoSettings.UsarMesas, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).Value != "true")
                {
                    return new ResultadoDaTroca(ResultadoDaTrocaDeMesa.MesasDesligadas);
                }

                var comanda = await AcharVisivelAsync(request, comandaId, ct);
                if (comanda is null)
                {
                    return new ResultadoDaTroca(ResultadoDaTrocaDeMesa.ComandaNaoEncontrada);
                }

                if (comanda.Estado != EstadoDaComanda.Aberta)
                {
                    return new ResultadoDaTroca(ResultadoDaTrocaDeMesa.ComandaNaoAberta);
                }

                if (comanda.MesaId == mesaId)
                {
                    return new ResultadoDaTroca(ResultadoDaTrocaDeMesa.MesmaMesa);
                }

                var nova = await mesas.AcharAtivaAsync(org.OrganizationId, org.UnitId, mesaId, ct);
                if (nova is null)
                {
                    return new ResultadoDaTroca(ResultadoDaTrocaDeMesa.MesaNaoEncontrada);
                }

                if (!await comandas.AvancarVersaoAsync(org.OrganizationId, comandaId, versao, ct))
                {
                    var atual = await comandas.AcharAsync(org.OrganizationId, org.UnitId, comandaId, ct);
                    return atual is null || atual.Estado != EstadoDaComanda.Aberta
                        ? new ResultadoDaTroca(ResultadoDaTrocaDeMesa.ComandaNaoAberta)
                        : new ResultadoDaTroca(ResultadoDaTrocaDeMesa.VersaoDesatualizada, VersaoAtual: atual.Versao);
                }

                var anterior = comanda.MesaId is { } antiga ? await mesas.AcharAsync(org.OrganizationId, org.UnitId, antiga, ct) : null;
                var identificacao = RegrasDeComanda.IdentificacaoAposTrocaDeMesa(comanda.Identificacao, anterior?.Identificacao, nova.Identificacao);
                if (!await comandas.TrocarMesaAsync(org.OrganizationId, comandaId, mesaId, identificacao, ct))
                {
                    throw new InvalidOperationException("A comanda mudou durante a troca de mesa.");
                }

                var entrada = new AuditEntry
                {
                    Action = ComandasAuditActions.TransferirMesa,
                    ObjectType = "comanda",
                    ObjectId = comandaId.ToString("D"),
                    Before = new JsonObject { ["mesaId"] = comanda.MesaId?.ToString("D"), ["identificacao"] = comanda.Identificacao, ["versao"] = versao },
                    After = new JsonObject { ["comandaNumero"] = comanda.Numero, ["mesaId"] = mesaId.ToString("D"), ["identificacao"] = identificacao, ["versao"] = versao + 1 },
                };
                await audit.RecordAsync(request, entrada, ct);
                await eventos.PublicarAsync(org, new ComandaTransferida(EntityId.New().Value, comandaId, comanda.UnidadeId, comanda.Numero, comanda.MesaId, mesaId, request.UserId), ct);
                return new ResultadoDaTroca(ResultadoDaTrocaDeMesa.Ok, comanda with { MesaId = mesaId, Identificacao = identificacao, Versao = versao + 1 });
            },
            cancellationToken);
    }

    /// <summary>The comanda when the caller may see it (their own, or any with <c>comanda:ver-comandas-de-outros</c>); null otherwise, so the move answers as for a comanda that does not exist.</summary>
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
