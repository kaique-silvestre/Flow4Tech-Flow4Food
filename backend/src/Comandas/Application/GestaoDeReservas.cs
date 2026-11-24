using System.Globalization;
using System.Text.Json.Nodes;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Comandas.Application;

public enum ResultadoDeReserva
{
    Ok,

    Invalido,

    NaoEncontrada,

    /// <summary>The table named does not exist in the unit (or is retired).</summary>
    MesaNaoEncontrada,

    MesasDesligadas,

    /// <summary>The table already has a waiting booking that overlaps this time.</summary>
    Sobreposta,

    /// <summary>The unit is closed at that instant (agenda de funcionamento).</summary>
    ForaDoHorario,

    /// <summary>The booking is not waiting any more (arrived, cancelled or no-show).</summary>
    EstadoInvalido,

    /// <summary>The table of the booking cannot take a comanda now (retired).</summary>
    MesaIndisponivel,

    /// <summary>The caller may not open a comanda (<c>comanda:abrir</c>), which arriving does.</summary>
    Negado,
}

/// <summary>The outcome of a reservation command. <see cref="Repetida"/> is true when the move had already happened (an arrival marked twice): nothing was written again.</summary>
public sealed record ResultadoDeReservar(ResultadoDeReserva Resultado, Reserva? Reserva = null, bool Repetida = false);

