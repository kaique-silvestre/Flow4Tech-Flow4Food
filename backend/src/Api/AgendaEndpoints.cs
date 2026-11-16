using System.Globalization;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Organizacao.Domain;

namespace Flow4Tech.Api;

/// <summary>One opening window of an operating day: local times <c>HH:mm</c>. Closing before opening means after midnight; equal times mean the whole operating day.</summary>
internal sealed record JanelaDto(string Abre, string Fecha);

internal sealed record ExcecaoDto(DateOnly Dia, JanelaDto? Janela);

/// <summary>The agenda of the ACTIVE unit now. <c>Semana</c> has seven entries, Monday first; null = closed that day. All values are what the server resolved (organization, then unit).</summary>
internal sealed record AgendaDto(
    DateOnly DiaOperacional,
    string Fuso,
    string Virada,
    List<JanelaDto?> Semana,
    List<ExcecaoDto> Excecoes,
    string? EstendidoAte,
    JanelaDto? JanelaDeHoje,
    bool AbertoAgora);

internal sealed record SalvarSemanaRequest(List<JanelaDto?>? Semana, string? Motivo);

internal sealed record SalvarExcecoesRequest(List<ExcecaoDto>? Excecoes, string? Motivo);

internal sealed record EstenderHojeRequest(string? Fecha, string? Motivo);

internal sealed record AgendaErro(string Codigo);

internal static class AgendaEndpoints
{
    private const long Body = 4096;

    public static void MapAgenda(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/agenda", async (RequestContextAccessor request, AgendaDaUnidade agenda, CancellationToken cancellationToken) =>
            {
                var atual = await agenda.ObterAsync(request, cancellationToken);
                return atual is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(Dto(atual));
            })
            .WithName("getAgenda")
            .RequireSession()
            .RequirePermission(OrganizacaoPermissions.Configurar)
            .Produces<AgendaDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPut("/agenda/semana", async (SalvarSemanaRequest body, RequestContextAccessor request, AgendaDaUnidade agenda, CancellationToken cancellationToken) =>
            {
                if (body.Semana is not { Count: 7 } semana || !TryJanelas(semana, out var janelas))
                {
                    return Erro(ResultadoDaAgenda.Invalido);
                }

                return Erro(await agenda.SalvarSemanaAsync(request, janelas, body.Motivo, cancellationToken));
            })
            .WithName("salvarAgendaSemana")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(OrganizacaoPermissions.Configurar, sensitive: true)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AgendaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<AgendaErro>(StatusCodes.Status409Conflict);

        app.MapPut("/agenda/excecoes", async (SalvarExcecoesRequest body, RequestContextAccessor request, AgendaDaUnidade agenda, CancellationToken cancellationToken) =>
            {
                var lista = new List<ExcecaoDaAgenda>();
                foreach (var e in body.Excecoes ?? [])
                {
                    if (e is null || !TryJanela(e.Janela, out var janela))
                    {
                        return Erro(ResultadoDaAgenda.Invalido);
                    }

                    lista.Add(new ExcecaoDaAgenda(e.Dia, janela));
                }

                return Erro(await agenda.SalvarExcecoesAsync(request, lista, body.Motivo, cancellationToken));
            })
            .WithName("salvarAgendaExcecoes")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(OrganizacaoPermissions.Configurar, sensitive: true)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AgendaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<AgendaErro>(StatusCodes.Status409Conflict);

        app.MapPost("/agenda/estender-hoje", async (EstenderHojeRequest body, RequestContextAccessor request, AgendaDaUnidade agenda, CancellationToken cancellationToken) =>
            {
                if (!TimeOnly.TryParseExact(body.Fecha, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
                {
                    return Erro(ResultadoDaAgenda.Invalido);
                }

                return Erro(await agenda.EstenderHojeAsync(request, fecha, body.Motivo, cancellationToken));
            })
            .WithName("estenderAgendaHoje")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(OrganizacaoPermissions.Configurar, sensitive: true)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AgendaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<AgendaErro>(StatusCodes.Status409Conflict);
    }

    private static AgendaDto Dto(AgendaAtual a) => new(
        a.DiaOperacional,
        a.Fuso,
        Hora(a.Virada),
        [.. a.Semana.Select(Dto)],
        [.. a.Excecoes.Select(e => new ExcecaoDto(e.Dia, Dto(e.Janela)))],
        a.EstendidoAte is { } ate ? Hora(ate) : null,
        Dto(a.JanelaDeHoje),
        a.AbertoAgora);

    private static JanelaDto? Dto(JanelaDeFuncionamento? j) => j is { } w ? new JanelaDto(Hora(w.Abre), Hora(w.Fecha)) : null;

    private static string Hora(TimeOnly hora) => hora.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static bool TryJanela(JanelaDto? dto, out JanelaDeFuncionamento? janela)
    {
        janela = null;
        if (dto is null)
        {
            return true;
        }

        if (!TimeOnly.TryParseExact(dto.Abre, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var abre) || !TimeOnly.TryParseExact(dto.Fecha, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
        {
            return false;
        }

        janela = new JanelaDeFuncionamento(abre, fecha);
        return true;
    }

    private static bool TryJanelas(List<JanelaDto?> dtos, out List<JanelaDeFuncionamento?> janelas)
    {
        janelas = [];
        foreach (var dto in dtos)
        {
            if (!TryJanela(dto, out var janela))
            {
                return false;
            }

            janelas.Add(janela);
        }

        return true;
    }

    /// <summary>Denied: 403 with an EMPTY body (no reason). Other refusals name only what is wrong with the request itself.</summary>
    private static IResult Erro(ResultadoDaAgenda resultado) => resultado switch
    {
        ResultadoDaAgenda.Ok => Results.NoContent(),
        ResultadoDaAgenda.Negado => Results.StatusCode(StatusCodes.Status403Forbidden),
        ResultadoDaAgenda.Travada => Results.Json(new AgendaErro("travada"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDaAgenda.SemHorarioParaEstender => Results.Json(new AgendaErro("sem-horario-para-estender"), statusCode: StatusCodes.Status409Conflict),
        ResultadoDaAgenda.ExtensaoInvalida => Results.Json(new AgendaErro("extensao-invalida"), statusCode: StatusCodes.Status400BadRequest),
        ResultadoDaAgenda.ExcecoesDemais => Results.Json(new AgendaErro("excecoes-demais"), statusCode: StatusCodes.Status400BadRequest),
        ResultadoDaAgenda.SemUnidadeAtiva => Results.Json(new AgendaErro("sem-unidade-ativa"), statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new AgendaErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
