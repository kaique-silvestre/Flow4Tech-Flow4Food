namespace Flow4Tech.Architecture.Tests;

/// <summary>Ticket 04: there is no test door in the product code. Sessions are real rows with signed tokens issued by the same service as the login.</summary>
public sealed class SessionDoorTests
{
    private static IEnumerable<string> SourceFiles()
    {
        var src = Path.Combine(ModuleGraph.BackendRoot(), "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("X-Test-Session")]
    [InlineData("TestSession")]
    [InlineData("SignedHeaderSessionSource")]
    public void No_product_code_mentions_the_removed_test_session(string marker)
    {
        var offenders = SourceFiles().Where(f => File.ReadAllText(f).Contains(marker, StringComparison.Ordinal)).Select(Path.GetFileName).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_signing_key_is_never_a_literal_in_product_code()
    {
        // Keys come from configuration (Flow4Tech:Tokens:*); a quoted "chave-" style literal or a long base64 blob next to "Key" would be a committed secret.
        var offenders = SourceFiles()
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), "(AppKey|ControlKey|SigningKey)\\s*=\\s*\"[^\"]{8,}\""))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
