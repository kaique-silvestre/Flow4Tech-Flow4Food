using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;

namespace Flow4Tech.Api;

/// <summary>
/// A booking. <c>Estado</c> is <c>reservada</c>, <c>chegou</c>, <c>cancelada</c> or <c>nao-compareceu</c>; <c>ComandaId</c> is set once the guests arrived. <c>Nome</c> and <c>Telefone</c> are personal data
/// (only who may see reservations gets them; they never reach the audit trail).
/// </summary>
internal sealed record ReservaDto(Guid Id, Guid? MesaId, string Nome, string? Telefone, int Pessoas, DateTimeOffset Horario, DateOnly DiaOperacional, string? Observacao, string Estado, Guid? ComandaId);

/// <summary>Nullable on purpose: a missing field is a 400, never a default. The unit is the session's and is never in the body. <c>Horario</c> is an instant with offset; <c>MesaId</c> is optional.</summary>
internal sealed record CriarReservaRequest(string? Nome, string? Telefone, int? Pessoas, DateTimeOffset? Horario, Guid? MesaId, string? Observacao);

/// <summary><c>Repetida</c> is true when the arrival had already been marked: the same comanda comes back and nothing new is opened.</summary>
internal sealed record ChegouDaReservaDto(ReservaDto Reserva, Guid ComandaId, bool Repetida);

internal sealed record ReservaErro(string Codigo);

internal static class ReservasEndpoints
{
    private const long Body = 2048;

    public static void MapReservas(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/reservas", async (DateOnly? dia, RequestContextAccessor request, GestaoDeReservas reservas, CancellationToken cancellationToken) =>
                Results.Ok((await reservas.ListarAsync(request, dia, cancellationToken)).Select(Dto).ToList()))
            .WithName("listReservas")
            .RequireSession()
            .RequirePermission(ReservaPermissions.Ver)
            .Produces<List<ReservaDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/reservas", async (CriarReservaRequest body, RequestContextAccessor request, GestaoDeReservas reservas, CancellationToken cancellationToken) =>
            {
                var r = await reservas.CriarAsync(request, body.Nome, body.Telefone, body.Pessoas ?? 0, body.Horario, body.MesaId, body.Observacao, cancellationToken);
                return r.Resultado == ResultadoDeReserva.Ok ? Results.Json(Dto(r.Reserva!), statusCode: StatusCodes.Status201Created) : Erro(r.Resultado);
            })
            .WithName("criarReserva")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ReservaPermissions.Gerenciar)
            .Produces<ReservaDto>(StatusCodes.Status201Created)
            .Produces<ReservaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ReservaErro>(StatusCodes.Status404NotFound)
            .Produces<ReservaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/reservas/{reservaId:guid}/chegou", async (Guid reservaId, RequestContextAccessor request, GestaoDeReservas reservas, CancellationToken cancellationToken) =>
            {
                var r = await reservas.ChegouAsync(request, reservaId, cancellationToken);
                return r.Resultado == ResultadoDeReserva.Ok ? Results.Ok(new ChegouDaReservaDto(Dto(r.Reserva!), r.Reserva!.ComandaId!.Value, r.Repetida)) : Erro(r.Resultado);
            })
            .WithName("reservaChegou")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ReservaPermissions.Chegou)
            .Produces<ChegouDaReservaDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ReservaErro>(StatusCodes.Status404NotFound)
            .Produces<ReservaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/reservas/{reservaId:guid}/cancelar", async (Guid reservaId, RequestContextAccessor request, GestaoDeReservas reservas, CancellationToken cancellationToken) =>
            {
                var r = await reservas.CancelarAsync(request, reservaId, cancellationToken);
                return r.Resultado == ResultadoDeReserva.Ok ? Results.Ok(Dto(r.Reserva!)) : Erro(r.Resultado);
            })
            .WithName("cancelarReserva")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ReservaPermissions.Gerenciar)
            .Produces<ReservaDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ReservaErro>(StatusCodes.Status404NotFound)
            .Produces<ReservaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/reservas/{reservaId:guid}/nao-compareceu", async (Guid reservaId, RequestContextAccessor request, GestaoDeReservas reservas, CancellationToken cancellationToken) =>
            {
                var r = await reservas.MarcarNaoCompareceuAsync(request, reservaId, cancellationToken);
                return r.Resultado == ResultadoDeReserva.Ok ? Results.Ok(Dto(r.Reserva!)) : Erro(r.Resultado);
            })
            .WithName("reservaNaoCompareceu")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ReservaPermissions.Gerenciar)
            .Produces<ReservaDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ReservaErro>(StatusCodes.Status404NotFound)
            .Produces<ReservaErro>(StatusCodes.Status409Conflict);
    }

    private static ReservaDto Dto(Reserva r) =>
        new(r.Id, r.MesaId, r.Nome, r.Telefone, r.Pessoas, r.Horario, r.DiaOperacional, r.Observacao, RegrasDeReserva.Palavra(r.Estado), r.ComandaId);

    private static IResult Erro(ResultadoDeReserva resultado) => resultado switch
    {
        ResultadoDeReserva.NaoEncontrada or ResultadoDeReserva.MesaNaoEncontrada => Results.Json(new ReservaErro("nao-encontrada"), statusCode: StatusCodes.Status404NotFound),
        ResultadoDeReserva.Sobreposta => Results.Json(new ReservaErro("sobreposta"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDeReserva.ForaDoHorario => Results.Json(new ReservaErro("fora-do-horario"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDeReserva.MesasDesligadas => Results.Json(new ReservaErro("mesas-desligadas"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDeReserva.EstadoInvalido => Results.Json(new ReservaErro("estado-invalido"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDeReserva.MesaIndisponivel => Results.Json(new ReservaErro("mesa-indisponivel"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDeReserva.Negado => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.Json(new ReservaErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
