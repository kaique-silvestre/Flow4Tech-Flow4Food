using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Domain;

namespace Flow4Tech.Organizacao.Application;

public enum ResultadoDaAgenda
{
    Ok,

    /// <summary>The caller lacks <c>organizacao:configurar</c> (403, no reason to the client).</summary>
    Negado,

    Invalido,

    SemUnidadeAtiva,

    /// <summary>The organization locked the option: the unit may not change it.</summary>
    Travada,

    /// <summary>More exceptions than <see cref="AgendaDeFuncionamento.MaximoDeExcecoes"/> (a layered setting is one short text).</summary>
    ExcecoesDemais,

    /// <summary>Today is closed or open the whole day: there is no closing to move.</summary>
    SemHorarioParaEstender,

    /// <summary>The new closing is not later than the current one or than now, or goes past the end of the operating day.</summary>
    ExtensaoInvalida,
}

public sealed record ExcecaoDaAgenda(DateOnly Dia, JanelaDeFuncionamento? Janela);

/// <summary>The agenda of the active unit as the server reads it NOW: the layers resolved, plus what that means for the current operating day.</summary>
public sealed record AgendaAtual(
    DateOnly DiaOperacional,
    string Fuso,
    TimeOnly Virada,
    IReadOnlyList<JanelaDeFuncionamento?> Semana,
    IReadOnlyList<ExcecaoDaAgenda> Excecoes,
    TimeOnly? EstendidoAte,
    JanelaDeFuncionamento? JanelaDeHoje,
    bool AbertoAgora);

