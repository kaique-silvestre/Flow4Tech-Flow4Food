using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Organizacao.Tests;

/// <summary>Declaration of options in code, their value rules and the system layer (spec 5.7.2).</summary>
public sealed class ConfiguracaoDeclaracaoTests
{
    private static SettingDefinition Def(SettingType type, string padrao, decimal? min = null, decimal? max = null, Func<string, bool>? validator = null) =>
        new("teste.opcao", type, padrao, SettingLayers.Organizacao, "teste") { Minimum = min, Maximum = max, Validator = validator };

    [Theory]
    [InlineData(SettingType.Inteiro, " 12 ", "12")]
    [InlineData(SettingType.Numero, "12.0", "12")]
    [InlineData(SettingType.Numero, "0.50", "0.5")]
    [InlineData(SettingType.Booleano, "TRUE", "true")]
    [InlineData(SettingType.Hora, "5:00", null)]
    [InlineData(SettingType.Hora, "05:00", "05:00")]
    [InlineData(SettingType.Inteiro, "1.5", null)]
    [InlineData(SettingType.Inteiro, "abc", null)]
    [InlineData(SettingType.Booleano, "yes", null)]
    [InlineData(SettingType.Texto, "", null)]
    [InlineData(SettingType.Texto, "  America/Sao_Paulo ", "America/Sao_Paulo")]
    public void Values_are_parsed_by_type_into_one_canonical_text(SettingType type, string raw, string? esperado) =>
        Assert.Equal(esperado, SettingValueRules.Normalize(Def(type, "1"), raw));

    [Fact]
    public void Range_and_the_options_own_check_are_enforced()
    {
        var faixa = Def(SettingType.Inteiro, "5", min: 1, max: 10);
        Assert.Equal("10", SettingValueRules.Normalize(faixa, "10"));
        Assert.Null(SettingValueRules.Normalize(faixa, "11"));
        Assert.Null(SettingValueRules.Normalize(faixa, "0"));
        Assert.Null(SettingValueRules.Normalize(Def(SettingType.Texto, "a", validator: v => v.StartsWith('a')), "b"));
    }

    [Fact]
    public void A_text_longer_than_the_limit_is_refused() =>
        Assert.Null(SettingValueRules.Normalize(Def(SettingType.Texto, "a"), new string('x', SettingValueRules.MaxTextLength + 1)));

    [SettingsCatalog]
    private static class Bom
    {
        public static readonly SettingDefinition A = new("grupo.a", SettingType.Inteiro, "3", SettingLayers.OrganizacaoEUnidade, "a");
        public static readonly SettingDefinition B = new("grupo.b", SettingType.Booleano, "false", SettingLayers.SomenteSistema, "b");
    }

    [SettingsCatalog]
    private static class Repetido
    {
        public static readonly SettingDefinition A = new("grupo.a", SettingType.Inteiro, "3", SettingLayers.Organizacao, "a");
    }

    [SettingsCatalog]
    private static class PadraoInvalido
    {
        public static readonly SettingDefinition A = new("grupo.c", SettingType.Inteiro, "tres", SettingLayers.Organizacao, "c");
    }

    [SettingsCatalog]
    private static class ChaveInvalida
    {
        public static readonly SettingDefinition A = new("SemPonto", SettingType.Inteiro, "3", SettingLayers.Organizacao, "c");
    }

    [Fact]
    public void The_catalog_is_collected_sorted_from_marked_classes_only()
    {
        var catalogo = SettingsCatalog.FromTypes([typeof(Bom), typeof(string)]);

        Assert.Equal(["grupo.a", "grupo.b"], catalogo.Select(d => d.Key));
        Assert.Equal("grupo.a|Inteiro|OrganizacaoEUnidade|3\ngrupo.b|Booleano|SomenteSistema|false\n", SettingsCatalog.Render(catalogo));
    }

    [Fact]
    public void A_duplicate_key_a_bad_default_or_a_bad_key_stops_the_collection()
    {
        Assert.Throws<InvalidOperationException>(() => SettingsCatalog.FromTypes([typeof(Bom), typeof(Repetido)]));
        Assert.Throws<InvalidOperationException>(() => SettingsCatalog.FromTypes([typeof(PadraoInvalido)]));
        Assert.Throws<InvalidOperationException>(() => SettingsCatalog.FromTypes([typeof(ChaveInvalida)]));
    }

    [Fact]
    public void The_system_layer_is_the_default_unless_configuration_says_otherwise()
    {
        var definicao = Bom.A;
        var padrao = new SystemSettings(_ => null);
        var configurado = new SystemSettings(d => d.SystemConfigPath == "Flow4Tech:Configuracoes:grupo.a" ? "7" : null);
        var legado = new SystemSettings(d => d.SystemConfigPath == "Flow4Tech:Antigo:A" ? "9.0" : null);
        var comLegado = definicao with { ConfigPath = "Flow4Tech:Antigo:A", Type = SettingType.Numero };

        Assert.Equal("3", padrao.SystemValue(definicao));
        Assert.Equal("7", configurado.SystemValue(definicao));
        Assert.Equal("9", legado.SystemValue(comLegado));
    }

    [Fact]
    public void A_configured_value_that_is_not_valid_stops_instead_of_falling_back()
    {
        var sistema = new SystemSettings(_ => "abc");

        Assert.Throws<InvalidOperationException>(() => sistema.SystemValue(Bom.A));
    }
}
