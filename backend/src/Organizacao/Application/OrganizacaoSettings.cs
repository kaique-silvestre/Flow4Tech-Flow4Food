using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Domain;

namespace Flow4Tech.Organizacao.Application;

/// <summary>
/// The options of the operating day (spec 5.7.5) and the opening agenda of a unit (F4-0143). The host's operating-day resolver and <see cref="AgendaDaUnidade"/> read them. The zone and the
/// rollover require an explicit start instant, because moving the rollover would otherwise re-label the day of operations already made. The agenda options are texts in the compact forms of
/// <see cref="Flow4Tech.Organizacao.Domain.AgendaDeFuncionamento"/>; the default (open the whole operating day, no exception, no extension) blocks nothing.
/// </summary>
[SettingsCatalog]
public static class OrganizacaoSettings
{
    public static readonly SettingDefinition FusoHorario = new(
        "dia-operacional.fuso",
        SettingType.Texto,
        "America/Sao_Paulo",
        SettingLayers.OrganizacaoEUnidade,
        "Fuso horario (IANA) em que o dia operacional da unidade e contado.")
    {
        Validator = EhFusoConhecido,
        RequiresEffectiveInstant = true,
    };

    public static readonly SettingDefinition ViradaDoDia = new(
        "dia-operacional.virada",
        SettingType.Hora,
        "05:00",
        SettingLayers.OrganizacaoEUnidade,
        "Hora local (HH:mm) em que comeca o proximo dia operacional.")
    {
        RequiresEffectiveInstant = true,
    };

    public static readonly SettingDefinition Semana = new(
        "dia-operacional.semana",
        SettingType.Texto,
        AgendaDeFuncionamento.SemanaPadrao,
        SettingLayers.OrganizacaoEUnidade,
        "Semana padrao: sete janelas HHmm-HHmm separadas por ponto e virgula, segunda primeiro; - fecha o dia.")
    {
        Validator = texto => AgendaDeFuncionamento.TentarSemana(texto, out _),
    };

    public static readonly SettingDefinition Excecoes = new(
        "dia-operacional.excecoes",
        SettingType.Texto,
        AgendaDeFuncionamento.Nenhuma,
        SettingLayers.OrganizacaoEUnidade,
        "Excecoes por dia operacional: yyMMdd=HHmm-HHmm (ou yyMMdd=- para fechado), separadas por ponto e virgula.")
    {
        Validator = texto => AgendaDeFuncionamento.TentarExcecoes(texto, out _),
    };

    public static readonly SettingDefinition EstenderHoje = new(
        "dia-operacional.estender-hoje",
        SettingType.Texto,
        AgendaDeFuncionamento.Nenhuma,
        SettingLayers.OrganizacaoEUnidade,
        "Novo fechamento do dia operacional corrente (yyMMdd=HHmm); vale so ate o fim desse dia operacional.")
    {
        Validator = texto => texto == AgendaDeFuncionamento.Nenhuma || AgendaDeFuncionamento.TentarExtensao(texto, out _, out _),
    };

    private static bool EhFusoConhecido(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
