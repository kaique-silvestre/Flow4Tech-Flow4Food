using Flow4Tech.Api;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Architecture.Tests;

/// <summary>
/// Option catalog of the layered settings (ticket 10, spec 5.7.2): options are declared in <c>[SettingsCatalog]</c> classes and collected from the module assemblies like the permission
/// catalog. The collected catalog is exported to <c>backend/settings.catalog.txt</c> and a published option is never removed or renamed (stored versions point at the key).
/// </summary>
public sealed partial class ModuleCatalogCoverageTests
{
    private static string SettingsExportPath() => Path.Combine(ModuleGraph.BackendRoot(), "settings.catalog.txt");

    private static List<string> ReadSettingsExport() =>
        File.Exists(SettingsExportPath()) ? [.. File.ReadAllLines(SettingsExportPath()).Where(l => l.Length > 0)] : [];

    [Fact]
    public void The_host_collects_the_option_catalog_from_the_modules_and_every_declaration_is_valid()
    {
        var catalog = ModuleCatalog.LoadSettings(AppContext.BaseDirectory);

        Assert.NotEmpty(catalog);
        Assert.Equal(catalog.Count, catalog.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalog, d => SettingsCatalog.Validate(d, d.Key));
        // The initial options of the spec (5.7.5): session clocks, login lock policy, time zone and operating day rollover.
        foreach (var key in new[] { "sessao.inatividade-horas", "sessao.absoluta-dias", "bloqueio.falhas-email", "dia-operacional.fuso", "dia-operacional.virada" })
        {
            Assert.Contains(catalog, d => d.Key == key);
        }
    }

    [Fact]
    public void The_committed_option_catalog_never_loses_a_published_key_and_matches_the_source()
    {
        var declared = SettingsCatalog.Render(ModuleCatalog.LoadSettings(AppContext.BaseDirectory)).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var published = ReadSettingsExport();
        static string Key(string line) => line[..line.IndexOf('|', StringComparison.Ordinal)];

        if (Environment.GetEnvironmentVariable("UPDATE_SETTINGS_CATALOG") == "1")
        {
            File.WriteAllText(SettingsExportPath(), string.Concat(declared.Select(l => l + "\n")));
            published = ReadSettingsExport();
        }

        Assert.True(File.Exists(SettingsExportPath()), "backend/settings.catalog.txt is missing: run with UPDATE_SETTINGS_CATALOG=1");
        Assert.Empty(published.Select(Key).Except(declared.Select(Key), StringComparer.Ordinal)); // a published option was removed or renamed
        Assert.Equal(published, declared); // a new option, or a changed default/type/layers: regenerate with UPDATE_SETTINGS_CATALOG=1 and commit the file
    }
}
