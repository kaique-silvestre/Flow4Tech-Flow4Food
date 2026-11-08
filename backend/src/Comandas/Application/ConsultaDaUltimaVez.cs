using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;

namespace Flow4Tech.Comandas.Application;

/// <summary>What a table ordered last time: the comanda it closed most recently and its items that stood (cancelled ones left out). <see cref="Comanda"/> is null (and the items empty) when there is none.</summary>
public sealed record UltimaVezNaMesa(Comanda? Comanda, IReadOnlyList<ItemDaComanda> Itens);

/// <summary>
/// "Last time at this table" (read only). Who may call is the route's job (<c>mesa:ver</c>); the unit comes from the session and the table must be an active one of that unit (else null: 404).
/// A waiter without <c>comanda:ver-comandas-de-outros</c> is shown only comandas they opened, the same visibility as the comanda list.
/// </summary>
public sealed class ConsultaDaUltimaVez(IUnitOfWork unitOfWork, IMesasStore mesas, IComandasStore comandas, IRodadasStore rodadas, IPermissaoDaComanda permissoes)
{
    /// <summary>Null when the table is not an active table of the active unit.</summary>
    public Task<UltimaVezNaMesa?> ConsultarAsync(IRequestContext request, Guid mesaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<UltimaVezNaMesa?>(
            request,
            async ct =>
            {
                var org = request.Organization;
                if (await mesas.AcharAtivaAsync(org.OrganizationId, org.UnitId, mesaId, ct) is null)
                {
                    return null;
                }

                var soAsDele = await permissoes.PodeAsync(request, ComandaPermissions.VerComandasDeOutros, ct) ? (Guid?)null : request.UserId;
                var ultima = await comandas.UltimaFechadaDaMesaAsync(org.OrganizationId, org.UnitId, mesaId, soAsDele, ct);
                if (ultima is null)
                {
                    return new UltimaVezNaMesa(null, []);
                }

                var itens = await rodadas.ListarItensAsync(org.OrganizationId, ultima.Id, ct);
                return new UltimaVezNaMesa(ultima, [.. itens.Where(i => i.Estado == EstadoDoItem.Lancado)]);
            },
            cancellationToken);
    }
}