/// <summary>
/// Reservations of the active unit. Who may call is the route's job (<c>reserva:ver|gerenciar|chegou</c>, decided by the one engine); arriving also asks <c>comanda:abrir</c> because it opens a comanda.
/// Each write is ONE unit of work: the row, its audit record and, on arrival, the comanda (opened by <see cref="GestaoDeComandas"/> in the same transaction) commit or roll back together.
/// Name and phone are personal data: they never reach the audit trail. The unit comes from the session, never from the client.
/// </summary>
public sealed class GestaoDeReservas(
    IUnitOfWork unitOfWork,
    IReservasStore store,
    IMesasStore mesas,
    GestaoDeComandas comandas,
    IPermissaoDaComanda permissoes,
    IDiaOperacional dias,
    IHorarioDeFuncionamento horario,
    IAuditLog audit,
    ISettings settings,
    IClock clock)
{
    public Task<ResultadoDeReservar> CriarAsync(IRequestContext request, string? nome, string? telefone, int pessoas, DateTimeOffset? quando, Guid? mesaId, string? observacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var limpo = RegrasDeReserva.Nome(nome);
                var agora = clock.UtcNow;
                if (limpo is null
                    || !RegrasDeReserva.TentarTelefone(telefone, out var fone)
                    || !RegrasDeReserva.TentarObservacao(observacao, out var nota)
                    || !RegrasDeReserva.PessoasValidas(pessoas)
                    || quando is not { } instante
                    || !RegrasDeReserva.HorarioValido(instante, agora))
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.Invalido);
                }

                var org = request.Organization;
                if (!await horario.EstaAbertoAsync(org, instante, ct))
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.ForaDoHorario);
                }

                if (mesaId is { } id)
                {
                    if ((await settings.ResolveAsync(OperacaoSettings.UsarMesas, org.OrganizationId, org.UnitId, agora, ct)).Value != "true")
                    {
                        return new ResultadoDeReservar(ResultadoDeReserva.MesasDesligadas);
                    }

                    if (await mesas.AcharAtivaAsync(org.OrganizationId, org.UnitId, id, ct) is null)
                    {
                        return new ResultadoDeReservar(ResultadoDeReserva.MesaNaoEncontrada);
                    }

                    await store.TravarMesaAsync(org.OrganizationId, id, ct);
                    if (RegrasDeReserva.Conflita(id, instante, await store.EmEsperaDaMesaAsync(org.OrganizationId, org.UnitId, id, ct)))
                    {
                        return new ResultadoDeReservar(ResultadoDeReserva.Sobreposta);
                    }
                }

                var dia = await dias.DoInstanteAsync(org, instante, ct);
                var reserva = new Reserva(EntityId.New().Value, org.UnitId, mesaId, limpo, fone, pessoas, instante, dia, nota, EstadoDaReserva.Reservada, null, agora);
                await store.InserirAsync(org.OrganizationId, reserva, ct);

                // Name and phone are personal data: the record carries only what identifies the booking and its time.
                var depois = new JsonObject
                {
                    ["estado"] = RegrasDeReserva.Palavra(reserva.Estado),
                    ["pessoas"] = pessoas,
                    ["horario"] = instante.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                };
                if (mesaId is { } mesaUsada)
                {
                    depois["mesaId"] = mesaUsada.ToString("D");
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ReservasAuditActions.CriarReserva, ObjectType = "reserva", ObjectId = reserva.Id.ToString("D"), After = depois }, ct);
                return new ResultadoDeReservar(ResultadoDeReserva.Ok, reserva);
            },
            cancellationToken);
    }

    /// <summary>The bookings of an operating day (the current one when <paramref name="dia"/> is null), by time. The current day is the unit's, from the server clock.</summary>
    public Task<IReadOnlyList<Reserva>> ListarAsync(IRequestContext request, DateOnly? dia, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                var alvo = dia ?? await dias.AtualAsync(org, ct);
                return await store.ListarDoDiaAsync(org.OrganizationId, org.UnitId, alvo, ct);
            },
            cancellationToken);
    }

    /// <summary>
    /// The guests arrived: the waiting booking becomes an open comanda of the same unit (the table of the booking, the head count of the booking, the caller as waiter) in this one unit of work.
    /// The booking row is locked first, so a repeat (double tap, retry, two devices) waits and then finds it done: it returns the same comanda and opens nothing.
    /// </summary>
    public Task<ResultadoDeReservar> ChegouAsync(IRequestContext request, Guid reservaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                if (!await permissoes.PodeAsync(request, ComandaPermissions.Abrir, ct))
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.Negado);
                }

                var reserva = await store.AcharParaMudarAsync(org.OrganizationId, org.UnitId, reservaId, ct);
                if (reserva is null)
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.NaoEncontrada);
                }

                if (reserva.Estado == EstadoDaReserva.Chegou)
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.Ok, reserva, Repetida: true);
                }

                if (!RegrasDeReserva.PodeMudar(reserva.Estado, EstadoDaReserva.Chegou))
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.EstadoInvalido, reserva);
                }

                var aberta = await comandas.AbrirAsync(request, null, reserva.MesaId, reserva.Pessoas, ct);
                if (aberta.Resultado != ResultadoDeComanda.Ok || aberta.Comanda is null)
                {
                    return new ResultadoDeReservar(
                        aberta.Resultado switch
                        {
                            ResultadoDeComanda.ForaDoHorario => ResultadoDeReserva.ForaDoHorario,
                            ResultadoDeComanda.MesasDesligadas => ResultadoDeReserva.MesasDesligadas,
                            _ => ResultadoDeReserva.MesaIndisponivel,
                        },
                        reserva);
                }

                await store.MudarEstadoAsync(org.OrganizationId, reserva.Id, EstadoDaReserva.Chegou, aberta.Comanda.Id, clock.UtcNow, ct);
                var depois = new JsonObject { ["estado"] = RegrasDeReserva.Palavra(EstadoDaReserva.Chegou), ["comandaId"] = aberta.Comanda.Id.ToString("D") };
                await audit.RecordAsync(request, Registro(ReservasAuditActions.ReservaChegou, reserva, depois), ct);
                return new ResultadoDeReservar(ResultadoDeReserva.Ok, reserva with { Estado = EstadoDaReserva.Chegou, ComandaId = aberta.Comanda.Id });
            },
            cancellationToken);
    }

    public Task<ResultadoDeReservar> CancelarAsync(IRequestContext request, Guid reservaId, CancellationToken cancellationToken = default) =>
        MudarAsync(request, reservaId, EstadoDaReserva.Cancelada, ReservasAuditActions.CancelarReserva, cancellationToken);

    public Task<ResultadoDeReservar> MarcarNaoCompareceuAsync(IRequestContext request, Guid reservaId, CancellationToken cancellationToken = default) =>
        MudarAsync(request, reservaId, EstadoDaReserva.NaoCompareceu, ReservasAuditActions.ReservaNaoCompareceu, cancellationToken);

    private Task<ResultadoDeReservar> MudarAsync(IRequestContext request, Guid reservaId, EstadoDaReserva para, string acao, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                var reserva = await store.AcharParaMudarAsync(org.OrganizationId, org.UnitId, reservaId, ct);
                if (reserva is null)
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.NaoEncontrada);
                }

                if (reserva.Estado == para)
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.Ok, reserva, Repetida: true);
                }

                if (!RegrasDeReserva.PodeMudar(reserva.Estado, para))
                {
                    return new ResultadoDeReservar(ResultadoDeReserva.EstadoInvalido, reserva);
                }

                await store.MudarEstadoAsync(org.OrganizationId, reserva.Id, para, null, clock.UtcNow, ct);
                await audit.RecordAsync(request, Registro(acao, reserva, new JsonObject { ["estado"] = RegrasDeReserva.Palavra(para) }), ct);
                return new ResultadoDeReservar(ResultadoDeReserva.Ok, reserva with { Estado = para });
            },
            cancellationToken);
    }

    private static AuditEntry Registro(string acao, Reserva antes, JsonObject depois) =>
        new() { Action = acao, ObjectType = "reserva", ObjectId = antes.Id.ToString("D"), Before = new JsonObject { ["estado"] = RegrasDeReserva.Palavra(antes.Estado) }, After = depois };
}
