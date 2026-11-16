using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Pagamentos.Domain;

namespace Flow4Tech.Pagamentos.Application;

/// <summary>A refund was recorded. <c>ValorCentavos</c> is negative (the cents handed back).</summary>
public sealed record PagamentoEstornado(
    Guid EventId,
    Guid EstornoId,
    Guid PagamentoId,
    Guid ComandaId,
    Guid UnidadeId,
    Guid? TurnoId,
    long ValorCentavos,
    string Motivo,
    Guid OperadorId) : IDomainEvent
{
    public const string Type = "pagamento.estornado";

    public string EventType => Type;
}

/// <summary>Port: the refund rows of the current organization (insert-only). Runs inside a unit of work; RLS filters.</summary>
public interface IEstornosStore
{
    /// <summary>Serializes refunds of one payment until the unit of work ends (two refunds racing cannot both pass the refundable balance).</summary>
    Task TravarPagamentoAsync(Guid organizationId, Guid pagamentoId, CancellationToken cancellationToken);

    Task InserirAsync(Guid organizationId, Estorno estorno, CancellationToken cancellationToken);

    /// <summary>Positive total refunded so far of one payment.</summary>
    Task<long> SomaEstornadaAsync(Guid organizationId, Guid pagamentoId, CancellationToken cancellationToken);

    /// <summary>Positive total refunded so far of each payment of the comanda (only payments with a refund appear).</summary>
    Task<IReadOnlyDictionary<Guid, long>> SomaEstornadaPorPagamentoAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    /// <summary>Refunds of a payment, oldest first.</summary>
    Task<IReadOnlyList<Estorno>> ListarDoPagamentoAsync(Guid organizationId, Guid pagamentoId, CancellationToken cancellationToken);
}

/// <summary>
/// Port: asks the one authorization engine for <c>pagamento:estornar</c> (limited by value per operation and by a daily cap) with the cents the SERVER measured (adapted in Api). A daily cap, if any, is
/// reserved inside the unit of work in progress, so a refusal that throws rolls the reservation back with the rest.
/// </summary>
public interface IDecisaoDeEstorno
{
    Task<bool> PodeAsync(IRequestContext request, long valorCentavos, CancellationToken cancellationToken);
}

/// <summary>Raised inside the unit of work when the engine refuses: nothing of the refund (nor of the cancellation that carried it) is kept. The caller audits the denial on its own transaction and answers 403.</summary>
public sealed class DecisaoDeEstornoNegadaException : InvalidOperationException
{
    public DecisaoDeEstornoNegadaException()
        : base("Decisao negada pelo motor.")
    {
    }

    public DecisaoDeEstornoNegadaException(string message)
        : base(message)
    {
    }

    public DecisaoDeEstornoNegadaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum ResultadoDoEstorno
{
    Ok,

    /// <summary>No reason (or one too long), or an amount below one cent.</summary>
    Invalido,

    /// <summary>No payment of the session's unit with this id.</summary>
    NaoEncontrado,

    /// <summary>The amount passes what is still refundable of the payment (or nothing is left).</summary>
    ExcedeSaldoEstornavel,

    /// <summary>The payment is not confirmed (money not received yet).</summary>
    NaoConfirmado,

    /// <summary>The unit requires an open shift and neither the payment's shift nor the operator has one open.</summary>
    SemTurnoAberto,

    /// <summary>A cash refund and the drawer of the shift holds less cash than the amount.</summary>
    CaixaSemDinheiro,

    /// <summary>The shift closed between the check and the movement (rolled back).</summary>
    TurnoEncerrado,

    /// <summary>The engine refused (not held, or beyond the limit or the daily cap). Answered as 403 with a typed body, nothing written.</summary>
    Negado,
}

/// <summary>Raised inside the unit of work when a refund fails AFTER a write of the same operation (a shift movement): the unit rolls back and the caller answers <see cref="Resultado"/>.</summary>
public sealed class EstornoRecusadoException : InvalidOperationException
{
    public EstornoRecusadoException()
        : this(ResultadoDoEstorno.Invalido)
    {
    }

    public EstornoRecusadoException(string message)
        : base(message)
    {
        Resultado = ResultadoDoEstorno.Invalido;
    }

    public EstornoRecusadoException(string message, Exception innerException)
        : base(message, innerException)
    {
        Resultado = ResultadoDoEstorno.Invalido;
    }

    public EstornoRecusadoException(ResultadoDoEstorno resultado)
        : base("Estorno recusado.")
    {
        Resultado = resultado;
    }

    public ResultadoDoEstorno Resultado { get; }
}

/// <summary>A payment with its refunds: what it applied, what was refunded (positive total) and what is still refundable.</summary>
public sealed record EstornosDoPagamento(PagamentoRecebido Pagamento, long EstornadoCentavos, long SaldoEstornavelCentavos, IReadOnlyList<EstornoFeito> Estornos);

/// <summary>A refund as the other modules and the API see it. <c>ValorCentavos</c> is negative.</summary>
public sealed record EstornoFeito(Guid Id, Guid PagamentoId, Guid ComandaId, Guid? TurnoId, string Metodo, long ValorCentavos, string Motivo, Guid OperadorId, DateTimeOffset RegistradoEm)
{
    public static EstornoFeito De(Estorno e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.Id, e.PagamentoId, e.ComandaId, e.TurnoId, e.Metodo, e.ValorCentavos, e.Motivo, e.OperadorId, e.RegistradoEm);
    }
}

/// <summary>The outcome of a refund: <c>Estornos</c> are the rows written (one per payment refunded) and <c>SaldoEstornavelCentavos</c> what is left of the payment asked (zero for a whole comanda).</summary>
public sealed record ResultadoDeEstorno(ResultadoDoEstorno Resultado, IReadOnlyList<EstornoFeito> Estornos, long SaldoEstornavelCentavos = 0);

/// <summary>Ends the "does the caller hold any refund rule" question's unit of work by rolling it back (a capped rule would reserve zero cents); always carries the answer. Never leaves <see cref="GestaoDePagamentos"/>.</summary>
public sealed class ConsultaDeRegraDeEstornoConcluidaException : InvalidOperationException
{
    public ConsultaDeRegraDeEstornoConcluidaException()
        : this(false)
    {
    }

    public ConsultaDeRegraDeEstornoConcluidaException(string message)
        : base(message)
    {
    }

    public ConsultaDeRegraDeEstornoConcluidaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ConsultaDeRegraDeEstornoConcluidaException(bool resultado)
        : base("question finished (rolled back on purpose)")
    {
        Resultado = resultado;
    }

    public bool Resultado { get; }
}
