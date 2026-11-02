namespace Flow4Tech.Architecture.Tests;

public class ModuleBoundaryTests
{
    private static readonly Dictionary<string, string[]> NoEdges = new()
    {
        ["A"] = [],
        ["B"] = [],
    };

    private static ProjectInfo Proj(string path, string[]? refs = null, string[]? pkgs = null, string[]? fws = null, string[]? usings = null, string sdk = "Microsoft.NET.Sdk", string[]? mentions = null) =>
        new(path, sdk, refs ?? [], pkgs ?? [], fws ?? [], usings ?? [], mentions);

    private const string Data = "src/Infrastructure/Data/d.csproj";

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

    // ---- tenant data only through TenantDatabase, EF Core as the style (ADR-002) ----

    private static Dictionary<string, string[]> OneModule => new() { ["A"] = [] };

    [Theory]
    [InlineData("src/A/Application/a.csproj")]
    [InlineData("src/A/Domain/a.csproj")]
    [InlineData("src/A/Interface/a.csproj")]
    [InlineData("src/A/Infrastructure/a.csproj")]
    [InlineData("src/Kernel/k.csproj")]
    public void Npgsql_package_namespace_or_code_outside_the_Data_project_fails(string path)
    {
        Assert.Contains(ModuleGraph.Check([Proj(path, pkgs: ["Npgsql"])], OneModule), v => v.Contains("only Infrastructure/Data may use Npgsql", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(path, pkgs: ["Npgsql.EntityFrameworkCore.PostgreSQL"])], OneModule), v => v.Contains("only Infrastructure/Data may use Npgsql", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(path, usings: ["Npgsql"])], OneModule), v => v.Contains("only Infrastructure/Data may use Npgsql", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(path, mentions: ["Npgsql"])], OneModule), v => v.Contains("only Infrastructure/Data may use Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Api_readiness_probe_is_the_one_documented_Npgsql_exception_outside_Data()
    {
        Assert.Empty(ModuleGraph.Check([Proj("src/Api/api.csproj", pkgs: ["Npgsql"], usings: ["Npgsql"], mentions: ["Npgsql"])], NoEdges));
    }

    [Fact]
    public void The_Data_project_may_use_Npgsql_EF_Core_and_set_up_contexts()
    {
        var data = Proj(Data, pkgs: ["Npgsql", "Microsoft.EntityFrameworkCore", "Npgsql.EntityFrameworkCore.PostgreSQL"], usings: ["Npgsql", "Microsoft.EntityFrameworkCore"], mentions: ["Npgsql", "DbContextSetup", "DirectDbContext"]);
        Assert.Empty(ModuleGraph.Check([data], NoEdges));
    }

    [Fact]
    public void Module_Infrastructure_may_reference_Data_and_use_EF_Core()
    {
        var projects = new[]
        {
            Proj("src/A/Infrastructure/i.csproj", ["src/Infrastructure/Data/d.csproj"], pkgs: ["Microsoft.EntityFrameworkCore"], usings: ["Microsoft.EntityFrameworkCore", "Flow4Tech.Infrastructure.Data"]),
            Proj(Data),
        };
        Assert.Empty(ModuleGraph.Check(projects, OneModule));
    }

    private const string Outbox = "src/Infrastructure/Outbox/o.csproj";

    [Fact]
    public void Outbox_may_use_EF_Core_on_top_of_Data_but_not_Npgsql_or_DbContext_setup()
    {
        var ok = Proj(Outbox, [Data], pkgs: ["Microsoft.EntityFrameworkCore"], usings: ["Microsoft.EntityFrameworkCore", "Flow4Tech.Infrastructure.Data"], mentions: []);
        Assert.Empty(ModuleGraph.Check([ok, Proj(Data)], OneModule));
        Assert.Contains(ModuleGraph.Check([Proj(Outbox, pkgs: ["Npgsql"])], OneModule), v => v.Contains("only Infrastructure/Data may use Npgsql", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(Outbox, mentions: ["DbContextSetup"])], OneModule), v => v.Contains("AddDbContext/UseNpgsql/DbContextOptions outside", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(Outbox, mentions: ["TenantSetting"])], OneModule), v => v.Contains("app.organizacao_id", StringComparison.Ordinal));
    }

    private const string Audit = "src/Infrastructure/Audit/a.csproj";

    [Fact]
    public void Audit_may_use_EF_Core_on_top_of_Data_and_Outbox_but_not_Npgsql_or_DbContext_setup()
    {
        var ok = Proj(Audit, [Data, Outbox], pkgs: ["Microsoft.EntityFrameworkCore"], usings: ["Microsoft.EntityFrameworkCore", "Flow4Tech.Infrastructure.Data"], mentions: []);
        Assert.Empty(ModuleGraph.Check([ok, Proj(Data), Proj(Outbox)], OneModule));
        Assert.Contains(ModuleGraph.Check([Proj(Audit, pkgs: ["Npgsql"])], OneModule), v => v.Contains("only Infrastructure/Data may use Npgsql", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(Audit, mentions: ["DbContextSetup"])], OneModule), v => v.Contains("AddDbContext/UseNpgsql/DbContextOptions outside", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(Audit, mentions: ["TenantSetting"])], OneModule), v => v.Contains("app.organizacao_id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src/A/Domain/a.csproj")]
    [InlineData("src/A/Application/a.csproj")]
    [InlineData("src/A/Interface/a.csproj")]
    [InlineData("src/A/Infrastructure/a.csproj")]
    public void Module_layers_may_not_reference_the_Audit_implementation_only_the_Kernel_contract(string path)
    {
        var violations = ModuleGraph.Check([Proj(path, [Audit]), Proj(Audit)], OneModule);
        Assert.Contains(violations, v => v.Contains("src/Infrastructure/Audit", StringComparison.Ordinal) && v.StartsWith(path, StringComparison.Ordinal));
    }

    [Fact]
    public void Module_Infrastructure_may_reference_Outbox()
    {
        Assert.Empty(ModuleGraph.Check([Proj("src/A/Infrastructure/i.csproj", [Outbox]), Proj(Outbox)], OneModule));
    }

    [Theory]
    [InlineData("src/A/Domain/a.csproj")]
    [InlineData("src/A/Application/a.csproj")]
    [InlineData("src/A/Interface/a.csproj")]
    public void Module_layers_other_than_Infrastructure_may_not_reference_Outbox(string path)
    {
        var violations = ModuleGraph.Check([Proj(path, [Outbox]), Proj(Outbox)], OneModule);
        Assert.Contains(violations, v => v.Contains("src/Infrastructure/Outbox", StringComparison.Ordinal) && v.StartsWith(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src/A/Domain/a.csproj")]
    [InlineData("src/A/Application/a.csproj")]
    [InlineData("src/A/Interface/a.csproj")]
    public void Module_layers_other_than_Infrastructure_may_not_reference_Data(string path)
    {
        var violations = ModuleGraph.Check([Proj(path, ["src/Infrastructure/Data/d.csproj"]), Proj(Data)], OneModule);
        Assert.Contains(violations, v => v.Contains("src/Infrastructure/Data", StringComparison.Ordinal) && v.StartsWith(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src/A/Application/a.csproj")]
    [InlineData("src/A/Interface/a.csproj")]
    [InlineData("src/Kernel/k.csproj")]
    [InlineData("src/Api/api.csproj")]
    public void EF_Core_outside_Data_and_module_Infrastructure_fails(string path)
    {
        Assert.Contains(ModuleGraph.Check([Proj(path, pkgs: ["Microsoft.EntityFrameworkCore"])], OneModule), v => v.Contains("EF Core is only allowed", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(path, usings: ["Microsoft.EntityFrameworkCore.Infrastructure"])], OneModule), v => v.Contains("EF Core is only allowed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src/Infrastructure/Data/d.csproj", "Microsoft.EntityFrameworkCore.Design")]
    [InlineData("src/A/Infrastructure/a.csproj", "Microsoft.EntityFrameworkCore.Tools")]
    public void EF_migrations_tooling_fails_because_the_schema_is_SQL(string path, string package)
    {
        Assert.Contains(ModuleGraph.Check([Proj(path, pkgs: [package])], OneModule), v => v.Contains("EF migrations are not used", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src/Infrastructure/Data/d.csproj")]
    [InlineData("src/A/Infrastructure/a.csproj")]
    public void Dapper_is_not_used_anywhere(string path)
    {
        Assert.Contains(ModuleGraph.Check([Proj(path, pkgs: ["Dapper"])], OneModule), v => v.Contains("Dapper is not used", StringComparison.Ordinal));
        Assert.Contains(ModuleGraph.Check([Proj(path, usings: ["Dapper"])], OneModule), v => v.Contains("Dapper is not used", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DbContextSetup", "AddDbContext/UseNpgsql/DbContextOptions outside")]
    [InlineData("DirectDbContext", "derives from DbContext directly")]
    public void Building_or_deriving_a_DbContext_outside_Data_fails(string mention, string expected)
    {
        foreach (var path in new[] { "src/A/Infrastructure/a.csproj", "src/A/Application/a.csproj", "src/Api/api.csproj" })
        {
            Assert.Contains(ModuleGraph.Check([Proj(path, mentions: [mention])], OneModule), v => v.Contains(expected, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("src/A/Infrastructure/a.csproj")]
    [InlineData("src/A/Application/a.csproj")]
    [InlineData("src/Api/api.csproj")]
    public void Setting_the_organization_context_by_hand_outside_Data_fails(string path)
    {
        Assert.Contains(ModuleGraph.Check([Proj(path, mentions: ["TenantSetting"])], OneModule), v => v.Contains("app.organizacao_id", StringComparison.Ordinal));
        Assert.Empty(ModuleGraph.Check([Proj(Data, mentions: ["TenantSetting"])], NoEdges));
    }

    [Theory]
    [InlineData("src/Infrastructure/Data/d.csproj")]
    [InlineData("src/A/Infrastructure/a.csproj")]
    public void Unparameterized_raw_sql_APIs_are_banned_everywhere(string path)
    {
        Assert.Contains(ModuleGraph.Check([Proj(path, mentions: ["RawSqlApi"])], OneModule), v => v.Contains("FromSqlRaw/ExecuteSqlRaw/SqlQueryRaw is banned", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("var c = new Foo(); services.AddDbContext<Foo>(o => o.UseNpgsql(cs));", "DbContextSetup")]
    [InlineData("public class Foo : DbContext { }", "DirectDbContext")]
    [InlineData("public class Foo : Microsoft.EntityFrameworkCore.DbContext { }", "DirectDbContext")]
    [InlineData("public sealed class Foo : IDisposable, DbContext { }", "DirectDbContext")]
    [InlineData("public sealed class Foo : IDisposable, Microsoft.EntityFrameworkCore.DbContext { }", "DirectDbContext")]
    [InlineData("public class Foo(int a) : DbContext(a) { }", "DirectDbContext")]
    [InlineData("public class Foo<T> : DbContext where T : class { }", "DirectDbContext")]
    [InlineData("using Ctx = Microsoft.EntityFrameworkCore.DbContext;", "DirectDbContext")]
    [InlineData("using Ctx = DbContext;", "DirectDbContext")]
    [InlineData("cmd.CommandText = \"SELECT set_config('app.organizacao_id', 'x', true)\";", "TenantSetting")]
    [InlineData("db.Itens.FromSqlRaw(\"select 1\")", "RawSqlApi")]
    [InlineData("var c = new NpgsqlConnection(cs);", "Npgsql")]
    public void Source_scanner_sees_the_banned_constructs_but_not_comments_or_the_safe_base_class(string code, string token)
    {
        var dir = Directory.CreateTempSubdirectory("scan").FullName;
        File.WriteAllText(Path.Combine(dir, "x.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(dir, "x.cs"), code);
        File.WriteAllText(Path.Combine(dir, "y.cs"), "// : DbContext AddDbContext Npgsql FromSqlRaw\n/// UseNpgsql\npublic class Ok(TenantTransaction t) : TenantDbContext(t) { }\nclass P { void F(int a, DbContext c) { } }");
        var found = ModuleGraph.ScanSource(dir);
        Assert.Contains(token, found);
        Assert.Single(found);
    }
}
