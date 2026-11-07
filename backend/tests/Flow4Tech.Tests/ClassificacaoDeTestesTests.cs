using System.Reflection;

namespace Flow4Tech.Tests;

/// <summary>
/// CI splits this project by class name: names containing "Endpoints" or "Integration" run in the job with real PostgreSQL,
/// everything else runs in the unit job without Docker. A class that starts a database but lacks those words would run
/// in the wrong job and fail with a confusing connection error. This guard turns that into a clear failure, locally and in the unit job.
/// </summary>
public sealed class ClassificacaoDeTestesTests
{
    private static readonly string[] IntegrationMarkers = ["Endpoints", "Integration"];

    [Fact]
    public void Classes_that_start_a_database_carry_an_integration_marker_in_their_name()
    {
        var offenders = typeof(ClassificacaoDeTestesTests).Assembly.GetTypes()
            .Where(IsTestClass)
            .Where(UsesDatabase)
            .Where(t => !IntegrationMarkers.Any(m => t.Name.Contains(m, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These test classes use a real database (Testcontainers) but their name has neither 'Endpoints' nor 'Integration', "
            + "so CI would run them in the unit job without Docker. Rename them: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_guard_itself_runs_in_the_unit_job()
    {
        Assert.DoesNotContain(IntegrationMarkers, m => nameof(ClassificacaoDeTestesTests).Contains(m, StringComparison.Ordinal));
    }

    [Fact]
    public void The_guard_recognises_a_known_database_class()
    {
        // Protects the guard from going blind (e.g. after a refactor of the fixtures): at least one class must be detected.
        Assert.Contains(typeof(ClassificacaoDeTestesTests).Assembly.GetTypes().Where(IsTestClass), UsesDatabase);
    }

    private static bool IsTestClass(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttributes().Any(a => a is FactAttribute));

    private static bool UsesDatabase(Type type) =>
        HoldsContainer(type)
        || type.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IClassFixture<>))
            .Any(i => HoldsContainer(i.GetGenericArguments()[0]));

    private static bool HoldsContainer(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.GetFields(all).Any(f => IsContainerType(f.FieldType)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsContainerType(Type type) =>
        type.Namespace?.StartsWith("Testcontainers", StringComparison.Ordinal) == true;
}
