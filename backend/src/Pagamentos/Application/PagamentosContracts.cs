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

    /// <summary>See the payments of a comanda and the payment methods of the organization.</summary>
    public const string Ver = "pagamento:ver";

    /// <summary>Split a payment: receive with more than one method line (mixed payment) or pay a part of a recorded split of the bill.</summary>
    public const string Dividir = "pagamento:dividir";

    /// <summary>Record a tip on a payment (outside the balance).</summary>
    public const string RegistrarGorjeta = "pagamento:registrar-gorjeta";

    /// <summary>Rename, deactivate or add payment methods of the organization.</summary>
    public const string GerenciarMetodos = "metodo-pagamento:gerenciar";
}

[AuditCatalog]
public static class PagamentosAuditActions
{
    public const string Registrar = "pagamento:registrar";

    public const string CriarMetodo = "metodo-pagamento:criar";

    public const string AlterarMetodo = "metodo-pagamento:alterar";
}

public sealed record PagamentoRegistrado(
    Guid EventId,
    Guid PagamentoId,
    Guid ComandaId,
    Guid UnidadeId,
    Guid? TurnoId,
    long RecebidoCentavos,
    long AplicadoCentavos,
    long TrocoCentavos,
    Guid OperadorId,
    string Metodo = "dinheiro",
    long GorjetaCentavos = 0,
    Guid? DivisaoId = null,
    int? DivisaoParte = null) : IDomainEvent
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

    /// <summary>Sum of the cents applied to the comanda by every confirmed payment so far (a pending one does not count).</summary>
    Task<long> SomaAplicadaAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    /// <summary>Cents applied so far to each part of a recorded split of the comanda (key: split id and part number); only parts with payments appear.</summary>
    Task<IReadOnlyDictionary<(Guid DivisaoId, int Parte), long>> SomaAplicadaPorParteAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);

    /// <summary>Payments of the comanda, oldest first.</summary>
    Task<IReadOnlyList<Pagamento>> ListarDaComandaAsync(Guid organizationId, Guid comandaId, CancellationToken cancellationToken);
}

/// <summary>Port: the payment methods of the current organization. Runs inside a unit of work (RLS filters). Methods are never deleted, only deactivated.</summary>
public interface IMetodosDePagamentoStore
{
    /// <summary>Inserts the factory methods the organization does not have yet (idempotent; a method that exists is left as the organization set it).</summary>
    Task GarantirDeFabricaAsync(Guid organizationId, DateTimeOffset agora, CancellationToken cancellationToken);

    Task<IReadOnlyList<MetodoDePagamento>> ListarAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<MetodoDePagamento?> AcharAsync(Guid organizationId, Guid metodoId, CancellationToken cancellationToken);

    /// <summary>The cash method (key <c>dinheiro</c>).</summary>
    Task<MetodoDePagamento?> AcharDinheiroAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Adds a method the organization defines (never cash). False when the name is taken (case-insensitive).</summary>
    Task<bool> CriarAsync(Guid organizationId, MetodoDePagamento metodo, DateTimeOffset agora, CancellationToken cancellationToken);

    /// <summary>Renames and activates or deactivates. <c>Existe</c> false when there is no such method; <c>NomeLivre</c> false (nothing written) when the name belongs to another method.</summary>
    Task<(bool Existe, bool NomeLivre)> AlterarAsync(Guid organizationId, Guid metodoId, string nome, bool ativo, CancellationToken cancellationToken);
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
    int ComandaVersao,
    Guid? MetodoId,
    string MetodoNome,
    long GorjetaCentavos,
    string Estado,
    Guid? DivisaoId,
    int? DivisaoParte)
{
    public static PagamentoRecebido De(Pagamento p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Id, p.ComandaId, p.TurnoId, p.Metodo, p.RecebidoCentavos, p.AplicadoCentavos, p.TrocoCentavos, p.OperadorId, p.RegistradoEm, p.SaldoAposCentavos, p.ComandaVersao, p.MetodoId, p.MetodoNome, p.GorjetaCentavos, p.Estado == EstadoDoPagamento.Pendente ? "pendente" : "confirmado", p.DivisaoId, p.DivisaoParte);
    }
}

/// <summary>A payment method as the other modules and the API see it. <c>Chave</c> is null for a method the organization created.</summary>
public sealed record MetodoDoSalao(Guid Id, string? Chave, string Nome, bool Dinheiro, bool Ativo, int Ordem)
{
    public static MetodoDoSalao De(MetodoDePagamento m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(m.Id, m.Chave, m.Nome, m.Tipo == TipoDeMetodo.Dinheiro, m.Ativo, m.Ordem);
    }
}

/// <summary>One method line asked for. <c>MetodoId</c> null means the cash method of the organization. <c>DivisaoId</c> and <c>DivisaoParte</c> (both or none) pay a part of a recorded split.</summary>
public sealed record LinhaDoRecebimento(Guid PagamentoId, Guid? MetodoId, long RecebidoCentavos, long GorjetaCentavos, Guid? DivisaoId, int? DivisaoParte);

/// <summary>A line with its method resolved and its cents shared out.</summary>
public sealed record LinhaPlanejada(LinhaDoRecebimento Pedido, MetodoDoSalao Metodo, long AplicadoCentavos, long TrocoCentavos);

public enum RecusaDoPlano
{
    Nenhuma,

    /// <summary>Malformed lines, an unknown method, or no balance.</summary>
    Invalido,

    MetodoInativo,

    /// <summary>The non-cash lines pass the balance, or a line would apply nothing.</summary>
    ExcedeSaldo,
}

public sealed record PlanoDeRecebimento(RecusaDoPlano Recusa, IReadOnlyList<LinhaPlanejada> Linhas);

/// <summary>What a payment provider is asked to take (the money handed over and the part that pays the bill).</summary>
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
