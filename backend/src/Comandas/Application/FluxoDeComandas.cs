using System.Text.Json;
using Flow4Tech.Kernel;

namespace Flow4Tech.Comandas.Application;

/// <summary>Timing of the real-time stream. The stream polls the outbox of the session's unit (no in-memory hub, so any instance serves any client).</summary>
public sealed record FluxoOptions
{
    /// <summary>Pause between two reads of the outbox.</summary>
    public TimeSpan Intervalo { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A comment line sent when nothing else was, to keep proxies from closing an idle connection.</summary>
    public TimeSpan Batimento { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often the route checks the session and <c>comanda:ver</c> again; a failed check ends the stream.</summary>
    public TimeSpan Revalidacao { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The stream ends after this long, so the client reconnects and the session is checked again (a revoked session cannot hold a stream forever).</summary>
    public TimeSpan DuracaoMaxima { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Events whose commit is slower than a later one keep an earlier timestamp: each read goes this far back and skips what it already delivered.</summary>
    public TimeSpan Janela { get; init; } = TimeSpan.FromSeconds(10);

    public int LimiteDoLote { get; init; } = 200;

    /// <summary>How many open comandas a resync lists.</summary>
    public int LimiteDoResync { get; init; } = 500;
}

/// <summary>A comanda and its version: the source of order. A client that holds a lower version reloads the comanda.</summary>
public sealed record VersaoDeComanda(Guid ComandaId, int Versao);

/// <summary>An outbox event as the store reads it. <see cref="Payload"/> is the JSON the publisher wrote.</summary>
public sealed record EventoBrutoDoFluxo(Guid Id, string Tipo, string Payload, DateTimeOffset OcorridoEm);

/// <summary>
/// One event for the client: what happened (<see cref="Tipo"/>) and to which comandas, each with its version when the event was delivered (never lower than the version the event produced).
/// </summary>
public sealed record EventoDoFluxo(Guid Id, string Tipo, DateTimeOffset Em, IReadOnlyList<VersaoDeComanda> Comandas);

/// <summary>"Reload": the client may have missed events, so it gets the current version of every open comanda it may see and fetches what is newer than it has.</summary>
public sealed record ResyncDoFluxo(string Motivo, IReadOnlyList<VersaoDeComanda> Comandas);

/// <summary>Port: reads outbox events of the unit and comanda versions. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IFluxoDeComandasStore
{
    /// <summary>Comanda and payment events of the unit with <c>ocorrido_em &gt;= desde</c>, oldest first, skipping <paramref name="jaEntregues"/>.</summary>
    Task<IReadOnlyList<EventoBrutoDoFluxo>> LerAsync(Guid organizationId, Guid unitId, DateTimeOffset desde, IReadOnlyCollection<Guid> jaEntregues, int limite, CancellationToken cancellationToken);

    /// <summary>Current versions of the given comandas of the unit; with <paramref name="garcomId"/> only those that waiter holds.</summary>
    Task<IReadOnlyList<VersaoDeComanda>> VersoesAsync(Guid organizationId, Guid unitId, IReadOnlyCollection<Guid> comandaIds, Guid? garcomId, CancellationToken cancellationToken);

    /// <summary>Current versions of the open comandas of the unit; with <paramref name="garcomId"/> only those that waiter holds.</summary>
    Task<IReadOnlyList<VersaoDeComanda>> VersoesDasAbertasAsync(Guid organizationId, Guid unitId, Guid? garcomId, int limite, CancellationToken cancellationToken);
}

/// <summary>What one connection already delivered. Created by <see cref="FluxoDeComandas.AbrirAsync"/>, kept by the caller for the life of the connection.</summary>
public sealed class AssinaturaDoFluxo
{
    internal DateTimeOffset Desde { get; set; }

    internal Dictionary<Guid, DateTimeOffset> Entregues { get; } = [];
}

/// <summary>
/// The real-time stream of comanda changes of the session's unit (read only, no audit). Who may call is the route's job (<c>comanda:ver</c>); like the comanda list, a caller without
/// <c>comanda:ver-comandas-de-outros</c> only hears about the comandas they hold. Every read is its own short unit of work for the session's organization (no transaction is held while the stream idles).
/// There is no event history to replay: a client that reconnects gets a <see cref="ResyncDoFluxo"/> and reloads by version.
/// </summary>
public sealed class FluxoDeComandas(IUnitOfWork unitOfWork, IFluxoDeComandasStore store, IPermissaoDaComanda permissoes, IClock clock, FluxoOptions options)
{
    /// <summary>Starts a connection at "now" and always answers with a resync (<c>abertura</c>, or <c>reconexao</c> when the client says it saw events before): there is no history to replay.</summary>
    public async Task<(AssinaturaDoFluxo Assinatura, ResyncDoFluxo Resync)> AbrirAsync(IRequestContext request, bool reconectou, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var assinatura = new AssinaturaDoFluxo { Desde = clock.UtcNow - options.Janela };
        var resync = await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                // What happened before the connection is not "new": remember it so the look-back window never delivers it.
                Marcar(assinatura, await store.LerAsync(request.Organization.OrganizationId, request.Organization.UnitId, assinatura.Desde, [], options.LimiteDoLote, ct));
                var garcom = await SoAsDeleAsync(request, ct);
                return new ResyncDoFluxo(reconectou ? "reconexao" : "abertura", await store.VersoesDasAbertasAsync(request.Organization.OrganizationId, request.Organization.UnitId, garcom, options.LimiteDoResync, ct));
            },
            cancellationToken);
        return (assinatura, resync);
    }

    /// <summary>The events committed since the last read, oldest first, each with the versions of the comandas it touched. Empty when nothing new (or nothing the caller may see).</summary>
    public Task<IReadOnlyList<EventoDoFluxo>> LerAsync(IRequestContext request, AssinaturaDoFluxo assinatura, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(assinatura);
        return unitOfWork.ExecuteAsync<IReadOnlyList<EventoDoFluxo>>(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                var unit = request.Organization.UnitId;
                var lote = await store.LerAsync(org, unit, assinatura.Desde, [.. assinatura.Entregues.Keys], options.LimiteDoLote, ct);
                if (lote.Count == 0)
                {
                    return [];
                }

                var ids = lote.ToDictionary(e => e.Id, e => ComandasDoEvento(e.Payload));
                var versoes = (await store.VersoesAsync(org, unit, [.. ids.Values.SelectMany(v => v).Distinct()], await SoAsDeleAsync(request, ct), ct)).ToDictionary(v => v.ComandaId, v => v.Versao);
                Marcar(assinatura, lote);
                return
                [
                    .. lote
                        .Select(e => new EventoDoFluxo(e.Id, e.Tipo, e.OcorridoEm, [.. ids[e.Id].Where(versoes.ContainsKey).Select(c => new VersaoDeComanda(c, versoes[c]))]))
                        .Where(e => e.Comandas.Count > 0),
                ];
            },
            cancellationToken);
    }

    /// <summary>Which comandas an event touched: its comanda, or both ends of a transfer or merge. A payload with none of them touches no comanda.</summary>
    internal static IReadOnlyList<Guid> ComandasDoEvento(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var ids = new List<Guid>();
        foreach (var name in (string[])["ComandaId", "OrigemId", "DestinoId"])
        {
            if (doc.RootElement.TryGetProperty(name, out var value) && value.TryGetGuid(out var id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private void Marcar(AssinaturaDoFluxo assinatura, IReadOnlyList<EventoBrutoDoFluxo> lote)
    {
        foreach (var e in lote)
        {
            assinatura.Entregues[e.Id] = e.OcorridoEm;
        }

        if (lote.Count > 0)
        {
            assinatura.Desde = new[] { assinatura.Desde, lote.Max(e => e.OcorridoEm) - options.Janela }.Max();
        }

        foreach (var antigo in assinatura.Entregues.Where(p => p.Value < assinatura.Desde).Select(p => p.Key).ToList())
        {
            assinatura.Entregues.Remove(antigo);
        }
    }

    private async Task<Guid?> SoAsDeleAsync(IRequestContext request, CancellationToken cancellationToken) =>
        await permissoes.PodeAsync(request, ComandaPermissions.VerComandasDeOutros, cancellationToken) ? null : request.UserId;
}
