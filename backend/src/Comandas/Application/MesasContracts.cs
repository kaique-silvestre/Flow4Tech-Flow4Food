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

    public const string CriarArea = "area:criar";

    public const string AlterarArea = "area:alterar";

    public const string DefinirAreaDaMesa = "mesa:definir-area";

    public const string TransferirMesa = "comanda:transferir-mesa";

    public const string TransferirItem = "comanda:transferir-item";

    public const string JuntarComanda = "comanda:juntar";

    public const string AbrirComanda = "comanda:abrir";

    public const string LancarRodada = "comanda:lancar-rodada";

    public const string DesfazerRodada = "comanda:desfazer-rodada";

    public const string CancelarItem = "comanda:cancelar-item";

    public const string AlterarItem = "comanda:alterar-item";

    public const string RenomearComanda = "comanda:renomear";

    public const string TrocarGarcom = "comanda:trocar-garcom";

    public const string AlterarDados = "comanda:alterar-dados";

    public const string CancelarComanda = "comanda:cancelar";

    public const string DarDesconto = "comanda:desconto";

    public const string DarCortesia = "comanda:cortesia";

    public const string DarBrinde = "comanda:brinde";

    public const string FecharComanda = "comanda:fechar";

    public const string RemoverTaxaServico = "comanda:remover-taxa-servico";
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

    /// <summary>The active table of the unit with this id, or null (unknown, retired or of another unit).</summary>
    Task<Mesa?> AcharAtivaAsync(Guid organizationId, Guid unitId, Guid mesaId, CancellationToken cancellationToken);

    /// <summary>The table of the unit with this id, retired or not; null when unknown or of another unit.</summary>
    Task<Mesa?> AcharAsync(Guid organizationId, Guid unitId, Guid mesaId, CancellationToken cancellationToken);

    /// <summary>Ids of the tables of the unit held by at least one open comanda.</summary>
    Task<IReadOnlySet<Guid>> OcupadasAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken);

    /// <summary>For each table of the unit held by an open comanda: how many, and when the first opened. Tables with none are absent.</summary>
    Task<IReadOnlyDictionary<Guid, OcupacaoDaMesa>> OcupacaoAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken);

    /// <summary>Active areas of the unit, ordered by name.</summary>
    Task<IReadOnlyList<Area>> ListarAreasAtivasAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken);

    /// <summary>The ACTIVE area of the unit with this id, or null (unknown, retired or of another unit).</summary>
    Task<Area?> AcharAreaAtivaAsync(Guid organizationId, Guid unitId, Guid areaId, CancellationToken cancellationToken);

    /// <summary>The area of the unit with this id, retired or not; null when unknown or of another unit.</summary>
    Task<Area?> AcharAreaAsync(Guid organizationId, Guid unitId, Guid areaId, CancellationToken cancellationToken);

    /// <summary>Inserts the area; false (nothing written) when the unit already has one with that name. Safe against simultaneous writers of the unit's areas.</summary>
    Task<bool> TentarInserirAreaAsync(Guid organizationId, Area area, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Renames and/or retires the area; false (nothing written) when another area of the unit already has the name. Safe against simultaneous writers of the unit's areas.</summary>
    Task<bool> TentarAtualizarAreaAsync(Guid organizationId, Guid unitId, Guid areaId, string nome, bool ativa, CancellationToken cancellationToken);

    /// <summary>Puts the active table in the area (or takes it out of any when null); false when the table is not an active one of the unit.</summary>
    Task<bool> DefinirAreaDaMesaAsync(Guid organizationId, Guid unitId, Guid mesaId, Guid? areaId, CancellationToken cancellationToken);
}
