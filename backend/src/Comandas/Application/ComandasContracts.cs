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

    /// <summary>Move an open comanda to another table of the unit.</summary>
    public const string TransferirMesa = "comanda:transferir-mesa";

    /// <summary>Move launched items to another open comanda of the unit, or separate them into a new comanda.</summary>
    public const string TransferirItem = "comanda:transferir-item";

    /// <summary>Merge an open comanda into another open comanda of the unit (the first one is closed by the merge).</summary>
    public const string Juntar = "comanda:juntar";

    /// <summary>Change the quantity or the note of an item already launched (the frozen price never changes).</summary>
    public const string AlterarItem = "comanda:alterar-item";

    /// <summary>Rename an open comanda.</summary>
    public const string Renomear = "comanda:renomear";

    /// <summary>Hand an open comanda to another waiter of the unit.</summary>
    public const string TrocarGarcom = "comanda:trocar-garcom";

    /// <summary>Change the head count and the general note of an open comanda.</summary>
    public const string AlterarDados = "comanda:alterar-dados";

    /// <summary>Read the timeline (who, what, when) of a comanda.</summary>
    public const string VerHistorico = "comanda:ver-historico";

    /// <summary>Give a discount (percent or value, on an item or the comanda). Limited: the engine measures the percent and the value the SERVER computed. Sensitive: a denial is audited.</summary>
    public const string Desconto = "comanda:desconto";

    /// <summary>Give an item as a courtesy (charge zero, kept for reports). Limited like a discount. Sensitive: a denial is audited.</summary>
    public const string Cortesia = "comanda:cortesia";

    /// <summary>Give an item as a gift (charge zero, kept for reports and the stock write-off). Limited like a discount. Sensitive: a denial is audited.</summary>
    public const string Brinde = "comanda:brinde";

    /// <summary>Cancel a whole comanda that has no payment (with a standard reason). Sensitive: a denial is audited.</summary>
    public const string Cancelar = "comanda:cancelar";

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

    /// <summary>Closes an OPEN comanda as merged into <paramref name="destinoId"/>; false when it was no longer open. Runs after the version bump of the same unit of work.</summary>
    Task<bool> JuntarAsync(Guid organizationId, Guid comandaId, Guid destinoId, Guid por, DateTimeOffset quando, CancellationToken cancellationToken);

    /// <summary>Marks the service fee of an OPEN comanda as removed; false when it was already removed or the comanda is not open.</summary>
    Task<bool> RemoverTaxaServicoAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    /// <summary>Optimistic write: bumps the version of an OPEN comanda only if it is still <paramref name="versaoEsperada"/>; false otherwise (and nothing changes). The row stays locked until the unit of work ends.</summary>
    Task<bool> AvancarVersaoAsync(Guid organizationId, Guid comandaId, int versaoEsperada, CancellationToken cancellationToken);

    /// <summary>Bumps the version of an OPEN comanda whatever it is now (taking the row lock until the unit of work ends); false when the comanda is not open. For writes that do not depend on what the caller last saw.</summary>
    Task<bool> AvancarVersaoSemConferirAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    /// <summary>Renames an OPEN comanda; false when it is no longer open. Runs after the version bump of the same unit of work.</summary>
    Task<bool> RenomearAsync(Guid organizationId, Guid comandaId, string identificacao, CancellationToken cancellationToken);

    /// <summary>Hands an OPEN comanda to another waiter; false when it is no longer open. Runs after the version bump of the same unit of work.</summary>
    Task<bool> TrocarGarcomAsync(Guid organizationId, Guid comandaId, Guid garcomId, CancellationToken cancellationToken);

    /// <summary>Replaces the head count and the general note (null clears each) of an OPEN comanda; false when it is no longer open. Runs after the version bump of the same unit of work.</summary>
    Task<bool> AtualizarDadosAsync(Guid organizationId, Guid comandaId, int? pessoas, string? observacao, CancellationToken cancellationToken);

    /// <summary>Cancels an OPEN comanda with its reason and author; false when it was no longer open. Runs after the version bump of the same unit of work.</summary>
    Task<bool> CancelarAsync(Guid organizationId, Guid comandaId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken);

    /// <summary>Moves an OPEN comanda to the table (and renames it to <paramref name="identificacao"/>); false when it is no longer open. Runs after the version bump of the same unit of work.</summary>
    Task<bool> TrocarMesaAsync(Guid organizationId, Guid comandaId, Guid mesaId, string identificacao, CancellationToken cancellationToken);

    /// <summary>The most recently CLOSED comanda that held the table in the unit (closed time, then id); only <paramref name="garcomId"/>'s when given. Null when there is none.</summary>
    Task<Comanda?> UltimaFechadaDaMesaAsync(Guid organizationId, Guid unitId, Guid mesaId, Guid? garcomId, CancellationToken cancellationToken);

    /// <summary>Open comandas of the unit, newest first; only <paramref name="garcomId"/>'s when given.</summary>
    Task<IReadOnlyList<Comanda>> ListarAbertasAsync(Guid organizationId, Guid unitId, Guid? garcomId, int limite, CancellationToken cancellationToken);
}

