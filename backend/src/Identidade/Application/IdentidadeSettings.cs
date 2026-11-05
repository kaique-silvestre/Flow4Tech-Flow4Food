using System.Globalization;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// The options of this module (spec 5.7.5): the two session clocks and the login lock policy. The SYSTEM layer of each is what the configuration used to be (the old
/// <c>Flow4Tech:Sessoes:*</c> and <c>Flow4Tech:Identidade:Bloqueio:*</c> keys still work as <see cref="SettingDefinition.ConfigPath"/>). The session clocks may be set per organization.
/// The lock policy is SYSTEM ONLY on purpose: the limiter judges an attempt before any lookup, when the organization of an e-mail account is not known yet, and an address has no
/// organization at all; a per-organization value would never be read.
/// </summary>
[SettingsCatalog]
public static class IdentidadeSettings
{
    // ---- session clocks (spec 5.3.4). New sessions read them when they start; the inactivity window is read whenever a session slides it. ----

    public static readonly SettingDefinition SessaoInatividadeHoras = new(
        "sessao.inatividade-horas",
        SettingType.Numero,
        "12",
        SettingLayers.Organizacao,
        "Horas sem atividade ate a sessao encerrar.")
    {
        Minimum = 0.1m,
        Maximum = 24 * 90,
        ConfigPath = "Flow4Tech:Sessoes:InatividadeHoras",
    };

    public static readonly SettingDefinition SessaoAbsolutaDias = new(
        "sessao.absoluta-dias",
        SettingType.Numero,
        "14",
        SettingLayers.Organizacao,
        "Dias desde o inicio ate a sessao encerrar, com ou sem atividade. Vale para as sessoes que comecam depois da mudanca.")
    {
        Minimum = 0.01m,
        Maximum = 365,
        ConfigPath = "Flow4Tech:Sessoes:AbsolutaDias",
    };

    public static readonly SettingDefinition SessaoIntervaloDeAtividadeSegundos = new(
        "sessao.intervalo-atividade-segundos",
        SettingType.Inteiro,
        "60",
        SettingLayers.SomenteSistema,
        "A atividade da sessao e gravada no maximo uma vez por este intervalo.")
    {
        Minimum = 0,
        Maximum = 3600,
        ConfigPath = "Flow4Tech:Sessoes:IntervaloDeAtividadeSegundos",
    };

    // ---- login lock policy (spec 5.5), system layer only ----

    public static readonly SettingDefinition BloqueioFalhasEmail = Bloqueio("falhas-email", SettingType.Inteiro, "5", "FalhasEmail", "Falhas seguidas ate bloquear uma conta com e-mail.", 1, 100);

    public static readonly SettingDefinition BloqueioFalhasOperacional = Bloqueio("falhas-operacional", SettingType.Inteiro, "8", "FalhasOperacional", "Falhas seguidas ate bloquear uma conta operacional.", 1, 100);

    public static readonly SettingDefinition BloqueioBaseSegundos = Bloqueio("base-segundos", SettingType.Numero, "30", "BloqueioBaseSegundos", "Duracao do primeiro bloqueio, em segundos; dobra a cada nova falha.", 1, 86_400);

    public static readonly SettingDefinition BloqueioMaximoMinutos = Bloqueio("maximo-minutos", SettingType.Numero, "15", "BloqueioMaximoMinutos", "Teto da duracao de um bloqueio progressivo, em minutos.", 1, 10_080);

    public static readonly SettingDefinition BloqueioFalhasPorIp = Bloqueio("falhas-por-ip", SettingType.Inteiro, "30", "FalhasPorIp", "Falhas de quaisquer contas, vindas de um endereco, ate bloquear o endereco.", 1, 10_000);

    public static readonly SettingDefinition BloqueioContasPorIp = Bloqueio("contas-por-ip", SettingType.Inteiro, "10", "ContasPorIp", "Contas diferentes falhando a partir de um endereco (credential stuffing).", 2, 10_000);

