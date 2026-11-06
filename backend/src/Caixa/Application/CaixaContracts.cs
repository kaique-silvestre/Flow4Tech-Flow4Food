using Flow4Tech.Caixa.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Caixa.Application;

[PermissionCatalog]
public static class CaixaPermissions
{
    /// <summary>See the own shift (the open one and the history).</summary>
    public const string Ver = "caixa:ver";

    /// <summary>Open a shift with a float.</summary>
    public const string Abrir = "caixa:abrir";

    /// <summary>Close the own shift counting the cash.</summary>
    public const string Fechar = "caixa:fechar";

    /// <summary>See the shifts of every operator of the unit.</summary>
    public const string VerTodos = "caixa:ver-todos";
}

[AuditCatalog]
public static class CaixaAuditActions
{
    public const string Abrir = "caixa:abrir";

    public const string Fechar = "caixa:fechar";
}

[SettingsCatalog]
public static class CaixaSettings
{
    /// <summary>When on, cash is received only while the operator has an open shift. Read by the payment flow (Pagamentos); declared here with the shift.</summary>
    public static readonly SettingDefinition ExigirCaixaAberto = new(
        "caixa.exigir-caixa-aberto",
        SettingType.Booleano,
        "true",
        SettingLayers.OrganizacaoEUnidade,
        "Exige turno de caixa aberto para receber dinheiro.");
}

public sealed record CaixaAberto(Guid EventId, Guid TurnoId, Guid UnidadeId, Guid OperadorId, long FundoCentavos) : IDomainEvent
{
    public const string Type = "caixa.aberto";

    public string EventType => Type;
}

public sealed record CaixaFechado(Guid EventId, Guid TurnoId, Guid UnidadeId, Guid OperadorId, long ContadoCentavos, long EsperadoCentavos, long DiferencaCentavos) : IDomainEvent
{
    public const string Type = "caixa.fechado";

    public string EventType => Type;
}

/// <summary>Port: the shift rows of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface ICaixaStore
{
    /// <summary>Inserts the open shift; false (nothing written) when the operator already has one open in the unit. Safe against two simultaneous opens.</summary>
    Task<bool> TentarAbrirAsync(Guid organizationId, Turno turno, CancellationToken cancellationToken);

    Task<Turno?> AcharAbertoAsync(Guid organizationId, Guid unitId, Guid operadorId, CancellationToken cancellationToken);

    /// <summary>The shift by id (any state); null when there is none in the organization.</summary>
    Task<Turno?> AcharAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);

    /// <summary>
    /// Closes an open shift with the counted cash; false when it was no longer open. The expected cash (<c>fundo + recebido em dinheiro</c>) and the difference are computed by the database in the
    /// same statement, so a cash payment committed an instant earlier is always counted.
    /// </summary>
    Task<bool> FecharAsync(Guid organizationId, Guid turnoId, DateTimeOffset fechadoEm, long contadoCentavos, CancellationToken cancellationToken);

    /// <summary>Shifts of the unit, most recently opened first; only <paramref name="operadorId"/>'s when given.</summary>
    Task<IReadOnlyList<Turno>> ListarAsync(Guid organizationId, Guid unitId, Guid? operadorId, int limite, CancellationToken cancellationToken);
}

/// <summary>
/// Public contract of the register for other modules (Pagamentos): which shift the operator has open, and recording cash received in it. Both run inside the caller's unit of work.
/// </summary>
public interface IMovimentosDoCaixa
{
    /// <summary>The id of the operator's open shift in the unit, or null.</summary>
    Task<Guid?> TurnoAbertoAsync(Guid organizationId, Guid unitId, Guid operadorId, CancellationToken cancellationToken);

    /// <summary>Adds cash applied to a comanda to the expected amount of an OPEN shift; false (nothing written) when the shift is no longer open.</summary>
    Task<bool> RegistrarRecebimentoEmDinheiroAsync(Guid organizationId, Guid turnoId, long centavos, CancellationToken cancellationToken);
}

/// <summary>Port: domain events of the register, written to the outbox on the unit of work in progress.</summary>
public interface IEventosDoCaixa
{
    Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken);
}

/// <summary>Port: asks the one authorization engine for the caller (adapted in Api). Used for decisions that change WHAT is returned, not whether the route runs.</summary>
public interface IPermissaoDoOperador
{
    Task<bool> PodeAsync(IRequestContext request, string permission, CancellationToken cancellationToken);
}
