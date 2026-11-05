using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Ticket 10: the session clocks and the lock thresholds are options declared in code (system layer = configuration or the declared default). No database.</summary>
public sealed class ConfiguracoesDeIdentidadeTests
{
    private static SystemSettings Sistema(Dictionary<string, string>? configurado = null) =>
        new(d => configurado is not null && configurado.TryGetValue(d.SystemConfigPath, out var v) ? v : null);

    [Fact]
    public void The_declared_defaults_are_the_values_the_code_had_before()
    {
        var antes = new LimitesDeLogin();
        var limites = ConfiguracoesDeIdentidade.Limites(Sistema());

        Assert.Equal(antes, limites);
        Assert.Equal(TimeSpan.FromMinutes(1), ConfiguracoesDeIdentidade.IntervaloDeAtividade(Sistema()));
        var padrao = new SessoesOptions();
        Assert.Equal(padrao.Inatividade, TimeSpan.FromHours(double.Parse(IdentidadeSettings.SessaoInatividadeHoras.Default, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(padrao.Absoluta, TimeSpan.FromDays(double.Parse(IdentidadeSettings.SessaoAbsolutaDias.Default, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task The_old_configuration_keys_are_the_system_layer()
    {
        var sistema = Sistema(new()
        {
            ["Flow4Tech:Identidade:Bloqueio:FalhasOperacional"] = "3",
            ["Flow4Tech:Identidade:Bloqueio:BloqueioBaseSegundos"] = "45",
            ["Flow4Tech:Identidade:Bloqueio:BloqueioMaximoMinutos"] = "20",
            ["Flow4Tech:Sessoes:InatividadeHoras"] = "1.5",
            ["Flow4Tech:Sessoes:AbsolutaDias"] = "2",
            ["Flow4Tech:Sessoes:IntervaloDeAtividadeSegundos"] = "10",
        });

        var limites = ConfiguracoesDeIdentidade.Limites(sistema);
        var relogios = await ConfiguracoesDeIdentidade.RelogiosAsync(sistema, Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal((3, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(20)), (limites.FalhasParaBloquearOperacional, limites.BloqueioBase, limites.BloqueioMaximo));
        Assert.Equal(new RelogiosDaSessao(TimeSpan.FromHours(1.5), TimeSpan.FromDays(2), TimeSpan.FromSeconds(10)), relogios);
    }

    [Fact]
    public void The_baseline_keeps_the_knobs_that_are_not_options()
    {
        var baseline = new LimitesDeLogin { FalhasParaCaptchaConta = 100, JanelaDeFalhas = TimeSpan.FromMinutes(5) };

        var limites = ConfiguracoesDeIdentidade.Limites(Sistema(new() { ["Flow4Tech:Identidade:Bloqueio:FalhasEmail"] = "7" }), baseline);

        Assert.Equal((7, 100, TimeSpan.FromMinutes(5)), (limites.FalhasParaBloquearEmail, limites.FalhasParaCaptchaConta, limites.JanelaDeFalhas));
    }

    [Fact]
    public void A_configured_threshold_that_is_not_valid_stops_the_host_instead_of_being_ignored()
    {
        Assert.Throws<InvalidOperationException>(() => ConfiguracoesDeIdentidade.Limites(Sistema(new() { ["Flow4Tech:Identidade:Bloqueio:FalhasEmail"] = "0" })));
        Assert.Throws<InvalidOperationException>(() => ConfiguracoesDeIdentidade.Limites(Sistema(new() { ["Flow4Tech:Identidade:Bloqueio:FalhasEmail"] = "muitas" })));
    }

    [Fact]
    public void The_lock_policy_is_system_only_and_the_session_clocks_can_be_set_by_organization()
    {
        Assert.All(
            new[] { IdentidadeSettings.BloqueioFalhasEmail, IdentidadeSettings.BloqueioFalhasOperacional, IdentidadeSettings.BloqueioBaseSegundos, IdentidadeSettings.BloqueioMaximoMinutos, IdentidadeSettings.BloqueioFalhasPorIp, IdentidadeSettings.BloqueioContasPorIp, IdentidadeSettings.BloqueioIpsPorConta, IdentidadeSettings.SessaoIntervaloDeAtividadeSegundos },
            d => Assert.Equal(SettingLayers.SomenteSistema, d.Layers));
        Assert.Equal(SettingLayers.Organizacao, IdentidadeSettings.SessaoInatividadeHoras.Layers);
        Assert.Equal(SettingLayers.Organizacao, IdentidadeSettings.SessaoAbsolutaDias.Layers);
    }
}
