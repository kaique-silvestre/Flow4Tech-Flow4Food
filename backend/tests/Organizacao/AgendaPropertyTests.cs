using Flow4Tech.Organizacao.Domain;
using FsCheck.Xunit;

namespace Flow4Tech.Organizacao.Tests;

/// <summary>Property tests (F4-0143) of the opening window and of the compact text forms the agenda is stored in.</summary>
public sealed class AgendaPropertyTests
{
    private static readonly DateOnly Dia = new(2026, 11, 16);

    private static TimeOnly Hora(int minutos) => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Abs(minutos % 1440)));

    [Property]
    public bool A_window_contains_exactly_the_instants_between_opening_and_closing(int abre, int fecha, int instante)
    {
        var janela = new JanelaDeFuncionamento(Hora(abre), Hora(fecha));
        var local = Dia.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Abs(instante % 2880));
        var esperado = janela.DiaInteiro || (local >= Dia.ToDateTime(janela.Abre) && local < janela.FimEm(Dia));
        return janela.Contem(Dia, local) == esperado;
    }

    [Property]
    public bool A_window_with_equal_times_never_blocks_and_one_never_contains_its_closing_instant(int abre, int fecha)
    {
        var inteira = new JanelaDeFuncionamento(Hora(abre), Hora(abre));
        var janela = new JanelaDeFuncionamento(Hora(abre), Hora(fecha));
        return inteira.Contem(Dia, Dia.ToDateTime(TimeOnly.MinValue).AddHours(30)) && (janela.DiaInteiro || !janela.Contem(Dia, janela.FimEm(Dia)));
    }

    [Property]
    public bool The_week_text_form_round_trips(int[] minutos)
    {
        ArgumentNullException.ThrowIfNull(minutos);
        var semana = Enumerable.Range(0, 7)
            .Select(i => minutos.Length < 2 || i % 3 == 0 ? (JanelaDeFuncionamento?)null : new JanelaDeFuncionamento(Hora(minutos[i % minutos.Length]), Hora(minutos[(i + 1) % minutos.Length])))
            .ToArray();
        var texto = AgendaDeFuncionamento.FormatarSemana(semana);
        return AgendaDeFuncionamento.TentarSemana(texto, out var lida) && lida.SequenceEqual(semana) && texto.Length <= 200;
    }

    [Property]
    public bool An_exception_beats_the_week_and_an_extension_only_moves_the_closing_of_its_own_day(int abre, int fecha, int nova)
    {
        var semana = Enumerable.Repeat<JanelaDeFuncionamento?>(new JanelaDeFuncionamento(Hora(abre), Hora(fecha)), 7).ToArray();
        var excecao = new JanelaDeFuncionamento(new TimeOnly(1, 0), new TimeOnly(2, 0));
        var excecoes = new Dictionary<DateOnly, JanelaDeFuncionamento?> { [Dia] = excecao, [Dia.AddDays(1)] = null };
        var extensao = (Dia.AddDays(2), Hora(nova));
        var daSemana = AgendaDeFuncionamento.Efetiva(Dia.AddDays(3), semana, excecoes, extensao) == semana[0];
        var fechadoPelaExcecao = AgendaDeFuncionamento.Efetiva(Dia.AddDays(1), semana, excecoes, extensao) is null;
        var estendido = AgendaDeFuncionamento.Efetiva(Dia.AddDays(2), semana, excecoes, extensao) is { } j && (semana[0]!.Value.DiaInteiro ? j == semana[0] : j.Fecha == Hora(nova) && j.Abre == Hora(abre));
        return AgendaDeFuncionamento.Efetiva(Dia, semana, excecoes, null) == excecao && daSemana && fechadoPelaExcecao && estendido;
    }

    [Property]
    public bool The_operating_day_changes_exactly_at_the_rollover(int virada)
    {
        var v = Hora(virada);
        var antes = Dia.ToDateTime(v).AddMinutes(-1);
        return AgendaDeFuncionamento.DiaOperacional(Dia.ToDateTime(v), v) == Dia && AgendaDeFuncionamento.DiaOperacional(antes, v) == Dia.AddDays(-1);
    }
}