public sealed record ComandaTransferida(Guid EventId, Guid ComandaId, Guid UnidadeId, int Numero, Guid? MesaDeId, Guid MesaParaId, Guid TransferidaPor) : IDomainEvent
{
    public const string Type = "comanda.transferida";

    public string EventType => Type;
}

/// <summary>Items moved between two comandas of one unit (<c>comanda:transferir-item</c>), or into a NEW comanda that a separation created (<paramref name="DestinoCriada"/>). Shares the event name of a table move; the fields tell them apart.</summary>
public sealed record ItensTransferidos(Guid EventId, Guid UnidadeId, Guid OrigemId, int OrigemNumero, Guid DestinoId, int DestinoNumero, bool DestinoCriada, IReadOnlyList<Guid> ItemIds, long ValorDeTabelaCentavos, Guid TransferidaPor) : IDomainEvent
{
    public const string Type = ComandaTransferida.Type;

    public string EventType => Type;
}

/// <summary>A comanda merged into another: its launched items moved to <paramref name="DestinoId"/> and it closed with no balance.</summary>
public sealed record ComandaJuntada(Guid EventId, Guid UnidadeId, Guid OrigemId, int OrigemNumero, Guid DestinoId, int DestinoNumero, int ItensMovidos, Guid JuntadaPor) : IDomainEvent
{
    public const string Type = "comanda.juntada";

    public string EventType => Type;
}

public sealed record ItemCortesia(Guid EventId, Guid ComandaId, Guid ItemId, Guid UnidadeId, Guid ProdutoId, string ProdutoNome, int Quantidade, long ValorDeTabelaCentavos, string Motivo, Guid DadaPor) : IDomainEvent
{
    public const string Type = "comanda.item-cortesia";

    public string EventType => Type;
}

public sealed record ItemBrinde(Guid EventId, Guid ComandaId, Guid ItemId, Guid UnidadeId, Guid ProdutoId, string ProdutoNome, int Quantidade, long ValorDeTabelaCentavos, string Motivo, Guid DadoPor) : IDomainEvent
{
    public const string Type = "comanda.item-brinde";

    public string EventType => Type;
}

public sealed record DescontoAplicado(Guid EventId, Guid ComandaId, Guid DescontoId, Guid UnidadeId, Guid? ItemId, string Forma, decimal? Percentual, long AplicadoCentavos, string Motivo, Guid DadoPor) : IDomainEvent
{
    public const string Type = "comanda.desconto-aplicado";

    public string EventType => Type;
}

public sealed record ItemAlterado(Guid EventId, Guid ComandaId, Guid ItemId, Guid UnidadeId, int QuantidadeAntes, int QuantidadeDepois, Guid AlteradoPor) : IDomainEvent
{
    public const string Type = "comanda.item-alterado";

    public string EventType => Type;
}

public sealed record ComandaCancelada(Guid EventId, Guid ComandaId, Guid UnidadeId, int Numero, string Motivo, int ItensCancelados, Guid CanceladaPor) : IDomainEvent
{
    public const string Type = "comanda.cancelada";

    public string EventType => Type;
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

public sealed record TaxaServicoRemovida(Guid EventId, Guid ComandaId, Guid UnidadeId, int Numero, decimal TaxaServicoPercentual, long TaxaServicoCentavos, Guid RemovidaPor) : IDomainEvent
{
    public const string Type = "comanda.taxa-servico-removida";

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

    /// <summary>Counts the launch of each product in the unit (feeds the frequent items) and by the waiter who launched it (feeds their favorites).</summary>
    Task RegistrarFrequenciaAsync(Guid organizationId, Guid unitId, Guid garcomId, IReadOnlyCollection<Guid> produtos, CancellationToken cancellationToken);

