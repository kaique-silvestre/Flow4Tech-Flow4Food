using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Organizacao.Application;

/// <summary>
/// The options of the operating day (spec 5.7.5): the place where roadmap 2.10b will persist the opening rules of a unit. TODAY nothing reads them: <c>ConfiguredOperatingDayResolver</c> keeps
/// answering from its own default, on purpose. They already require an explicit start instant, because moving the rollover would otherwise re-label the day of operations already made.
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
