using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Domain;

namespace Flow4Tech.Organizacao.Tests;

/// <summary>The resolution rules of the layered settings (spec 5.7), pure: no database.</summary>
public sealed class ConfiguracaoResolucaoTests
{
    private static readonly SettingDefinition Opcao = new("teste.limite", SettingType.Inteiro, "10", SettingLayers.OrganizacaoEUnidade, "limite de teste");
    private static readonly SettingDefinition SoOrganizacao = new("teste.org", SettingType.Inteiro, "1", SettingLayers.Organizacao, "so organizacao");
    private static readonly SettingDefinition SoSistema = new("teste.sistema", SettingType.Inteiro, "1", SettingLayers.SomenteSistema, "so sistema");
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UnitId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");

    private static VersaoDeConfiguracao Org(SettingDefinition d, int versao, string? valor, DateTimeOffset desde, bool travado = false) =>
        new(Guid.NewGuid(), d.Key, null, versao, valor, travado, desde, desde, null, null, null);

    private static VersaoDeConfiguracao Unit(SettingDefinition d, int versao, string? valor, DateTimeOffset desde) =>
        new(Guid.NewGuid(), d.Key, UnitId, versao, valor, false, desde, desde, null, null, null);

    [Fact]
    public void With_no_stored_version_the_system_value_wins()
    {
        var r = ResolvedorDeConfiguracao.Resolver(Opcao, "10", [], T0);

        Assert.Equal(("10", SettingLayer.Sistema, false), (r.Value, r.Source, r.LockedByOrganization));
    }

    [Fact]
    public void The_organization_overrides_the_system_and_the_unit_overrides_the_organization()
    {
        var versions = new[] { Org(Opcao, 1, "20", T0.AddDays(-1)), Unit(Opcao, 1, "30", T0.AddDays(-1)) };

        var emUnidade = ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0, UnitId);
        var soOrganizacao = ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0);

        Assert.Equal(("30", SettingLayer.Unidade), (emUnidade.Value, emUnidade.Source));
        Assert.Equal(("20", SettingLayer.Organizacao), (soOrganizacao.Value, soOrganizacao.Source));
    }

    [Fact]
    public void A_null_value_means_inherit_from_the_layer_above()
    {
        var versions = new[] { Org(Opcao, 1, "20", T0.AddDays(-2)), Unit(Opcao, 1, "30", T0.AddDays(-2)), Unit(Opcao, 2, null, T0.AddDays(-1)) };

        var r = ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0, UnitId);

        Assert.Equal(("20", SettingLayer.Organizacao), (r.Value, r.Source));
    }

    [Fact]
    public void A_lock_on_the_organization_makes_the_unit_value_inert()
    {
        var versions = new[] { Org(Opcao, 1, "20", T0.AddDays(-1), travado: true), Unit(Opcao, 1, "30", T0.AddDays(-2)) };

        var r = ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0, UnitId);

        Assert.Equal(("20", SettingLayer.Organizacao, true), (r.Value, r.Source, r.LockedByOrganization));
    }

    [Fact]
    public void A_lock_with_no_organization_value_locks_the_system_value()
    {
        var versions = new[] { Org(Opcao, 1, null, T0.AddDays(-1), travado: true), Unit(Opcao, 1, "30", T0.AddDays(-2)) };

        var r = ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0, UnitId);

        Assert.Equal(("10", SettingLayer.Sistema, true), (r.Value, r.Source, r.LockedByOrganization));
    }

    [Fact]
    public void Unlocking_brings_the_unit_value_back()
    {
        var versions = new[] { Org(Opcao, 1, "20", T0.AddDays(-3), travado: true), Org(Opcao, 2, "20", T0.AddDays(-1)), Unit(Opcao, 1, "30", T0.AddDays(-5)) };

        var r = ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0, UnitId);

        Assert.Equal(("30", SettingLayer.Unidade, false), (r.Value, r.Source, r.LockedByOrganization));
    }

    [Fact]
    public void A_version_applies_only_from_its_instant_and_the_past_is_never_rewritten()
    {
        var versions = new[] { Org(Opcao, 1, "20", T0.AddDays(-1)), Org(Opcao, 2, "40", T0.AddHours(2)) };

        Assert.Equal("20", ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0).Value);
        Assert.Equal("20", ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0.AddHours(2).AddTicks(-1)).Value);
        Assert.Equal("40", ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0.AddHours(2)).Value);
        Assert.Equal("10", ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0.AddDays(-2)).Value); // before any version: the system value
    }

    [Fact]
    public void A_later_version_cancels_a_scheduled_one_that_has_not_started()
    {
        // v2 scheduled for +2h, then v3 written now: v3 is the latest, so v2 never takes effect.
        var versions = new[] { Org(Opcao, 1, "20", T0.AddDays(-1)), Org(Opcao, 2, "40", T0.AddHours(2)), Org(Opcao, 3, "20", T0) };

        Assert.Equal("20", ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0.AddHours(3)).Value);
    }

    [Fact]
    public void Layers_the_option_does_not_allow_are_ignored()
    {
        var soOrg = new[] { Org(SoOrganizacao, 1, "5", T0.AddDays(-1)), Unit(SoOrganizacao, 1, "9", T0.AddDays(-1)) };
        var soSis = new[] { Org(SoSistema, 1, "5", T0.AddDays(-1)) };

        Assert.Equal("5", ResolvedorDeConfiguracao.Resolver(SoOrganizacao, "1", soOrg, T0, UnitId).Value);
        Assert.Equal(("1", SettingLayer.Sistema), (ResolvedorDeConfiguracao.Resolver(SoSistema, "1", soSis, T0).Value, ResolvedorDeConfiguracao.Resolver(SoSistema, "1", soSis, T0).Source));
    }

    [Fact]
    public void Only_the_asked_unit_is_read()
    {
        var other = Guid.Parse("0197a000-0000-7000-8000-0000000000a9");
        var versions = new[] { Unit(Opcao, 1, "30", T0.AddDays(-1)) with { UnidadeId = other } };

        Assert.Equal("10", ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0, UnitId).Value);
        Assert.Equal("30", ResolvedorDeConfiguracao.Resolver(Opcao, "10", versions, T0, other).Value);
    }
}