    public static readonly SettingDefinition BloqueioIpsPorConta = Bloqueio("ips-por-conta", SettingType.Inteiro, "5", "IpsPorConta", "Enderecos diferentes falhando contra uma conta (credential stuffing).", 2, 10_000);

    private static SettingDefinition Bloqueio(string name, SettingType type, string padrao, string legacy, string description, decimal min, decimal max) =>
        new("bloqueio." + name, type, padrao, SettingLayers.SomenteSistema, description)
        {
            Minimum = min,
            Maximum = max,
            ConfigPath = "Flow4Tech:Identidade:Bloqueio:" + legacy,
        };
}

/// <summary>Reads the options of this module into the shapes the services use.</summary>
public static class ConfiguracoesDeIdentidade
{
    /// <summary>
    /// The lock thresholds: <paramref name="baseline"/> (the knobs that are not options, and the values a test or an embedding host sets in code) with the seven exposed options taken from
    /// the SYSTEM layer. Read when the limiter is built, which the host does late (after every configuration source is loaded).
    /// </summary>
    public static LimitesDeLogin Limites(ISettings settings, LimitesDeLogin? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var limites = (baseline ?? new LimitesDeLogin()) with
        {
            FalhasParaBloquearEmail = Int(settings, IdentidadeSettings.BloqueioFalhasEmail),
            FalhasParaBloquearOperacional = Int(settings, IdentidadeSettings.BloqueioFalhasOperacional),
            BloqueioBase = TimeSpan.FromSeconds(Num(settings, IdentidadeSettings.BloqueioBaseSegundos)),
            BloqueioMaximo = TimeSpan.FromMinutes(Num(settings, IdentidadeSettings.BloqueioMaximoMinutos)),
            FalhasParaBloquearIp = Int(settings, IdentidadeSettings.BloqueioFalhasPorIp),
            ContasDistintasPorIp = Int(settings, IdentidadeSettings.BloqueioContasPorIp),
            IpsDistintosPorConta = Int(settings, IdentidadeSettings.BloqueioIpsPorConta),
        };
        limites.Validar();
        return limites;
    }

    /// <summary>The system value of the interval at which activity is written (no database: this runs on every request).</summary>
    public static TimeSpan IntervaloDeAtividade(ISettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return TimeSpan.FromSeconds(Int(settings, IdentidadeSettings.SessaoIntervaloDeAtividadeSegundos));
    }

    /// <summary>The two clocks of a session of <paramref name="organizationId"/> as they apply at <paramref name="at"/>: the organization's value, else the system's.</summary>
    public static async Task<RelogiosDaSessao> RelogiosAsync(ISettings settings, Guid organizationId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var inatividade = await settings.ResolveAsync(IdentidadeSettings.SessaoInatividadeHoras, organizationId, null, at, cancellationToken);
        var absoluta = await settings.ResolveAsync(IdentidadeSettings.SessaoAbsolutaDias, organizationId, null, at, cancellationToken);
        return new RelogiosDaSessao(
            TimeSpan.FromTicks((long)Math.Round(inatividade.AsDecimal() * TimeSpan.TicksPerHour)),
            TimeSpan.FromTicks((long)Math.Round(absoluta.AsDecimal() * TimeSpan.TicksPerDay)),
            IntervaloDeAtividade(settings));
    }

    /// <summary>The system-layer text of a clock, for the code-set options of a host that has no stored layers (tests): hours and days as invariant decimals.</summary>
    public static string Horas(TimeSpan value) => (value.Ticks / (decimal)TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture);

    public static string Dias(TimeSpan value) => (value.Ticks / (decimal)TimeSpan.TicksPerDay).ToString(CultureInfo.InvariantCulture);

    private static int Int(ISettings settings, SettingDefinition d) => int.Parse(settings.SystemValue(d), CultureInfo.InvariantCulture);

    private static double Num(ISettings settings, SettingDefinition d) => (double)decimal.Parse(settings.SystemValue(d), CultureInfo.InvariantCulture);
}
