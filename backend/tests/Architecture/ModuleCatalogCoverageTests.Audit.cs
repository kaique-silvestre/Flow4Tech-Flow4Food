using System.Text.RegularExpressions;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Architecture.Tests;

/// <summary>
/// Audit action catalog (spec 5.6.7): names are declared in <c>[AuditCatalog]</c> classes, exported to <c>backend/audit.catalog.txt</c>, and a published name is never
/// renamed or removed. Part of the existing coverage class on purpose (no new public test type).
/// </summary>
public sealed partial class ModuleCatalogCoverageTests
{
    [GeneratedRegex(@"\[AuditCatalog\]\s*(?:public|internal)?\s*(?:static\s+)?(?:partial\s+)?class\s+\w+\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline)]
    private static partial Regex AuditCatalogClass();

    [GeneratedRegex(@"\bAction\s*=\s*""")]
    private static partial Regex ActionStringLiteral();

    internal static IReadOnlyList<string> AuditNamesInSource(IEnumerable<string> sources) =>
        [.. sources.SelectMany(src => AuditCatalogClass().Matches(src))
            .SelectMany(m => ConstString().Matches(m.Groups["body"].Value))
            .Select(m => m.Groups["name"].Value)
            .Order(StringComparer.Ordinal)];

    private static string AuditExportPath() => Path.Combine(ModuleGraph.BackendRoot(), "audit.catalog.txt");

    private static List<string> ReadAuditExport() =>
        File.Exists(AuditExportPath())
            ? [.. File.ReadAllLines(AuditExportPath()).Where(l => l.Length > 0)]
            : [];

    private static IEnumerable<string> SourcesWithoutBuildOutput(string src) =>
        Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText);

    [Fact]
    public void The_audit_source_scan_reads_constants_only_from_audit_catalog_classes()
    {
        var names = AuditNamesInSource([
            "[AuditCatalog]\npublic static class A\n{\n    public const string X = \"sessao:trocar-unidade\";\n}\n",
            "[PermissionCatalog]\npublic static class P\n{\n    public const string Y = \"c:d\";\n}\n",
        ]);

        Assert.Equal(["sessao:trocar-unidade"], names);
    }

    [Fact]
    public void Audit_catalog_names_are_resource_colon_action_and_unique()
    {
        var declared = AuditNamesInSource(SourcesWithoutBuildOutput(Path.Combine(ModuleGraph.BackendRoot(), "src")));

        Assert.NotEmpty(declared);
        Assert.All(declared, name => Assert.True(AuditActionName.IsValid(name), name));
        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_committed_audit_catalog_never_loses_a_published_name_and_matches_the_source()
    {
        var declared = AuditNamesInSource(SourcesWithoutBuildOutput(Path.Combine(ModuleGraph.BackendRoot(), "src")));
        var published = ReadAuditExport();

        if (Environment.GetEnvironmentVariable("UPDATE_AUDIT_CATALOG") == "1")
        {
            // Regenerating can only ADD: a name already published stays in the file even if the source dropped it, so the test below keeps failing until it is restored.
            File.WriteAllText(AuditExportPath(), string.Concat(published.Union(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(n => n + "\n")));
            published = ReadAuditExport();
        }

        Assert.True(File.Exists(AuditExportPath()), "backend/audit.catalog.txt is missing: run with UPDATE_AUDIT_CATALOG=1");
        Assert.Empty(published.Except(declared, StringComparer.Ordinal)); // a published audit action was removed or renamed: names are never renamed (spec 5.6.7)
        Assert.Empty(declared.Except(published, StringComparer.Ordinal)); // a new action: add it with UPDATE_AUDIT_CATALOG=1 and commit the file
        Assert.Equal(published.Order(StringComparer.Ordinal), published); // sorted
    }

    [Fact]
    public void Audit_actions_come_from_the_catalog_constants_never_from_string_literals()
    {
        Assert.Matches(ActionStringLiteral(), "new AuditEntry { Action = \"sessao:trocar-unidade\" }");
        Assert.DoesNotMatch(ActionStringLiteral(), "new AuditEntry { Action = PlatformAuditActions.SessaoTrocarUnidade }");

        var src = Path.Combine(ModuleGraph.BackendRoot(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && ActionStringLiteral().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_audit_integrity_event_and_job_names_are_stable()
    {
        Assert.Equal("auditoria.integridade-violada", AuditIntegrityViolated.Type);
    }
}
