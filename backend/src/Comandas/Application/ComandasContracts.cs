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

    /// <summary>Launch a round of items into a comanda.</summary>
    public const string LancarItem = "comanda:lancar-item";

    /// <summary>Cancel one item of a comanda (with a reason). Sensitive: a denial is audited.</summary>
    public const string CancelarItem = "comanda:cancelar-item";

    /// <summary>Undo a whole round inside the undo window. Sensitive: a denial is audited.</summary>
    public const string DesfazerRodada = "comanda:desfazer-rodada";

    /// <summary>Receive a cash payment for a comanda (the cashier). The payment itself is also gated by <c>pagamento:registrar</c>.</summary>
    public const string ReceberPagamento = "comanda:receber-pagamento";

    /// <summary>Close a comanda whose balance is zero.</summary>
    public const string Fechar = "comanda:fechar";

    /// <summary>Remove the service fee of an open comanda. Sensitive: a denial is audited, and so is the removal.</summary>
    public const string RemoverTaxaServico = "comanda:remover-taxa-servico";
}

[SettingsCatalog]
public static class ComandaSettings
{
    /// <summary>How long after sending, in seconds, the waiter who sent a round may undo it. Measured on the server clock; system layer only.</summary>
    public static readonly SettingDefinition JanelaDesfazerSegundos = new(
        "comanda.janela-desfazer-segundos",
        SettingType.Inteiro,
        "8",
        SettingLayers.SomenteSistema,
        "Janela, em segundos, para desfazer uma rodada logo apos o envio.")
    {
        Minimum = 1,
        Maximum = 60,
    };
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

    /// <summary>The comanda of the unit by id, any state; null when there is none in the unit.</summary>
    Task<Comanda?> AcharAsync(Guid organizationId, Guid unitId, Guid comandaId, CancellationToken cancellationToken);

    /// <summary>Closes an OPEN comanda; false when it was no longer open. Runs after the version bump of the same unit of work.</summary>
    Task<bool> FecharAsync(Guid organizationId, Guid comandaId, DateTimeOffset fechadaEm, CancellationToken cancellationToken);

    /// <summary>Marks the service fee of an OPEN comanda as removed; false when it was already removed or the comanda is not open.</summary>
    Task<bool> RemoverTaxaServicoAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    /// <summary>Optimistic write: bumps the version of an OPEN comanda only if it is still <paramref name="versaoEsperada"/>; false otherwise (and nothing changes). The row stays locked until the unit of work ends.</summary>
    Task<bool> AvancarVersaoAsync(Guid organizationId, Guid comandaId, int versaoEsperada, CancellationToken cancellationToken);

    /// <summary>Open comandas of the unit, newest first; only <paramref name="garcomId"/>'s when given.</summary>
    Task<IReadOnlyList<Comanda>> ListarAbertasAsync(Guid organizationId, Guid unitId, Guid? garcomId, int limite, CancellationToken cancellationToken);
}

public sealed record RodadaEnviada(Guid EventId, Guid ComandaId, Guid RodadaId, Guid UnidadeId, int Itens, long ValorCentavos, Guid EnviadaPor) : IDomainEvent
{
    public const string Type = "comanda.rodada-enviada";

    public string EventType => Type;
}

public sealed record RodadaDesfeita(Guid EventId, Guid ComandaId, Guid RodadaId, Guid UnidadeId, Guid DesfeitaPor) : IDomainEvent
{
    public const string Type = "comanda.rodada-desfeita";

    public string EventType => Type;
}

public sealed record ItemCancelado(Guid EventId, Guid ComandaId, Guid ItemId, Guid UnidadeId, string Motivo, Guid CanceladoPor) : IDomainEvent
{
    public const string Type = "comanda.item-cancelado";

    public string EventType => Type;
}

public sealed record ComandaFechada(Guid EventId, Guid ComandaId, Guid UnidadeId, int Numero, long TotalCentavos, Guid FechadaPor) : IDomainEvent
{
    public const string Type = "comanda.fechada";

    public string EventType => Type;
}

/// <summary>Port: the rounds and items of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IRodadasStore
{
    /// <summary>The round with its items, by the id the client generated; null when there is none.</summary>
    Task<RodadaComItens?> AcharRodadaAsync(Guid organizationId, Guid rodadaId, CancellationToken cancellationToken);

    /// <summary>Items of one comanda (all states), in launch order.</summary>
    Task<IReadOnlyList<ItemDaComanda>> ListarItensAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    Task<ItemDaComanda?> AcharItemAsync(Guid organizationId, Guid itemId, CancellationToken cancellationToken);

    Task InserirRodadaAsync(Guid organizationId, RodadaComItens rodada, CancellationToken cancellationToken);

    /// <summary>Counts the launch of each product in the unit (feeds the frequent items).</summary>
    Task RegistrarFrequenciaAsync(Guid organizationId, Guid unitId, IReadOnlyCollection<Guid> produtos, CancellationToken cancellationToken);

    /// <summary>Cancels one item; false when it was no longer launched.</summary>
    Task<bool> CancelarItemAsync(Guid organizationId, Guid itemId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken);

    /// <summary>Marks the round undone and cancels its items with the fixed reason; false when it was already undone.</summary>
    Task<bool> DesfazerRodadaAsync(Guid organizationId, Guid rodadaId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken);
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
