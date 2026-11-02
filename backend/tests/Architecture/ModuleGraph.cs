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
    IReadOnlyList<string> Usings);

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
                .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
                .SelectMany(f => UsingRegex().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
                .ToList();
            result.Add(new ProjectInfo(
                rel,
                (string?)doc.Root?.Attribute("Sdk") ?? "",
                refs,
                doc.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToList(),
                doc.Descendants("FrameworkReference").Select(e => (string)e.Attribute("Include")!).ToList(),
                usings));
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

    /// <summary>src/&lt;Module&gt;/&lt;Layer&gt;/x.csproj -> module+layer; src/Kernel, src/Api -> no module.</summary>
    private static (string? Module, string Layer, string Name) Parse(string path)
    {
        var parts = path.Split('/');
        return parts is ["src", var module, var layer, _] ? (module, layer, module) : (null, "", parts.Length > 1 ? parts[1] : path);
    }

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex UsingRegex();

    [GeneratedRegex(@"^Flow4Tech\.(\w+)\.(Domain|Application|Infrastructure|Interface)\b")]
    private static partial Regex UsingModuleRegex();
}
