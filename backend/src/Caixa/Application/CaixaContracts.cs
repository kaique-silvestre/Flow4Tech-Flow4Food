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

    /// <summary>[A][L] Put money into the drawer during the own shift (limited by amount).</summary>
    public const string Suprimento = "caixa:suprimento";

    /// <summary>[A][L] Take money out of the drawer during the own shift (limited by amount).</summary>
    public const string Sangria = "caixa:sangria";

    /// <summary>Close the shift blind: when the blind-count option is on, the holder does not see the expected value until the count is submitted.</summary>
    public const string FecharCego = "caixa:fechar-cego";

    /// <summary>[A][L] Approve or recount a difference above the tolerance (limited by the difference amount).</summary>
    public const string AprovarDiferenca = "caixa:aprovar-diferenca";
}

[AuditCatalog]
public static class CaixaAuditActions
{
    public const string Abrir = "caixa:abrir";

    public const string Fechar = "caixa:fechar";

    public const string Suprimento = "caixa:suprimento";

    public const string Sangria = "caixa:sangria";

    public const string AprovarDiferenca = "caixa:aprovar-diferenca";

    public const string RecontarDiferenca = "caixa:recontar-diferenca";
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

    /// <summary>Total difference (cash plus counted methods, absolute) up to which closing concludes alone; above it the shift waits for an approver.</summary>
    public static readonly SettingDefinition ToleranciaDiferenca = new(
        "caixa.tolerancia-diferenca-centavos",
        SettingType.Inteiro,
        "500",
        SettingLayers.OrganizacaoEUnidade,
        "Diferenca do fechamento, em centavos, que conclui sozinha.")
    {
        Minimum = 0,
        Maximum = RegrasDoCaixa.ValorMaximoCentavos,
    };

    /// <summary>When on, whoever closes with <c>caixa:fechar-cego</c> does not see the expected value until the count is submitted.</summary>
    public static readonly SettingDefinition ConferenciaCega = new(
        "caixa.conferencia-cega",
        SettingType.Booleano,
        "false",
        SettingLayers.OrganizacaoEUnidade,
        "Esconde o valor esperado de quem fecha o caixa ate a contagem ser enviada.");

    /// <summary>When on, the person who closed a shift may approve its own difference (separation of duties off).</summary>
    public static readonly SettingDefinition PermitirAutoaprovacao = new(
        "caixa.permitir-autoaprovacao",
        SettingType.Booleano,
        "false",
        SettingLayers.OrganizacaoEUnidade,
        "Permite que quem fechou o caixa aprove a propria diferenca.");
}

public sealed record CaixaAberto(Guid EventId, Guid TurnoId, Guid UnidadeId, Guid OperadorId, long FundoCentavos) : IDomainEvent
{
    public const string Type = "caixa.aberto";

    public string EventType => Type;
}

public sealed record CaixaFechado(Guid EventId, Guid TurnoId, Guid UnidadeId, Guid OperadorId, long ContadoCentavos, long EsperadoCentavos, long DiferencaCentavos, string Situacao = "concluida") : IDomainEvent
{
    public const string Type = "caixa.fechado";

    public string EventType => Type;
}

public sealed record CaixaSuprimento(Guid EventId, Guid MovimentoId, Guid TurnoId, Guid UnidadeId, Guid OperadorId, long ValorCentavos, string Motivo) : IDomainEvent
{
    public const string Type = "caixa.suprimento";

    public string EventType => Type;
}

public sealed record CaixaSangria(Guid EventId, Guid MovimentoId, Guid TurnoId, Guid UnidadeId, Guid OperadorId, long ValorCentavos, string Motivo, string Destino) : IDomainEvent
{
    public const string Type = "caixa.sangria";

    public string EventType => Type;
}

public sealed record CaixaDiferencaAprovada(Guid EventId, Guid TurnoId, Guid UnidadeId, Guid OperadorId, Guid AprovadorId, long DiferencaCentavos, string Motivo) : IDomainEvent
{
    public const string Type = "caixa.diferenca-aprovada";

