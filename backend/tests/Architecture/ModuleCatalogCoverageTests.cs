using System.Text.RegularExpressions;
using Flow4Tech.Api;

namespace Flow4Tech.Architecture.Tests;

/// <summary>
/// A module that the Api does not reference, or a catalog class the host cannot load, would silently drop permissions from the catalog.
/// These tests tie what the host collects at runtime to what is written in the source tree.
/// </summary>
public sealed partial class ModuleCatalogCoverageTests
{
    [GeneratedRegex(@"\[PermissionCatalog\]\s*(?:public|internal)?\s*(?:static\s+)?(?:partial\s+)?class\s+\w+\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline)]
    private static partial Regex CatalogClass();

    [GeneratedRegex(@"const\s+string\s+\w+\s*=\s*""(?<name>[^""]+)""")]
    private static partial Regex ConstString();

    internal static IReadOnlyList<string> ModulesNotReferenced(IEnumerable<string> applicationProjects, string apiCsproj) =>
        [.. applicationProjects.Where(p => !apiCsproj.Contains(p, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    internal static IReadOnlyList<string> NamesInSource(IEnumerable<string> sources) =>
        [.. sources.SelectMany(src => CatalogClass().Matches(src))
            .SelectMany(m => ConstString().Matches(m.Groups["body"].Value))
            .Select(m => m.Groups["name"].Value)
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<string> ModuleSources(string src) =>
        Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText);

    [Fact]
    public void The_check_flags_a_module_application_project_the_api_does_not_reference()
    {
        var missing = ModulesNotReferenced(
            ["Organizacao/Application/Flow4Tech.Organizacao.Application.csproj", "Vendas/Application/Flow4Tech.Vendas.Application.csproj"],
            "<ProjectReference Include=\"../Organizacao/Application/Flow4Tech.Organizacao.Application.csproj\" />");

        Assert.Equal(["Vendas/Application/Flow4Tech.Vendas.Application.csproj"], missing);
    }

    [Fact]
    public void The_source_scan_reads_permission_constants_only_from_catalog_classes()
    {
        var names = NamesInSource([
            "[PermissionCatalog]\npublic static class A\n{\n    public const string X = \"a:b\";\n}\n",
            "public static class NotCatalog\n{\n    public const string Y = \"c:d\";\n}\n",
        ]);

        Assert.Equal(["a:b"], names);
    }

    [Fact]
    public void The_api_references_every_module_application_project()
    {
        var src = Path.Combine(ModuleGraph.BackendRoot(), "src");
        var projects = Directory.EnumerateFiles(src, "Flow4Tech.*.Application.csproj", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'));
        var apiCsproj = File.ReadAllText(Path.Combine(src, "Api", "Flow4Tech.Api.csproj"));

        Assert.NotEmpty(projects);
        Assert.Empty(ModulesNotReferenced(projects, apiCsproj));
    }

    [Fact]
    public void The_catalog_collected_by_the_host_equals_the_permissions_declared_in_the_source_tree()
    {
        var declared = NamesInSource(ModuleSources(Path.Combine(ModuleGraph.BackendRoot(), "src")));

        Assert.NotEmpty(declared);
        Assert.Equal(declared, ModuleCatalog.Load(AppContext.BaseDirectory));
    }
}
