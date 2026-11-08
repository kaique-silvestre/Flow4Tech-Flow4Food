using System.Text.RegularExpressions;

namespace Flow4Tech.Architecture.Tests;

/// <summary>
/// F4-0113: a ticket file carries its global ID in the name and the title (<c>issues/F4-NNNN-slug.md</c>, <c>packets/F4-NNNN.md</c>) and the index in
/// <c>docs/tickets.md</c> points only to files that exist. The local number of a slice is not a ticket identity.
/// </summary>
public sealed partial class TicketFilesTests
{
    [GeneratedRegex(@"^F4-\d{4}-[a-z0-9-]+\.md$")]
    private static partial Regex IssueName();

    [GeneratedRegex(@"^F4-\d{4}(-[a-z])?\.md$")]
    private static partial Regex PacketName();

    [GeneratedRegex(@"`(\.scratch/[^`]*\.md)`")]
    private static partial Regex IndexedPath();

    private static string Root() => Path.GetFullPath(Path.Combine(ModuleGraph.BackendRoot(), ".."));

    private static IEnumerable<string> Files(string folder) =>
        Directory.EnumerateDirectories(Path.Combine(Root(), ".scratch"), "S-*")
            .Select(slice => Path.Combine(slice, folder))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.md"));

    [Fact]
    public void Every_ticket_file_is_named_and_titled_with_its_global_id()
    {
        var issues = Files("issues").ToList();

        Assert.NotEmpty(issues);
        Assert.Empty(issues.Where(f => !IssueName().IsMatch(Path.GetFileName(f))).Select(f => Path.GetRelativePath(Root(), f)).ToList());
        Assert.Empty(issues.Where(f => !File.ReadLines(f).First().StartsWith("# F4-", StringComparison.Ordinal)).Select(f => Path.GetRelativePath(Root(), f)).ToList());
    }

    [Fact]
    public void Every_packet_is_named_and_titled_with_its_global_id()
    {
        var packets = Files("packets").ToList();

        Assert.NotEmpty(packets);
        Assert.Empty(packets.Where(f => !PacketName().IsMatch(Path.GetFileName(f))).Select(f => Path.GetRelativePath(Root(), f)).ToList());
        Assert.Empty(packets.Where(f => !File.ReadLines(f).First().StartsWith("# Packet F4-", StringComparison.Ordinal)).Select(f => Path.GetRelativePath(Root(), f)).ToList());
    }

    [Fact]
    public void Every_file_the_ticket_index_names_exists_and_every_ticket_file_is_indexed()
    {
        var index = File.ReadAllText(Path.Combine(Root(), "docs", "tickets.md"));
        var indexed = IndexedPath().Matches(index).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(indexed.Where(p => !File.Exists(Path.Combine(Root(), p))).ToList());
        var unindexed = Files("issues").Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/')).Where(p => !indexed.Contains(p)).ToList();
        Assert.Empty(unindexed);
    }
}
