using Flow4Tech.Api;

namespace Flow4Tech.Tests;

public sealed class HostBindingTests
{
    [Fact]
    public void Injected_PORT_is_bound_on_all_interfaces()
    {
        Assert.Equal("http://*:3456", HostBinding.ResolveUrl("3456", otherBindingConfigured: false));
        Assert.Equal("http://*:3456", HostBinding.ResolveUrl("3456", otherBindingConfigured: true));
    }

    [Fact]
    public void Without_PORT_it_falls_back_to_8080()
    {
        Assert.Equal("http://*:8080", HostBinding.ResolveUrl(null, otherBindingConfigured: false));
        Assert.Equal("http://*:8080", HostBinding.ResolveUrl("  ", otherBindingConfigured: false));
    }

    [Fact]
    public void Without_PORT_an_explicit_binding_is_left_alone()
    {
        Assert.Null(HostBinding.ResolveUrl(null, otherBindingConfigured: true));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public void An_invalid_PORT_fails_with_a_clear_message(string port)
    {
        var ex = Assert.Throws<FormatException>(() => HostBinding.ResolveUrl(port, otherBindingConfigured: false));

        Assert.Contains("PORT", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3f2a9c1d0e", "3f2a9c1d0e")]
    [InlineData("  3F2A9C1D0E4B5A69788796A5B4C3D2E1F0A9B8C7 ", "3F2A9C1D0E4B5A69788796A5B4C3D2E1F0A9B8C7")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not-a-sha!", null)]
    [InlineData("abc", null)]
    public void The_commit_is_exposed_only_when_it_looks_like_a_git_sha(string? raw, string? expected)
    {
        Assert.Equal(expected, BuildInfo.NormalizeCommit(raw));
    }
}
