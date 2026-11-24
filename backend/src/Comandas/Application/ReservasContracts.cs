using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Comandas.Application;

[PermissionCatalog]
public static class ReservaPermissions
{
    /// <summary>List the reservations of the unit.</summary>
    public const string Ver = "reserva:ver";

    /// <summary>Book a table, cancel a booking and mark a no-show.</summary>
    public const string Gerenciar = "reserva:gerenciar";

    /// <summary>Mark that the guests arrived: the reservation becomes a comanda (it also takes <c>comanda:abrir</c>, checked by the handler).</summary>
    public const string Chegou = "reserva:chegou";
}

[AuditCatalog]
public static class ReservasAuditActions
{
    public const string CriarReserva = "reserva:criar";

    public const string ReservaChegou = "reserva:chegou";

    public const string CancelarReserva = "reserva:cancelar";

    public const string ReservaNaoCompareceu = "reserva:nao-compareceu";
}

/// <summary>Port: the reservation rows of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IReservasStore
{
    /// <summary>Serializes the writers of one table's bookings until the unit of work ends, so two simultaneous bookings cannot both pass the overlap check.</summary>
    Task TravarMesaAsync(Guid organizationId, Guid mesaId, CancellationToken cancellationToken);

    Task InserirAsync(Guid organizationId, Reserva reserva, CancellationToken cancellationToken);

    /// <summary>The WAITING bookings of the table (the only ones that hold it).</summary>
    Task<IReadOnlyList<ReservaNaMesa>> EmEsperaDaMesaAsync(Guid organizationId, Guid unitId, Guid mesaId, CancellationToken cancellationToken);

    /// <summary>Every WAITING booking of the unit that names a table (the map shows the ones due soon).</summary>
    Task<IReadOnlyList<ReservaNaMesa>> EmEsperaComMesaAsync(Guid organizationId, Guid unitId, CancellationToken cancellationToken);

    /// <summary>The booking of the unit with this id, LOCKED until the unit of work ends (a second writer waits for it); null when unknown or of another unit.</summary>
    Task<Reserva?> AcharParaMudarAsync(Guid organizationId, Guid unitId, Guid reservaId, CancellationToken cancellationToken);

    Task MudarEstadoAsync(Guid organizationId, Guid reservaId, EstadoDaReserva estado, Guid? comandaId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The bookings of the operating day, by time.</summary>
    Task<IReadOnlyList<Reserva>> ListarDoDiaAsync(Guid organizationId, Guid unitId, DateOnly dia, CancellationToken cancellationToken);
}