    public string EventType => Type;
}

/// <summary>A payment method as the count sees it: <paramref name="Dinheiro"/> methods are counted in the shift itself, never in <c>contagens</c>.</summary>
public sealed record MetodoDeContagem(Guid Id, string Nome, bool Dinheiro);

/// <summary>An approver's act on a pending difference, for the history of the shift.</summary>
public sealed record AprovacaoDeDiferenca(Guid Id, Guid TurnoId, string Tipo, Guid AprovadorId, string Motivo, long DiferencaAntesCentavos, long DiferencaDepoisCentavos, DateTimeOffset CriadoEm);

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

    /// <summary>History: shifts opened in [<paramref name="de"/>, <paramref name="ate"/>) (each bound optional), of one operator when given, of one situation when given, most recent first.</summary>
    Task<IReadOnlyList<Turno>> ListarHistoricoAsync(Guid organizationId, Guid unitId, Guid? operadorId, DateTimeOffset? de, DateTimeOffset? ate, SituacaoDaConferencia? situacao, int limite, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the open shift row and answers what the drawer holds now (<c>fundo + cash received + supplies - withdrawals</c>); null when the shift is not open. The lock serializes movements and
    /// closing: whoever comes second sees the other's committed work.
    /// </summary>
    Task<long?> SaldoDaGavetaTravandoAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);

    Task InserirMovimentoAsync(Guid organizationId, MovimentoDoCaixa movimento, CancellationToken cancellationToken);

    Task<IReadOnlyList<MovimentoDoCaixa>> ListarMovimentosAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);

    /// <summary>Sum of supplies and of withdrawals of the shift, summed by the database.</summary>
    Task<(long Suprimentos, long Sangrias)> TotaisDosMovimentosAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);

    /// <summary>The methods among <paramref name="ids"/> that exist in the organization.</summary>
    Task<IReadOnlyDictionary<Guid, MetodoDeContagem>> MetodosAsync(Guid organizationId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Applied value plus tips of the confirmed payments of the shift, per method that was used (what a card machine or a pix statement should add up to).</summary>
    Task<IReadOnlyDictionary<Guid, long>> RecebidoPorMetodoAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);

    Task InserirContagensAsync(Guid organizationId, Guid turnoId, int sequencia, IReadOnlyList<ContagemDoMetodo> contagens, DateTimeOffset criadoEm, CancellationToken cancellationToken);

    /// <summary>The current count of each method of the shift (the highest sequence of each), by method name.</summary>
    Task<IReadOnlyList<ContagemDoMetodo>> ContagensAtuaisAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);

    Task<int> ProximaSequenciaAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);

    /// <summary>Sets the situation of a CLOSED shift (used right after closing, inside the closing unit of work).</summary>
    Task DefinirSituacaoAsync(Guid organizationId, Guid turnoId, SituacaoDaConferencia situacao, CancellationToken cancellationToken);

    /// <summary>Marks a pending shift approved; false (nothing written) when it was no longer pending. The row lock makes two approvers a one-wins race.</summary>
    Task<bool> AprovarAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);

    /// <summary>Replaces the cash count of a PENDING shift and recomputes its difference; false when it was no longer pending.</summary>
    Task<bool> RecontarDinheiroAsync(Guid organizationId, Guid turnoId, long contadoCentavos, CancellationToken cancellationToken);

    Task InserirAprovacaoAsync(Guid organizationId, Guid unitId, AprovacaoDeDiferenca aprovacao, CancellationToken cancellationToken);

    Task<IReadOnlyList<AprovacaoDeDiferenca>> ListarAprovacoesAsync(Guid organizationId, Guid turnoId, CancellationToken cancellationToken);
}

/// <summary>Port: asks the one engine for a limited caixa permission with the amount the SERVER computed (adapted in Api). A daily cap, if any, is reserved in the unit of work in progress.</summary>
public interface IDecisaoDeCaixa
{
    Task<bool> PodeAsync(IRequestContext request, string permission, long valorCentavos, CancellationToken cancellationToken);
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
