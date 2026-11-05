using System.Text.RegularExpressions;

namespace Flow4Tech.Architecture.Tests;

/// <summary>
/// Ticket 05 (spec 5.4.1, ADR-006): nothing branches on a profile name. The engine is the only place that reads the profiles of a subject, the factory profile names are
/// data that only the factory policy and the interim profile source (until ticket 06 persists profiles) may name, and no code compares a profile to a literal.
/// </summary>
public sealed partial class ProfileBranchingTests
{
    /// <summary>The ONLY files allowed to name a factory profile.</summary>
    private static readonly string[] FactoryProfileUsers =
    [
        "Permissoes/Application/PoliticaDeFabrica.cs",
        "Api/PerfisDaConta.cs",
    ];

    /// <summary>The ONLY file that reads <c>Subject.Profiles</c>: the pure evaluator.</summary>
    private static readonly string[] ProfileReaders = ["Permissoes/Domain/PolicyEvaluator.cs"];

    [GeneratedRegex(@"\bPerfisDeFabrica\b")]
    private static partial Regex FactoryNameRegex();

    [GeneratedRegex(@"\.Profiles\b")]
    private static partial Regex ProfilesReadRegex();

    [GeneratedRegex(@"\b(Profile|Perfil)\w*\s*(==|!=)|(==|!=)\s*\w*(Profile|Perfil)\b|\b(Profile|Perfil)\w*\.(Equals|StartsWith|EndsWith)\(|case\s+""(dono|conta|gerente|garcom|caixa)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileComparisonRegex();

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "modules.dependencies.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("backend root not found"), "src");
    }

    private static IEnumerable<(string Relative, string Text)> Sources()
    {
        var root = SourceRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(file));
        }
    }

    [Fact]
    public void Only_the_factory_policy_and_the_interim_profile_source_name_a_factory_profile()
    {
        var offenders = Sources().Where(s => FactoryNameRegex().IsMatch(s.Text) && !FactoryProfileUsers.Contains(s.Relative)).Select(s => s.Relative).ToList();

        Assert.Empty(offenders);
        Assert.True(Sources().Any(s => FactoryProfileUsers.Contains(s.Relative) && FactoryNameRegex().IsMatch(s.Text)), "the allowed users must really exist (not vacuous)");
    }

    [Fact]
    public void Only_the_evaluator_reads_the_profiles_of_a_subject()
    {
        var offenders = Sources().Where(s => ProfilesReadRegex().IsMatch(s.Text) && !ProfileReaders.Contains(s.Relative)).Select(s => s.Relative).ToList();

        Assert.Empty(offenders);
        Assert.True(Sources().Any(s => ProfileReaders.Contains(s.Relative) && ProfilesReadRegex().IsMatch(s.Text)), "the evaluator must really read them (not vacuous)");
    }

    [Fact]
    public void No_source_compares_a_profile_to_a_value()
    {
        var offenders = Sources().Where(s => ProfileComparisonRegex().IsMatch(s.Text)).Select(s => s.Relative).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_detector_finds_the_forbidden_shapes()
    {
        Assert.Matches(ProfileComparisonRegex(), "if (perfil == \"dono\") return;");
        Assert.Matches(ProfileComparisonRegex(), "if (\"dono\" == profile) return;");
        Assert.Matches(ProfileComparisonRegex(), "if (profileName.Equals(\"x\")) return;");
        Assert.Matches(ProfileComparisonRegex(), "switch (p) { case \"dono\": break; }");
        Assert.Matches(ProfilesReadRegex(), "who.Profiles.Contains(\"x\")");
        Assert.DoesNotMatch(ProfileComparisonRegex(), "var allowed = rule.Effect == RuleEffect.Allow;");
    }
}
