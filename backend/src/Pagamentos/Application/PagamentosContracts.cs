using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Pagamentos.Domain;

namespace Flow4Tech.Pagamentos.Application;

[PermissionCatalog]
public static class PagamentosPermissions
{
    /// <summary>Record a payment received for a comanda.</summary>
    public const string Registrar = "pagamento:registrar";

    /// <summary>See the payments of a comanda.</summary>
    public const string Ver = "pagamento:ver";
}

[AuditCatalog]
public static class PagamentosAuditActions
{
    public const string Registrar = "pagamento:registrar";
}

public sealed record PagamentoRegistrado(Guid EventId, Guid PagamentoId, Guid ComandaId, Guid UnidadeId, Guid? TurnoId, long RecebidoCentavos, long AplicadoCentavos, long TrocoCentavos, Guid OperadorId) : IDomainEvent
{
    public const string Type = "pagamento.registrado";

    public string EventType => Type;
}

/// <summary>Port: the payment rows of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IPagamentosStore
{
    /// <summary>Inserts the payment; false (nothing written) when a payment with that id already exists.</summary>
    Task<bool> TentarInserirAsync(Guid organizationId, Pagamento pagamento, CancellationToken cancellationToken);

    Task<Pagamento?> AcharAsync(Guid organizationId, Guid pagamentoId, CancellationToken cancellationToken);

    /// <summary>Sum of the cents applied to the comanda by every payment so far.</summary>
    Task<long> SomaAplicadaAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    /// <summary>Payments of the comanda, oldest first.</summary>
    Task<IReadOnlyList<Pagamento>> ListarDaComandaAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);
}

/// <summary>Port: domain events of the payments, written to the outbox on the unit of work in progress.</summary>
public interface IEventosDePagamentos
{
    Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken);
}

/// <summary>A payment as the other modules and the API see it (the Domain record stays inside Pagamentos).</summary>
public sealed record PagamentoRecebido(
    Guid Id,
    Guid ComandaId,
    Guid? TurnoId,
    string Metodo,
    long RecebidoCentavos,
    long AplicadoCentavos,
    long TrocoCentavos,
    Guid OperadorId,
    DateTimeOffset RegistradoEm,
    long SaldoAposCentavos,
    int ComandaVersao)
{
    public static PagamentoRecebido De(Pagamento p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Id, p.ComandaId, p.TurnoId, p.Metodo, p.RecebidoCentavos, p.AplicadoCentavos, p.TrocoCentavos, p.OperadorId, p.RegistradoEm, p.SaldoAposCentavos, p.ComandaVersao);
    }
}

/// <summary>What a payment provider is asked to take. Cash is the only method of this slice.</summary>
public sealed record SolicitacaoDePagamento(Guid PagamentoId, Guid ComandaId, long RecebidoCentavos, long AplicadoCentavos);

/// <summary>The provider's answer: <see cref="Confirmado"/> is true once the money is considered received.</summary>
public sealed record ResultadoDoProvedor(bool Confirmado);

/// <summary>
/// Provider contract (start, confirm): the seam where a card machine or Pix will plug in later. Nothing but <see cref="ProvedorManual"/> exists in this slice.
/// </summary>
public interface IProvedorDePagamento
{
    string Nome { get; }

    Task<ResultadoDoProvedor> IniciarAsync(SolicitacaoDePagamento solicitacao, CancellationToken cancellationToken);

    Task<ResultadoDoProvedor> ConfirmarAsync(Guid pagamentoId, CancellationToken cancellationToken);
}

/// <summary>The single provider: cash handed over by hand, so starting a payment already confirms it (synchronous, no external call).</summary>
public sealed class ProvedorManual : IProvedorDePagamento
{
    public const string NomeDoProvedor = "manual";

    public string Nome => NomeDoProvedor;

    public Task<ResultadoDoProvedor> IniciarAsync(SolicitacaoDePagamento solicitacao, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solicitacao);
        return Task.FromResult(new ResultadoDoProvedor(true));
    }

    public Task<ResultadoDoProvedor> ConfirmarAsync(Guid pagamentoId, CancellationToken cancellationToken) => Task.FromResult(new ResultadoDoProvedor(true));
}

/// <summary>The cash rules other modules need, over the Domain ones (which stay inside Pagamentos).</summary>
public static class RegrasDeRecebimento
{
    public static bool RecebidoValido(long recebidoCentavos) => RegrasDePagamento.RecebidoValido(recebidoCentavos);

    /// <summary>What a payment takes off the balance: the cash received, or the whole balance when more was handed over.</summary>
    public static long Aplicado(long recebidoCentavos, long saldoCentavos) => RegrasDePagamento.Aplicado(recebidoCentavos, saldoCentavos);
}
