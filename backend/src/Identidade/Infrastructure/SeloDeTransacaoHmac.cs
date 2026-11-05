using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Microsoft.IdentityModel.Tokens;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>
/// Signs the state of a Google round trip: <c>base64url(json).base64url(HMAC-SHA256)</c>. The key is DERIVED from the token signing key (<c>Flow4Tech:Tokens:AppKey</c>) with a fixed label, so the same secret never
/// signs two different things and no extra secret has to be provisioned; rotating the key ends the round trips in flight (they last 10 minutes). The content is signed, NOT encrypted: it holds the PKCE verifier,
/// which only matters together with the authorization code that lands in this same browser. Anything not made by this key, tampered, malformed, oversized or expired opens as null.
/// </summary>
public sealed class SeloDeTransacaoHmac : ISeloDeTransacao
{
    private const int TamanhoMaximo = 2048;
    private static readonly JsonSerializerOptions Json = new();
    private readonly byte[] _chave;
    private readonly IClock _clock;

    public SeloDeTransacaoHmac(string? chave, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (string.IsNullOrEmpty(chave) || Encoding.UTF8.GetByteCount(chave) < TokensOptions.MinimumKeyBytes)
        {
            throw new InvalidOperationException($"Flow4Tech:Tokens:AppKey must be at least {TokensOptions.MinimumKeyBytes} bytes.");
        }

        _clock = clock;
        _chave = HMACSHA256.HashData(Encoding.UTF8.GetBytes(chave), Encoding.ASCII.GetBytes("f4t-oidc-transacao-v1"));
    }

    public string Selar(TransacaoOidc transacao)
    {
        ArgumentNullException.ThrowIfNull(transacao);
        var corpo = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(transacao, Json));
        return Base64UrlEncoder.Encode(corpo) + "." + Base64UrlEncoder.Encode(HMACSHA256.HashData(_chave, corpo));
    }

    public TransacaoOidc? Abrir(string? selo)
    {
        if (string.IsNullOrEmpty(selo) || selo.Length > TamanhoMaximo)
        {
            return null;
        }

        var partes = selo.Split('.');
        if (partes.Length != 2)
        {
            return null;
        }

        try
        {
            var corpo = Base64UrlEncoder.DecodeBytes(partes[0]);
            var marca = Base64UrlEncoder.DecodeBytes(partes[1]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_chave, corpo), marca))
            {
                return null;
            }

            var transacao = JsonSerializer.Deserialize<TransacaoOidc>(corpo, Json);
            return transacao is not null && transacao.ExpiraEm > _clock.UtcNow ? transacao : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