    /// <summary>Counts the launch of each option with its product in the unit (feeds the most used add-ons).</summary>
    Task RegistrarFrequenciaDeOpcoesAsync(Guid organizationId, Guid unitId, IReadOnlyCollection<(Guid ProdutoId, Guid OpcaoId)> usos, CancellationToken cancellationToken);

    /// <summary>Cancels one item; false when it was no longer launched.</summary>
    Task<bool> CancelarItemAsync(Guid organizationId, Guid itemId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken);

    /// <summary>Changes quantity and note of an item still launched (price and options stay frozen); false when it was no longer launched.</summary>
    Task<bool> AlterarItemAsync(Guid organizationId, Guid itemId, int quantidade, string? observacao, CancellationToken cancellationToken);

    /// <summary>Cancels every item of the comanda that is still launched, with one reason; returns how many.</summary>
    Task<int> CancelarItensVivosAsync(Guid organizationId, Guid comandaId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken);

    /// <summary>Turns a launched NORMAL item into a courtesy or a gift (charge zero; price and options stay); false when it was no longer a launched normal item.</summary>
    Task<bool> DefinirTipoAsync(Guid organizationId, Guid itemId, TipoDoItem tipo, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken);

    /// <summary>
    /// Moves launched items to the comanda <paramref name="destinoId"/>: each item changes comanda and goes into a copy of its round that belongs to the destination (same send time, author and position;
    /// <paramref name="destinoVersao"/> is the destination version after the write). Price, options, type and note travel with the item. Returns how many moved. Runs after both version bumps of the same unit of work.
    /// </summary>
    Task<int> MoverItensAsync(Guid organizationId, IReadOnlyCollection<Guid> itemIds, Guid destinoId, int destinoVersao, CancellationToken cancellationToken);

    /// <summary>Marks the round undone and cancels its items with the fixed reason; false when it was already undone.</summary>
    Task<bool> DesfazerRodadaAsync(Guid organizationId, Guid rodadaId, string motivo, Guid por, DateTimeOffset quando, CancellationToken cancellationToken);
}

/// <summary>One line of a comanda's timeline: the audit record behind it. <c>Antes</c> and <c>Depois</c> hold only top-level scalar fields that are not secrets.</summary>
public sealed record EventoDoHistorico(Guid Id, string Acao, DateTimeOffset Quando, Guid? UsuarioId, string? UsuarioNome, IReadOnlyDictionary<string, string> Antes, IReadOnlyDictionary<string, string> Depois);

/// <summary>Port: reads the audit trail of one comanda (its own records, its rounds', its items' and its payments'). Runs inside a unit of work (RLS filters).</summary>
public interface IHistoricoDaComanda
{
    Task<IReadOnlyList<EventoDoHistorico>> ListarAsync(Guid organizationId, Guid comandaId, int limite, CancellationToken cancellationToken);
}

public sealed record GarcomDaUnidade(Guid Id, string Nome);

/// <summary>Port: whether an account may take comandas of the unit (active, and linked to the unit). Runs inside a unit of work.</summary>
public interface IGarconsDaUnidade
{
    /// <summary>The active accounts linked to the unit, by name (who a comanda can be handed to).</summary>
    Task<IReadOnlyList<GarcomDaUnidade>> ListarAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken);

    Task<bool> PodeAtenderAsync(Guid organizationId, Guid unitId, Guid contaId, CancellationToken cancellationToken);
}

/// <summary>Port: the discounts of the current organization. Runs inside a unit of work (RLS filters). Rows are written once.</summary>
public interface IDescontosStore
{
    /// <summary>The discounts of one comanda in the order they were given (instant, then id).</summary>
    Task<IReadOnlyList<Desconto>> ListarAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    Task InserirAsync(Guid organizationId, Guid unitId, Desconto desconto, CancellationToken cancellationToken);
}

/// <summary>
/// Port: asks the one authorization engine for a LIMITED permission of a comanda (<c>comanda:desconto|cortesia|brinde</c>) with the percent and the value the SERVER computed (adapted in Api). A daily cap, if any, is
/// reserved inside the unit of work in progress, so a refusal that throws rolls the reservation back with the rest.
/// </summary>
public interface IDecisaoDeDesconto
{
    Task<bool> PodeAsync(IRequestContext request, string permission, decimal percentual, long valorCentavos, CancellationToken cancellationToken);
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
