using System.Text;
using System.Text.RegularExpressions;

namespace Flow4Tech.Architecture.Tests;

/// <summary>
/// F4-0112: the root <c>CLAUDE.md</c> is loaded into every agent session, so it stays small and points to <c>docs/agents/</c> for module detail. A new topic goes in a
/// <c>docs/agents</c> file with one line in the index, not as a new block in the root.
/// </summary>
public sealed partial class ClaudeMdSizeTests
{
    private const int LimitBytes = 16 * 1024;

    [GeneratedRegex(@"`(docs/agents/[a-z0-9-]+\.md)`")]
    private static partial Regex AgentDocPointer();

    private static string RepoRoot() => Path.GetFullPath(Path.Combine(ModuleGraph.BackendRoot(), ".."));

    [Fact]
    public void The_root_instructions_file_stays_under_the_size_limit()
    {
        var bytes = Encoding.UTF8.GetByteCount(File.ReadAllText(Path.Combine(RepoRoot(), "CLAUDE.md")));

        Assert.True(bytes <= LimitBytes, $"CLAUDE.md has {bytes} bytes (limit {LimitBytes}). Move the detail to docs/agents/<topic>.md and keep one pointer line in the index.");
    }

    [Fact]
    public void Every_pointer_in_the_root_file_names_a_file_that_exists()
    {
        var root = RepoRoot();
        var pointers = AgentDocPointer().Matches(File.ReadAllText(Path.Combine(root, "CLAUDE.md"))).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(pointers);
        var missing = pointers.Where(p => !File.Exists(Path.Combine(root, p))).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_topic_file_is_reachable_from_the_index()
    {
        var root = RepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "CLAUDE.md"));
        var unreachable = Directory.EnumerateFiles(Path.Combine(root, "docs", "agents"), "*.md")
            .Select(f => "docs/agents/" + Path.GetFileName(f))
            .Where(f => !text.Contains(f, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(unreachable);
    }
}
