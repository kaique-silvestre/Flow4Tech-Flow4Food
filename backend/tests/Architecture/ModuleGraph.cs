using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Flow4Tech.Architecture.Tests;

/// <summary>One .csproj as the architecture rules see it. Path is relative to backend/, '/'-separated.</summary>
internal sealed record ProjectInfo(
    string Path,
    string Sdk,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> FrameworkReferences,
    IReadOnlyList<string> Usings,
    IReadOnlyList<string>? Mentions = null)
{
    /// <summary>Source-text findings from <see cref="ModuleGraph"/>: Npgsql, DbContextSetup, DirectDbContext, RawSqlApi, TenantSetting.</summary>
    public IReadOnlyList<string> Found => Mentions ?? [];
}

/// <summary>Loads the real tree and evaluates the module-boundary rules. Pure functions over <see cref="ProjectInfo"/> so fixtures can be violating.</summary>
internal static partial class ModuleGraph
{
    private static readonly string[] ForbiddenInDomain =
    [
        "Microsoft.AspNetCore", "Microsoft.Extensions", "Microsoft.EntityFrameworkCore",
        "Npgsql", "Dapper", "System.Data", "System.Net.Http",
    ];

    public static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "modules.dependencies.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("backend/modules.dependencies.json not found");
    }

    /// <summary>Module name -> modules it may depend on (only through their Application layer).</summary>
    public static Dictionary<string, string[]> LoadAllowed(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(json)
        ?? throw new InvalidOperationException("modules.dependencies.json is empty");

    public static List<ProjectInfo> LoadProjects(string backendRoot)
    {
        var result = new List<ProjectInfo>();
        foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(backendRoot, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(backendRoot, file).Replace('\\', '/');
            var doc = XDocument.Load(file);
            var refs = doc.Descendants("ProjectReference")
                .Select(e => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file)!, ((string)e.Attribute("Include")!).Replace('\\', '/'))))
                .Select(p => System.IO.Path.GetRelativePath(backendRoot, p).Replace('\\', '/'))
                .ToList();
            var usings = Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(file)!, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(f))
                .SelectMany(f => UsingRegex().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
                .ToList();
            var mentions = ScanSource(System.IO.Path.GetDirectoryName(file)!);
            result.Add(new ProjectInfo(
                rel,
                (string?)doc.Root?.Attribute("Sdk") ?? "",
                refs,
                doc.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToList(),
                doc.Descendants("FrameworkReference").Select(e => (string)e.Attribute("Include")!).ToList(),
                usings,
                mentions));
        }

        return result;
    }

    public static List<string> Check(IReadOnlyList<ProjectInfo> projects, IReadOnlyDictionary<string, string[]> allowed)
    {
        var violations = new List<string>();
        var moduleEdges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (module, targets) in allowed)
        {
            foreach (var t in targets.Where(t => !allowed.ContainsKey(t)))
            {
                violations.Add($"modules.dependencies.json: {module} -> unknown module {t}");
            }
        }

        foreach (var p in projects)
        {
            var self = Parse(p.Path);
            CheckTenantDataAccess(p, self, violations);
            if (self.Module is null)
            {
                CheckNonModule(p, self, violations);
                continue;
            }

            if (!allowed.ContainsKey(self.Module))
            {
                violations.Add($"{p.Path}: module {self.Module} is not declared in modules.dependencies.json");
            }

            foreach (var refPath in p.ProjectReferences)
            {
                var target = Parse(refPath);
                if (target.Module is null)
                {
                    if ((IsData(refPath) || IsOutbox(refPath)) && self.Layer == "Infrastructure")
                    {
                        continue; // the only door to tenant data (TenantDatabase) and the outbox built on it; see CheckTenantDataAccess
                    }

                    if (target.Name != "Kernel")
                    {
                        violations.Add($"{p.Path}: module project references {refPath} (only Kernel is allowed outside modules)");
                    }

                    continue;
                }

                if (target.Module == self.Module)
                {
                    CheckSameModuleLayer(p, self.Layer, target.Layer, violations);
                    continue;
                }

                if (target.Layer != "Application")
                {
                    violations.Add($"{p.Path}: references internals of {target.Module} ({refPath}); only {target.Module}.Application is public");
                }
                else if (self.Layer == "Domain")
                {
                    violations.Add($"{p.Path}: Domain may not depend on another module ({target.Module})");
                }
                else if (!allowed.TryGetValue(self.Module, out var ok) || !ok.Contains(target.Module))
                {
                    violations.Add($"{p.Path}: forbidden dependency {self.Module} -> {target.Module} (not declared in modules.dependencies.json)");
                }

                AddEdge(moduleEdges, self.Module, target.Module);
            }

            if (self.Layer == "Domain")
            {
                CheckDomainPurity(p, violations);
            }

            foreach (var u in p.Usings)
            {
                var m = UsingModuleRegex().Match(u);
                if (m.Success && m.Groups[1].Value != self.Module && m.Groups[2].Value != "Application")
                {
                    violations.Add($"{p.Path}: using {u} reaches into internals of {m.Groups[1].Value}");
                }
            }
        }

        foreach (var (module, targets) in allowed)
        {
            foreach (var t in targets)
            {
                AddEdge(moduleEdges, module, t);
            }
        }

        foreach (var cycle in FindCycles(moduleEdges))
        {
            violations.Add($"dependency cycle between modules: {cycle}");
        }

        return violations;
    }

    private static bool IsData(string path) => path.StartsWith("src/Infrastructure/Data/", StringComparison.Ordinal);

    private static bool IsOutbox(string path) => path.StartsWith("src/Infrastructure/Outbox/", StringComparison.Ordinal);

    private static bool IsAudit(string path) => path.StartsWith("src/Infrastructure/Audit/", StringComparison.Ordinal);

    /// <summary>Generated build output (obj/bin), whatever the OS path separator is.</summary>
    private static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal) || normalized.Contains("/bin/", StringComparison.Ordinal);
    }

    private static bool IsNpgsqlName(string name) => name == "Npgsql" || name.StartsWith("Npgsql.", StringComparison.Ordinal);

    private static bool IsEfName(string name) => name == "Microsoft.EntityFrameworkCore" || name.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal);

    /// <summary>Banned constructs found in the (non-comment) C# code under <paramref name="projectDirectory"/>.</summary>
    public static List<string> ScanSource(string projectDirectory)
    {
        var code = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .Select(File.ReadAllText)
            .ToList();
        var mentions = new List<string>();
        AddIf(mentions, "Npgsql", code, NpgsqlCodeRegex());
        AddIf(mentions, "DbContextSetup", code, DbContextSetupRegex());
        AddIf(mentions, "DirectDbContext", code, DirectDbContextRegex());
        AddIf(mentions, "RawSqlApi", code, RawSqlApiRegex());
        AddIf(mentions, "TenantSetting", code, TenantSettingRegex());
        return mentions;
    }

    private static void AddIf(List<string> mentions, string token, List<string> sources, Regex regex)
    {
        if (sources.Exists(text => regex.IsMatch(text)))
        {
            mentions.Add(token);
        }
    }

    /// <summary>
    /// ADR-002: tenant data is reached only through TenantDatabase (transaction + organization context), with EF Core as the data-access style.
    /// - Npgsql only in the Data project (a connection cannot be built elsewhere; exemption: the Api readiness probe).
    /// - EF Core packages and namespaces only in Data, Outbox and module Infrastructure layers; EF Design/Tools banned (the schema is SQL migrations, not EF migrations).
    /// - DbContexts derive from TenantDbContext (constructible only from a TenantTransaction): direct `: DbContext`, AddDbContext*, UseNpgsql and
    ///   DbContextOptions are Data-only; FromSqlRaw/ExecuteSqlRaw/SqlQueryRaw are banned everywhere (use interpolated FromSql/ExecuteSql/SqlQuery).
    /// - Dapper is not used.
    /// - Only a module Infrastructure layer may reference the Data or Outbox project; other layers hit the "only Kernel is allowed outside modules" rule.
    /// </summary>
    private static void CheckTenantDataAccess(ProjectInfo p, (string? Module, string Layer, string Name) self, List<string> violations)
    {
        var isData = IsData(p.Path);
        // Outbox is shared technical code built on TenantDbContext (ticket 08, ADR-003): its own EF mapping of the outbox tables, still only through
        // TenantDatabase. It gets EF like a module Infrastructure layer; Npgsql, DbContext setup and the tenant setting stay Data-only.
        // Audit (ticket 02) is the same kind of shared technical code: EF mapping of the audit table on top of TenantDbContext, nothing else.
        var mayUseEf = isData || IsOutbox(p.Path) || IsAudit(p.Path) || (self.Module is not null && self.Layer == "Infrastructure");
        const string Door = "tenant data goes through TenantDatabase, ADR-002";

        // src/Api is exempt only because Program.cs runs the readiness probe (SELECT 1, no tenant table) on a raw connection.
        // Remove this exemption when that probe moves behind the Data project (e.g. with the DI registration of TenantDatabase).
        if (!isData && !p.Path.StartsWith("src/Api/", StringComparison.Ordinal))
        {
            foreach (var pkg in p.PackageReferences.Where(IsNpgsqlName))
            {
                violations.Add($"{p.Path}: references package {pkg}; only Infrastructure/Data may use Npgsql ({Door})");
            }

            foreach (var u in p.Usings.Where(IsNpgsqlName))
            {
                violations.Add($"{p.Path}: uses {u}; only Infrastructure/Data may use Npgsql ({Door})");
            }

            if (p.Found.Contains("Npgsql"))
            {
                violations.Add($"{p.Path}: source mentions Npgsql; only Infrastructure/Data may use Npgsql ({Door})");
            }
        }

        if (!mayUseEf)
        {
            foreach (var pkg in p.PackageReferences.Where(IsEfName))
            {
                violations.Add($"{p.Path}: references package {pkg}; EF Core is only allowed in Infrastructure/Data, Infrastructure/Outbox, Infrastructure/Audit and module Infrastructure layers");
            }

            foreach (var u in p.Usings.Where(IsEfName))
            {
                violations.Add($"{p.Path}: uses {u}; EF Core is only allowed in Infrastructure/Data, Infrastructure/Outbox, Infrastructure/Audit and module Infrastructure layers");
            }
        }

        foreach (var pkg in p.PackageReferences.Where(x => x is "Microsoft.EntityFrameworkCore.Design" or "Microsoft.EntityFrameworkCore.Tools"))
        {
            violations.Add($"{p.Path}: references package {pkg}; EF migrations are not used, the schema is SQL files applied by MigrationRunner (ADR-002)");
        }

        foreach (var pkg in p.PackageReferences.Where(x => x == "Dapper"))
        {
            violations.Add($"{p.Path}: references package {pkg}; Dapper is not used, EF Core is the data-access style (ADR-002)");
        }

        foreach (var u in p.Usings.Where(x => x == "Dapper" || x.StartsWith("Dapper.", StringComparison.Ordinal)))
        {
            violations.Add($"{p.Path}: uses {u}; Dapper is not used, EF Core is the data-access style (ADR-002)");
        }

        if (!isData)
        {
            if (p.Found.Contains("DbContextSetup"))
            {
                violations.Add($"{p.Path}: AddDbContext/UseNpgsql/DbContextOptions outside Infrastructure/Data; contexts derive from TenantDbContext and are built from a TenantTransaction ({Door})");
            }

            if (p.Found.Contains("DirectDbContext"))
            {
                violations.Add($"{p.Path}: class derives from DbContext directly; derive from TenantDbContext ({Door})");
            }
        }

        if (!isData && p.Found.Contains("TenantSetting"))
        {
            violations.Add($"{p.Path}: mentions the app.organizacao_id setting; only TenantDatabase (Infrastructure/Data) sets the organization context ({Door})");
        }

        if (p.Found.Contains("RawSqlApi"))
        {
            violations.Add($"{p.Path}: FromSqlRaw/ExecuteSqlRaw/SqlQueryRaw is banned; use the interpolated FromSql/ExecuteSql/SqlQuery (parameterized) through the tenant context");
        }
    }

    private static void CheckNonModule(ProjectInfo p, (string? Module, string Layer, string Name) self, List<string> violations)
    {
        if (self.Name == "Kernel")
        {
            foreach (var r in p.ProjectReferences)
            {
                violations.Add($"{p.Path}: Kernel must not reference {r}");
            }
        }
    }

    private static void CheckSameModuleLayer(ProjectInfo p, string from, string to, List<string> violations)
    {
        string[] allowedTargets = from switch
        {
            "Domain" => [],
            "Application" => ["Domain"],
            _ => ["Application"],
        };
        if (!allowedTargets.Contains(to))
        {
            violations.Add($"{p.Path}: layer {from} may not reference layer {to}");
        }
    }

    private static void CheckDomainPurity(ProjectInfo p, List<string> violations)
    {
        if (p.Sdk != "Microsoft.NET.Sdk")
        {
            violations.Add($"{p.Path}: Domain must use Microsoft.NET.Sdk, not {p.Sdk}");
        }

        foreach (var pkg in p.PackageReferences)
        {
            violations.Add($"{p.Path}: Domain references package {pkg}");
        }

        foreach (var fw in p.FrameworkReferences)
        {
            violations.Add($"{p.Path}: Domain references framework {fw}");
        }

        foreach (var u in p.Usings.Where(u => ForbiddenInDomain.Any(f => u == f || u.StartsWith(f + ".", StringComparison.Ordinal))))
        {
            violations.Add($"{p.Path}: Domain uses infrastructure/framework namespace {u}");
        }

        foreach (var u in p.Usings.Where(u => u.EndsWith(".Infrastructure", StringComparison.Ordinal) || u.EndsWith(".Interface", StringComparison.Ordinal)))
        {
            violations.Add($"{p.Path}: Domain uses outer layer namespace {u}");
        }
    }

    private static void AddEdge(Dictionary<string, HashSet<string>> edges, string from, string to)
    {
        if (!edges.TryGetValue(from, out var set))
        {
            edges[from] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        set.Add(to);
    }

    private static List<string> FindCycles(Dictionary<string, HashSet<string>> edges)
    {
        var cycles = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();

        void Visit(string node)
        {
            var at = stack.IndexOf(node);
            if (at >= 0)
            {
                cycles.Add(string.Join(" -> ", stack.Skip(at).Append(node)));
                return;
            }

            if (!done.Add(node))
            {
                return;
            }

            stack.Add(node);
            if (edges.TryGetValue(node, out var next))
            {
                foreach (var n in next.Order(StringComparer.Ordinal))
                {
                    Visit(n);
                }
            }

            stack.RemoveAt(stack.Count - 1);
        }

        foreach (var node in edges.Keys.Order(StringComparer.Ordinal))
        {
            Visit(node);
        }

        return cycles;
    }

    /// <summary>src/&lt;Module&gt;/&lt;Layer&gt;/x.csproj -> module+layer; src/Kernel, src/Api, src/Infrastructure (shared technical code, not a module) -> no module.</summary>
    private static (string? Module, string Layer, string Name) Parse(string path)
    {
        var parts = path.Split('/');
        return parts is ["src", var module, var layer, _] && module != "Infrastructure" ? (module, layer, module) : (null, "", parts.Length > 1 ? parts[1] : path);
    }

    /// <summary>Code (not comment) lines mentioning the Npgsql identifier.</summary>
    [GeneratedRegex(@"^(?!\s*(?://|/?\*)).*\bNpgsql", RegexOptions.Multiline)]
    private static partial Regex NpgsqlCodeRegex();

    [GeneratedRegex(@"^(?!\s*(?://|/?\*)).*(?:\bAddDbContext\w*|\bUseNpgsql\b|\bDbContextOptions\w*)", RegexOptions.Multiline)]
    private static partial Regex DbContextSetupRegex();

    // class/record X [: A, ]DbContext (namespace prefix allowed, primary constructor allowed), or `using Alias = ...DbContext;`
    [GeneratedRegex(@"^(?!\s*(?://|/?\*)).*(?:\b(?:class|record)\s+\w+(?:<[^>]*>)?(?:\([^)]*\))?\s*:\s*(?:[\w.<>]+\s*,\s*)*(?:[\w.]+\.)?DbContext\b|\busing\s+\w+\s*=\s*(?:global::)?(?:[\w.]+\.)?DbContext\s*;)", RegexOptions.Multiline)]
    private static partial Regex DirectDbContextRegex();

    [GeneratedRegex(@"^(?!\s*(?://|/?\*)).*app\.organizacao_id", RegexOptions.Multiline)]
    private static partial Regex TenantSettingRegex();

    [GeneratedRegex(@"^(?!\s*(?://|/?\*)).*\b(?:FromSqlRaw|ExecuteSqlRaw|SqlQueryRaw)\b", RegexOptions.Multiline)]
    private static partial Regex RawSqlApiRegex();

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex UsingRegex();

    [GeneratedRegex(@"^Flow4Tech\.(\w+)\.(Domain|Application|Infrastructure|Interface)\b")]
    private static partial Regex UsingModuleRegex();
}
