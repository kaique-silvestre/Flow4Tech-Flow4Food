using System.Text.RegularExpressions;

namespace Flow4Tech.Architecture.Tests;

/// <summary>ADR-006 decision C: the Kernel contracts (engine, unit of work) name no database type, so the engine stays swappable.</summary>
public sealed partial class KernelContractTests
{
    [GeneratedRegex(@"TenantTransaction|Npgsql|EntityFrameworkCore|DbContext|DbTransaction|NpgsqlConnection")]
    private static partial Regex DatabaseTypes();

    [Fact]
    public void The_detector_flags_a_kernel_file_that_names_a_database_type()
    {
        Assert.Matches(DatabaseTypes(), "Task Decidir(TenantTransaction tx);");
        Assert.DoesNotMatch(DatabaseTypes(), "Task<T> ExecuteAsync<T>(OrganizationContext o, Func<CancellationToken, Task<T>> work);");
    }

    [Fact]
    public void Kernel_source_names_no_database_type()
    {
        var kernel = Path.Combine(ModuleGraph.BackendRoot(), "src", "Kernel");
        var offenders = Directory.EnumerateFiles(kernel, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && DatabaseTypes().IsMatch(File.ReadAllText(f)))
            .ToList();

        Assert.Empty(offenders);
    }
}