/// <summary>
/// The opening agenda of a unit (F4-0143): the weekly default, the exceptions by operating day and "extend today", all stored as layered <c>dia-operacional.*</c> options, so a change is a
/// new version with history and audit (<see cref="Configuracoes"/>, ONE unit of work, the unit is the session's ACTIVE one). Reads take the instant from the server clock; no time comes
/// from the client. <see cref="EstaAbertoAsync"/> is the question the launching handlers ask; it needs no permission because the caller is serving a request already authorized by its own route.
/// </summary>
public sealed class AgendaDaUnidade(IUnitOfWork unitOfWork, ISettings settings, Configuracoes configuracoes, IPermissaoDoChamador permissoes, IClock clock)
{
    public async Task<DateOnly> DiaOperacionalAsync(Guid organizationId, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        (await LerAsync(organizationId, unitId, at, cancellationToken)).Dia;

    /// <summary>The instant on the unit's wall clock (its configured time zone): what a promotion's days and time window are judged on.</summary>
    public async Task<DateTime> HoraLocalAsync(Guid organizationId, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        (await LerAsync(organizationId, unitId, at, cancellationToken)).Local;

    public async Task<bool> EstaAbertoAsync(Guid organizationId, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var estado = await LerAsync(organizationId, unitId, at, cancellationToken);
        return estado.Janela is { } janela && janela.Contem(estado.Dia, estado.Local);
    }

    /// <summary>The agenda of the active unit now. Null when the caller may not configure.</summary>
    public Task<AgendaAtual?> ObterAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<AgendaAtual?>(
            request,
            async ct =>
            {
                if (!await permissoes.PodeAsync(request, OrganizacaoPermissions.Configurar, ct))
                {
                    return null;
                }

                var unit = Unidade(request);
                var estado = await LerAsync(request.Organization.OrganizationId, unit, clock.UtcNow, ct);
                return new AgendaAtual(
                    estado.Dia,
                    estado.Fuso,
                    estado.Virada,
                    estado.Semana,
                    [.. estado.Excecoes.Select(e => new ExcecaoDaAgenda(e.Key, e.Value))],
                    estado.Extensao is { } e && e.Dia == estado.Dia ? e.Fecha : null,
                    estado.Janela,
                    estado.Janela is { } janela && janela.Contem(estado.Dia, estado.Local));
            },
            cancellationToken);
    }

    public async Task<ResultadoDaAgenda> SalvarSemanaAsync(IRequestContext request, IReadOnlyList<JanelaDeFuncionamento?> semana, string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(semana);
        if (semana.Count != 7)
        {
            return ResultadoDaAgenda.Invalido;
        }

        var estado = await LerAsync(request.Organization.OrganizationId, Unidade(request), clock.UtcNow, cancellationToken);
        return semana.Any(j => j is { } w && !w.FuncionaCom(estado.Virada))
            ? ResultadoDaAgenda.Invalido
            : await Escrever(request, OrganizacaoSettings.Semana, AgendaDeFuncionamento.FormatarSemana(semana), motivo, cancellationToken);
    }

    /// <summary>Replaces the exceptions of the unit. Dates already past (before the current operating day) are dropped: they no longer mean anything and would eat the room.</summary>
    public async Task<ResultadoDaAgenda> SalvarExcecoesAsync(IRequestContext request, IReadOnlyList<ExcecaoDaAgenda> excecoes, string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(excecoes);
        if (excecoes.Select(e => e.Dia).Distinct().Count() != excecoes.Count)
        {
            return ResultadoDaAgenda.Invalido;
        }

        var estado = await LerAsync(request.Organization.OrganizationId, Unidade(request), clock.UtcNow, cancellationToken);
        if (excecoes.Any(e => !AgendaDeFuncionamento.DataArmazenavel(e.Dia) || (e.Janela is { } w && !w.FuncionaCom(estado.Virada))))
        {
            return ResultadoDaAgenda.Invalido;
        }

        var futuras = excecoes.Where(e => e.Dia >= estado.Dia).ToDictionary(e => e.Dia, e => e.Janela);
        return futuras.Count > AgendaDeFuncionamento.MaximoDeExcecoes
            ? ResultadoDaAgenda.ExcecoesDemais
            : await Escrever(request, OrganizacaoSettings.Excecoes, AgendaDeFuncionamento.FormatarExcecoes(futuras), motivo, cancellationToken);
    }

    /// <summary>Moves the closing of the CURRENT operating day to <paramref name="fecha"/>. The extension is stored with that day's date, so it stops mattering when the day ends.</summary>
    public async Task<ResultadoDaAgenda> EstenderHojeAsync(IRequestContext request, TimeOnly fecha, string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var estado = await LerAsync(request.Organization.OrganizationId, Unidade(request), clock.UtcNow, cancellationToken);
        if (estado.Janela is not { } atual || atual.DiaInteiro)
        {
            return ResultadoDaAgenda.SemHorarioParaEstender;
        }

        var nova = atual with { Fecha = fecha };
        var fim = nova.FimEm(estado.Dia);
        if (nova.DiaInteiro || fim <= atual.FimEm(estado.Dia) || fim <= estado.Local || fim > estado.Dia.AddDays(1).ToDateTime(estado.Virada))
        {
            return ResultadoDaAgenda.ExtensaoInvalida;
        }

        return await Escrever(request, OrganizacaoSettings.EstenderHoje, AgendaDeFuncionamento.FormatarExtensao(estado.Dia, fecha), motivo, cancellationToken);
    }

    private static Guid? Unidade(IRequestContext request) => request.Organization.UnitId == Guid.Empty ? null : request.Organization.UnitId;

    private async Task<ResultadoDaAgenda> Escrever(IRequestContext request, SettingDefinition definicao, string valor, string? motivo, CancellationToken cancellationToken)
    {
        var resultado = await configuracoes.AlterarAsync(request, new AlterarConfiguracao(definicao.Key, CamadaDeEscrita.Unidade, valor, null, null, motivo), cancellationToken);
        return resultado switch
        {
            ResultadoConfiguracao.Ok => ResultadoDaAgenda.Ok,
            ResultadoConfiguracao.Negado => ResultadoDaAgenda.Negado,
            ResultadoConfiguracao.Travada => ResultadoDaAgenda.Travada,
            ResultadoConfiguracao.SemUnidadeAtiva => ResultadoDaAgenda.SemUnidadeAtiva,
            _ => ResultadoDaAgenda.Invalido,
        };
    }

    private async Task<Estado> LerAsync(Guid organizationId, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var unit = unitId is { } id && id != Guid.Empty ? id : (Guid?)null;
        var fuso = (await settings.ResolveAsync(OrganizacaoSettings.FusoHorario, organizationId, unit, at, cancellationToken)).Value;
        var virada = TimeOnly.ParseExact((await settings.ResolveAsync(OrganizacaoSettings.ViradaDoDia, organizationId, unit, at, cancellationToken)).Value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var semana = AgendaDeFuncionamento.TentarSemana((await settings.ResolveAsync(OrganizacaoSettings.Semana, organizationId, unit, at, cancellationToken)).Value, out var s)
            ? s
            : [.. Enumerable.Repeat<JanelaDeFuncionamento?>(new JanelaDeFuncionamento(default, default), 7)];
        _ = AgendaDeFuncionamento.TentarExcecoes((await settings.ResolveAsync(OrganizacaoSettings.Excecoes, organizationId, unit, at, cancellationToken)).Value, out var excecoes);
        (DateOnly Dia, TimeOnly Fecha)? extensao = AgendaDeFuncionamento.TentarExtensao((await settings.ResolveAsync(OrganizacaoSettings.EstenderHoje, organizationId, unit, at, cancellationToken)).Value, out var diaDaExtensao, out var fecha)
            ? (diaDaExtensao, fecha)
            : null;

        var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(fuso)).DateTime;
        var dia = AgendaDeFuncionamento.DiaOperacional(local, virada);
        return new Estado(fuso, virada, dia, local, semana, excecoes, extensao, AgendaDeFuncionamento.Efetiva(dia, semana, excecoes, extensao));
    }

    private sealed record Estado(
        string Fuso,
        TimeOnly Virada,
        DateOnly Dia,
        DateTime Local,
        IReadOnlyList<JanelaDeFuncionamento?> Semana,
        SortedDictionary<DateOnly, JanelaDeFuncionamento?> Excecoes,
        (DateOnly Dia, TimeOnly Fecha)? Extensao,
        JanelaDeFuncionamento? Janela);
}
