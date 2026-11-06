using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Comandas.Application;

[PermissionCatalog]
public static class ComandaPermissions
{
    /// <summary>Open a comanda.</summary>
    public const string Abrir = "comanda:abrir";

    /// <summary>List the comandas the caller opened.</summary>
    public const string Ver = "comanda:ver";

    /// <summary>See the comandas other waiters opened (without it the list holds only the caller's own).</summary>
    public const string VerComandasDeOutros = "comanda:ver-comandas-de-outros";
}

[SettingsCatalog]
public static class VendaSettings
{
    /// <summary>Service fee percentage of the unit. Frozen on each comanda when it opens, so a later change never reaches an open bill.</summary>
    public static readonly SettingDefinition TaxaServicoPercentual = new(
        "venda.taxa-servico-percentual",
        SettingType.Numero,
        "10",
        SettingLayers.OrganizacaoEUnidade,
        "Taxa de servico, em percentual do consumo (0 a 20).")
    {
        Minimum = 0,
        Maximum = RegrasDeComanda.TaxaMaxima,
    };
}

public sealed record ComandaAberta(Guid EventId, Guid ComandaId, Guid UnidadeId, int Numero, string Identificacao, Guid? MesaId, Guid GarcomId) : IDomainEvent
{
    public const string Type = "comanda.aberta";

    public string EventType => Type;
}

/// <summary>Port: the comanda rows of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IComandasStore
{
    /// <summary>Hands out the next display number of the unit on the operating day: one upsert that locks the counter row until the unit of work ends (unique, gap-free; a rollback gives it back).</summary>
    Task<int> ProximoNumeroAsync(Guid organizationId, Guid unitId, DateOnly dia, CancellationToken cancellationToken);

    Task InserirAsync(Guid organizationId, Comanda comanda, CancellationToken cancellationToken);

    /// <summary>Open comandas of the unit, newest first; only <paramref name="garcomId"/>'s when given.</summary>
    Task<IReadOnlyList<Comanda>> ListarAbertasAsync(Guid organizationId, Guid unitId, Guid? garcomId, int limite, CancellationToken cancellationToken);
}

/// <summary>Port: domain events of the comandas, written to the outbox on the unit of work in progress.</summary>
public interface IEventosDeComandas
{
    Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken);
}

/// <summary>Port: asks the one authorization engine for the caller (adapted in Api). Used for decisions that change WHAT is returned, not whether the route runs.</summary>
public interface IPermissaoDaComanda
{
    Task<bool> PodeAsync(IRequestContext request, string permission, CancellationToken cancellationToken);
}

/// <summary>Port: the unit's current OPERATING day (adapted in Api over the operating-day resolver; display numbers restart with it).</summary>
public interface IDiaOperacional
{
    Task<DateOnly> AtualAsync(OrganizationContext organization, CancellationToken cancellationToken);
}
