using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Domain;
using FsCheck.Xunit;

namespace Flow4Tech.Organizacao.Tests;

/// <summary>Property tests (F4-0105) of the layered settings resolution (spec 5.7).</summary>
public sealed class ConfiguracaoPropertyTests
{
    private static readonly SettingDefinition Opcao = new("teste.limite", SettingType.Inteiro, "10", SettingLayers.OrganizacaoEUnidade, "limite");
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Unit = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");

    private static VersaoDeConfiguracao V(Guid? unit, int versao, string? valor, int diasDesdeT0, bool travado = false) =>
        new(Guid.NewGuid(), Opcao.Key, unit, versao, valor, travado, T0.AddDays(diasDesdeT0), T0.AddDays(diasDesdeT0), null, null, null);

    [Property]
    public bool Resolution_does_not_depend_on_the_order_the_versions_come_in(int[] dias, bool[] travadas)
    {
        ArgumentNullException.ThrowIfNull(dias);
        ArgumentNullException.ThrowIfNull(travadas);
        var versoes = dias.Select((d, i) => V(i % 2 == 0 ? null : Unit, i + 1, (i * 7).ToString(System.Globalization.CultureInfo.InvariantCulture), d % 30, travadas.Length > 0 && travadas[i % travadas.Length])).ToList();
        var a = ResolvedorDeConfiguracao.Resolver(Opcao, "10", versoes, T0, Unit);
        var b = ResolvedorDeConfiguracao.Resolver(Opcao, "10", Enumerable.Reverse(versoes), T0, Unit);
        return (a.Value, a.Source, a.LockedByOrganization) == (b.Value, b.Source, b.LockedByOrganization);
    }

    [Property]
    public bool A_version_that_has_not_started_never_changes_what_applies_now(int dias, string valor)
    {
        var future = V(null, 1, valor ?? "x", 1 + Math.Abs(dias % 100));
        var sem = ResolvedorDeConfiguracao.Resolver(Opcao, "10", [], T0);
        var com = ResolvedorDeConfiguracao.Resolver(Opcao, "10", [future], T0);
        return (sem.Value, sem.Source) == (com.Value, com.Source);
    }

    [Property]
    public bool A_locked_organization_value_makes_the_unit_value_inert(int organizacao, int unidade)
    {
        var versoes = new[] { V(null, 1, organizacao.ToString(System.Globalization.CultureInfo.InvariantCulture), -1, travado: true), V(Unit, 1, unidade.ToString(System.Globalization.CultureInfo.InvariantCulture), -1) };
        var r = ResolvedorDeConfiguracao.Resolver(Opcao, "10", versoes, T0, Unit);
        return r.Value == organizacao.ToString(System.Globalization.CultureInfo.InvariantCulture) && r.Source == SettingLayer.Organizacao && r.LockedByOrganization;
    }

    [Property]
    public bool The_highest_started_version_wins_and_a_null_value_inherits(int a, int b)
    {
        var v1 = V(null, 1, a.ToString(System.Globalization.CultureInfo.InvariantCulture), -2);
        var v2 = V(null, 2, b.ToString(System.Globalization.CultureInfo.InvariantCulture), -1);
        var inherit = V(null, 3, null, 0);
        var r2 = ResolvedorDeConfiguracao.Resolver(Opcao, "10", [v1, v2], T0);
        var r3 = ResolvedorDeConfiguracao.Resolver(Opcao, "10", [v1, v2, inherit], T0);
        return r2.Value == b.ToString(System.Globalization.CultureInfo.InvariantCulture) && r3.Value == "10" && r3.Source == SettingLayer.Sistema;
    }
}
