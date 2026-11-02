using System.Text.RegularExpressions;

namespace Flow4Tech.Architecture.Tests;

/// <summary>
/// Part of the existing coverage class on purpose (no new public test type). <c>OrganizationContext.WithoutUnit</c> builds a context without a session (login, first password change). The organization it takes must come from the
/// database, never from the client, so only the Identidade Application layer (which gets it from the pre-authentication lookup) may call it.
/// </summary>
public sealed partial class ModuleCatalogCoverageTests
{
    [GeneratedRegex(@"\bOrganizationContext\s*\.\s*WithoutUnit\s*\(")]
    private static partial Regex PreSessionCall();

    private static string PreSessionSep(string name) => Path.DirectorySeparatorChar + name + Path.DirectorySeparatorChar;

    private static List<string> PreSessionCallers(string srcRoot, out int scanned)
    {
        var files = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(PreSessionSep("obj"), StringComparison.Ordinal) && !f.Contains(PreSessionSep("bin"), StringComparison.Ordinal))
            .ToList();
        scanned = files.Count;
        return [.. files.Where(f => PreSessionCall().IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(srcRoot, f).Replace('\\', '/'))];
    }

    private static bool PreSessionAllowed(string relative) =>
        relative.StartsWith("Identidade/Application/", StringComparison.Ordinal) || relative.StartsWith("Kernel/", StringComparison.Ordinal);

    [Fact]
    public void The_detector_matches_the_call_in_its_usual_spellings()
    {
        Assert.Matches(PreSessionCall(), "var c = OrganizationContext.WithoutUnit(id);");
        Assert.Matches(PreSessionCall(), "var c = Flow4Tech.Kernel.OrganizationContext . WithoutUnit (id);");
        Assert.DoesNotMatch(PreSessionCall(), "var c = request.Organization;");
    }

    [Fact]
    public void Only_the_identity_application_layer_builds_a_context_without_a_unit()
    {
        var callers = PreSessionCallers(Path.Combine(ModuleGraph.BackendRoot(), "src"), out var scanned);

        Assert.True(scanned > 0, "the scan found no source files: it would pass vacuously");
        var offenders = callers.Where(c => !PreSessionAllowed(c)).ToList();
        Assert.Empty(offenders);
        Assert.Contains(callers, c => c.StartsWith("Identidade/Application/", StringComparison.Ordinal)); // not vacuous: the legitimate use exists
    }

    [Fact]
    public void A_fixture_that_calls_it_from_another_module_or_the_host_is_reported()
    {
        var root = Path.Combine(Path.GetTempPath(), "f4t-presession-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var dir in new[] { "Identidade/Application", "Organizacao/Application", "Api", "Organizacao/Infrastructure" })
            {
                Directory.CreateDirectory(Path.Combine(root, dir));
                File.WriteAllText(Path.Combine(root, dir, "X.cs"), "class X { object o = OrganizationContext.WithoutUnit(id); }");
            }

            var callers = PreSessionCallers(root, out _);

            Assert.Equal(["Api/X.cs", "Organizacao/Application/X.cs", "Organizacao/Infrastructure/X.cs"], callers.Where(c => !PreSessionAllowed(c)).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [GeneratedRegex(@"\bPreAuthDatabase\b")]
    private static partial Regex PreAuthDatabaseUse();

    /// <summary>
    /// <c>PreAuthDatabase</c> is the one connection opened without an organization context. Only the Data project (which defines it) and the Identidade Infrastructure layer
    /// (its adapter) may name it: no other module and no host code can open a context-free door.
    /// </summary>
    [Fact]
    public void Only_the_identity_infrastructure_names_the_connection_without_an_organization_context()
    {
        var src = Path.Combine(ModuleGraph.BackendRoot(), "src");
        var users = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(PreSessionSep("obj"), StringComparison.Ordinal) && !f.Contains(PreSessionSep("bin"), StringComparison.Ordinal))
            .Where(f => PreAuthDatabaseUse().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .ToList();

        var offenders = users.Where(f => !f.StartsWith("Infrastructure/Data/", StringComparison.Ordinal) && !f.StartsWith("Identidade/Infrastructure/", StringComparison.Ordinal)).ToList();
        Assert.Empty(offenders);
        Assert.Contains(users, f => f.StartsWith("Identidade/Infrastructure/", StringComparison.Ordinal)); // not vacuous
    }
}
