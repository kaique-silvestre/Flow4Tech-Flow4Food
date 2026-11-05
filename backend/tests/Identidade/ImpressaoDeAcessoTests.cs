using Flow4Tech.Identidade.Domain;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.5.5: what counts as "the same device" and "the same place" for the new-login alert.</summary>
public sealed class ImpressaoDeAcessoTests
{
    [Fact]
    public void A_browser_update_is_the_same_device()
    {
        var antes = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        var depois = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.6167.85 Safari/537.36";

        Assert.Equal(ImpressaoDeAcesso.Aparelho(antes), ImpressaoDeAcesso.Aparelho(depois));
    }

    [Fact]
    public void Another_browser_or_system_is_another_device()
    {
        var chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        var firefox = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0";
        var iphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1";

        Assert.NotEqual(ImpressaoDeAcesso.Aparelho(chrome), ImpressaoDeAcesso.Aparelho(firefox));
        Assert.NotEqual(ImpressaoDeAcesso.Aparelho(chrome), ImpressaoDeAcesso.Aparelho(iphone));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_user_agent_gives_no_device(string? agente) => Assert.Null(ImpressaoDeAcesso.Aparelho(agente));

    [Fact]
    public void The_device_is_bounded_in_size() => Assert.True(ImpressaoDeAcesso.Aparelho(new string('a', 5000))!.Length <= 200);

    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0/24")]
    [InlineData("203.0.113.5", "203.0.113.0/24")]
    [InlineData("::ffff:203.0.113.77", "203.0.113.0/24")]
    [InlineData("2001:db8:abcd:12:aaaa:bbbb:cccc:dddd", "2001:db8:abcd:12::/64")]
    public void The_place_is_the_network_prefix_not_the_address(string ip, string esperado) => Assert.Equal(esperado, ImpressaoDeAcesso.Rede(ip));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    public void An_unknown_or_malformed_address_gives_no_place(string? ip) => Assert.Null(ImpressaoDeAcesso.Rede(ip));
}
