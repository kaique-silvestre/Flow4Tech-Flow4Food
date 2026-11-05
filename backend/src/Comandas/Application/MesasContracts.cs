using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Comandas.Application;

[PermissionCatalog]
public static class MesaPermissions
{
    /// <summary>List the tables of the unit.</summary>
    public const string Ver = "mesa:ver";

    /// <summary>Register tables.</summary>
    public const string Gerenciar = "mesa:gerenciar";
}

[AuditCatalog]
public static class ComandasAuditActions
{
    public const string CriarMesa = "mesa:criar";
}

[SettingsCatalog]
public static class OperacaoSettings
{
    /// <summary>When off, the unit works without tables: none is listed and none can be registered. Registered tables are kept.</summary>
    public static readonly SettingDefinition UsarMesas = new(
        "operacao.usar-mesas",
        SettingType.Booleano,
        "true",
        SettingLayers.OrganizacaoEUnidade,
        "Usa mesas no atendimento.");
}

/// <summary>Port: the table rows of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IMesasStore
{
    /// <summary>Inserts the table; false (nothing written) when the unit already has one with that identification. Safe against simultaneous inserts.</summary>
    Task<bool> TentarInserirAsync(Guid organizationId, Mesa mesa, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Active tables of the unit, ordered by identification.</summary>
    Task<IReadOnlyList<Mesa>> ListarAtivasAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken);
}
