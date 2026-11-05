using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Flow4Tech.Identidade.Domain;

/// <summary>
/// What the new-login alert compares (spec 5.5.5), pure. "Device" is the user agent with every digit removed (so a browser update is still the same device); "place" is the network
/// prefix of the address (IPv4 /24, IPv6 /64), not the address (mobile and home connections change addresses all the time). There is no geolocation provider: the place is a network,
/// not a city. Both are kept in clear text next to the session, which already stores the address and the agent.
/// </summary>
public static class ImpressaoDeAcesso
{
    public const int TamanhoMaximo = 200;

    public static string? Aparelho(string? agente)
    {
        if (string.IsNullOrWhiteSpace(agente))
        {
            return null;
        }

        var text = new StringBuilder(Math.Min(agente.Length, TamanhoMaximo));
        var lastWasSpace = false;
        foreach (var c in agente.Trim())
        {
            if (char.IsAsciiDigit(c) || c is '.' or '_')
            {
                continue;
            }

            var space = char.IsWhiteSpace(c);
            if (space && lastWasSpace)
            {
                continue;
            }

            lastWasSpace = space;
            text.Append(space ? ' ' : char.ToLower(c, CultureInfo.InvariantCulture));
            if (text.Length >= TamanhoMaximo)
            {
                break;
            }
        }

        return text.Length == 0 ? null : text.ToString();
    }

    public static string? Rede(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => string.Create(CultureInfo.InvariantCulture, $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24"),
            AddressFamily.InterNetworkV6 => string.Create(CultureInfo.InvariantCulture, $"{bytes[0] << 8 | bytes[1]:x}:{bytes[2] << 8 | bytes[3]:x}:{bytes[4] << 8 | bytes[5]:x}:{bytes[6] << 8 | bytes[7]:x}::/64"),
            _ => null,
        };
    }
}
