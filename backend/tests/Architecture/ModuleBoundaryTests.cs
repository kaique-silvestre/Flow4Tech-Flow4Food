namespace Flow4Tech.Architecture.Tests;

public class ModuleBoundaryTests
{
    private static readonly Dictionary<string, string[]> NoEdges = new()
    {
        ["A"] = [],
        ["B"] = [],
    };

    private static ProjectInfo Proj(string path, string[]? refs = null, string[]? pkgs = null, string[]? fws = null, string[]? usings = null, string sdk = "Microsoft.NET.Sdk") =>
        new(path, sdk, refs ?? [], pkgs ?? [], fws ?? [], usings ?? []);

    // ---- the real tree ----

    [Fact]
    public void RealTree_RespectsDeclaredBoundaries()
    {
        var root = ModuleGraph.BackendRoot();
        var allowed = ModuleGraph.LoadAllowed(File.ReadAllText(Path.Combine(root, "modules.dependencies.json")));
        var violations = ModuleGraph.Check(ModuleGraph.LoadProjects(root), allowed);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // ---- internals access ----

    [Fact]
    public void ReferencingAnotherModulesInternals_Fails()
    {
        var projects = new[]
        {
            Proj("src/A/Application/a.csproj", ["src/B/Domain/b.csproj"]),
            Proj("src/B/Domain/b.csproj"),
        };
        var allowed = new Dictionary<string, string[]> { ["A"] = ["B"], ["B"] = [] };
        Assert.Contains(ModuleGraph.Check(projects, allowed), v => v.Contains("references internals of B", StringComparison.Ordinal));
    }

    [Fact]
    public void UsingAnotherModulesInternalNamespace_Fails()
    {
        var projects = new[] { Proj("src/A/Application/a.csproj", usings: ["Flow4Tech.B.Infrastructure.Db"]), Proj("src/B/Domain/b.csproj") };
        Assert.Contains(ModuleGraph.Check(projects, NoEdges), v => v.Contains("reaches into internals of B", StringComparison.Ordinal));
    }

    // ---- declared edges ----

    [Fact]
    public void UndeclaredEdgeToApplication_Fails()
    {
        var projects = new[] { Proj("src/A/Application/a.csproj", ["src/B/Application/b.csproj"]), Proj("src/B/Application/b.csproj") };
        Assert.Contains(ModuleGraph.Check(projects, NoEdges), v => v.Contains("forbidden dependency A -> B", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclaredEdgeToApplication_Passes()
    {
        var projects = new[] { Proj("src/A/Application/a.csproj", ["src/B/Application/b.csproj"]), Proj("src/B/Application/b.csproj") };
        var allowed = new Dictionary<string, string[]> { ["A"] = ["B"], ["B"] = [] };
        Assert.Empty(ModuleGraph.Check(projects, allowed));
    }

    // ---- cycles ----

    [Fact]
    public void CycleInDeclaredFile_Fails()
    {
        var allowed = new Dictionary<string, string[]> { ["A"] = ["B"], ["B"] = ["A"] };
        Assert.Contains(ModuleGraph.Check([], allowed), v => v.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void CycleInActualReferences_Fails()
    {
        var projects = new[]
        {
            Proj("src/A/Application/a.csproj", ["src/B/Application/b.csproj"]),
            Proj("src/B/Application/b.csproj", ["src/A/Application/a.csproj"]),
        };
        var allowed = new Dictionary<string, string[]> { ["A"] = ["B"], ["B"] = ["A"] };
        Assert.Contains(ModuleGraph.Check(projects, allowed), v => v.Contains("A -> B -> A", StringComparison.Ordinal));
    }

    // ---- domain purity ----

    [Fact]
    public void DomainReferencingInfrastructureProject_Fails()
    {
        var projects = new[] { Proj("src/A/Domain/d.csproj", ["src/A/Infrastructure/i.csproj"]), Proj("src/A/Infrastructure/i.csproj") };
        Assert.Contains(ModuleGraph.Check(projects, NoEdges), v => v.Contains("layer Domain may not reference layer Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void DomainReferencingPackageOrFramework_Fails()
    {
        var projects = new[] { Proj("src/A/Domain/d.csproj", pkgs: ["Npgsql"], fws: ["Microsoft.AspNetCore.App"], sdk: "Microsoft.NET.Sdk.Web") };
        var violations = ModuleGraph.Check(projects, NoEdges);
        Assert.Contains(violations, v => v.Contains("package Npgsql", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("framework Microsoft.AspNetCore.App", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Microsoft.NET.Sdk.Web", StringComparison.Ordinal));
    }

    [Fact]
    public void DomainUsingFrameworkNamespace_Fails()
    {
        var projects = new[] { Proj("src/A/Domain/d.csproj", usings: ["Microsoft.AspNetCore.Http", "Npgsql"]) };
        Assert.Equal(2, ModuleGraph.Check(projects, NoEdges).Count(v => v.Contains("infrastructure/framework namespace", StringComparison.Ordinal)));
    }

    [Fact]
    public void ModuleNotDeclaredInFile_Fails()
    {
        var projects = new[] { Proj("src/Z/Domain/z.csproj") };
        Assert.Contains(ModuleGraph.Check(projects, NoEdges), v => v.Contains("not declared", StringComparison.Ordinal));
    }
}
