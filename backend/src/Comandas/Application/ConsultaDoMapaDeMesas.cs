using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Comandas.Application;

/// <summary>
/// The table map of the active unit (read only): active areas with their tables, each table with the status DERIVED from the open comandas, how many are open and when the first opened.
/// Who may call is the route's job (<c>mesa:ver</c>). When the unit turned tables off the map is empty, without error. The unit comes from the session, never from the client.
/// </summary>
public sealed class ConsultaDoMapaDeMesas(IUnitOfWork unitOfWork, IMesasStore store, ISettings settings, IClock clock)
{
    public Task<MapaDeMesas> ConsultarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                if ((await settings.ResolveAsync(OperacaoSettings.UsarMesas, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).Value != "true")
                {
                    return MapaDeMesas.Vazio;
                }

                var areas = await store.ListarAreasAtivasAsync(org.OrganizationId, org.UnitId, ct);
                var mesas = await store.ListarAtivasAsync(org.OrganizationId, org.UnitId, ct);
                var ocupacao = await store.OcupacaoAsync(org.OrganizationId, org.UnitId, ct);
                return RegrasDeMesa.MontarMapa(areas, mesas, ocupacao);
            },
            cancellationToken);
    }
}
