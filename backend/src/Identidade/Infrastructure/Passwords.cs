using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel.Audit;
using Konscious.Security.Cryptography;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>Argon2id cost parameters (RFC 9106). Defaults follow the OWASP Password Storage Cheat Sheet (46 MiB, 2 iterations, 1 lane). Changing them rehashes on the next login.</summary>
public sealed record Argon2Options
{
    public int MemoryKib { get; init; } = 47104;

    public int Iterations { get; init; } = 2;

    public int Parallelism { get; init; } = 1;

    /// <summary>At most this many hashes run at once: each one holds <see cref="MemoryKib"/> of memory, so a burst of logins cannot exhaust the process.</summary>
    public int MaxConcurrentHashes { get; init; } = Environment.ProcessorCount;
}

/// <summary>
/// Argon2id through Konscious.Security.Cryptography.Argon2 1.3.1 (managed, no native code; checked against the RFC 9106 test vector in the tests). Stored form is the
/// PHC string <c>$argon2id$v=19$m=..,t=..,p=..$salt$hash</c> (16-byte random salt, 32-byte tag, unpadded base64), so the parameters travel with the hash and old hashes keep
/// verifying after the parameters change. A stored hash is untrusted input: its parameters are bounded before any memory is allocated.
/// </summary>
public sealed partial class Argon2PasswordHasher : IPasswordHasher, IDisposable
{
    private const int SaltBytes = 16;
    private const int TagBytes = 32;
    private const int Version = 19;
    private const int MaxMemoryKib = 262_144; // 256 MiB: a hostile stored value must not make the server allocate gigabytes
    private const int MaxIterations = 20;
    private const int MaxParallelism = 16;

    private readonly Argon2Options _options;
    private readonly SemaphoreSlim _gate;
    private readonly string _dummyHash;

    public Argon2PasswordHasher(Argon2Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Parallelism < 1 || options.Parallelism > MaxParallelism
            || options.MemoryKib < 8 * options.Parallelism || options.MemoryKib > MaxMemoryKib
            || options.Iterations < 1 || options.Iterations > MaxIterations
            || options.MaxConcurrentHashes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Argon2 parameters out of range.");
        }

        _options = options;
        _gate = new SemaphoreSlim(options.MaxConcurrentHashes);
        // A real hash of a random password, made with the CURRENT parameters: verifying against it costs what verifying a real account costs.
        _dummyHash = Encode(RandomNumberGenerator.GetBytes(SaltBytes), options.MemoryKib, options.Iterations, options.Parallelism, Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(SaltBytes), options.MemoryKib, options.Iterations, options.Parallelism, TagBytes));
    }

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrEmpty(password); // the policy refuses an empty password long before this; the primitive cannot hash one
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var tag = await RunAsync(() => Derive(Encoding.UTF8.GetBytes(password), salt, _options.MemoryKib, _options.Iterations, _options.Parallelism, TagBytes), cancellationToken);
        return Encode(salt, _options.MemoryKib, _options.Iterations, _options.Parallelism, tag);
    }

    public async Task<bool> VerifyAsync(string password, string encodedHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(encodedHash, out var parsed))
        {
            return false;
        }

        // The primitive refuses an empty password: do the same work with a placeholder and answer false whatever comes out (no stored hash can be of an empty password).
        var empty = password.Length == 0;
        var tag = await RunAsync(() => Derive(empty ? [0] : Encoding.UTF8.GetBytes(password), parsed.Salt, parsed.MemoryKib, parsed.Iterations, parsed.Parallelism, parsed.Tag.Length), cancellationToken);
        return CryptographicOperations.FixedTimeEquals(tag, parsed.Tag) && !empty;
    }

    public async Task VerifyDummyAsync(string password, CancellationToken cancellationToken = default)
    {
        await VerifyAsync(password, _dummyHash, cancellationToken);
    }

    public bool NeedsRehash(string encodedHash) =>
        !TryParse(encodedHash, out var p)
        || p.MemoryKib != _options.MemoryKib || p.Iterations != _options.Iterations || p.Parallelism != _options.Parallelism
        || p.Salt.Length != SaltBytes || p.Tag.Length != TagBytes;

    public void Dispose() => _gate.Dispose();

    /// <summary>The raw Argon2id primitive (RFC 9106), exposed to tests so the library wiring is proven against the RFC vector.</summary>
    internal static byte[] Derive(byte[] password, byte[] salt, int memoryKib, int iterations, int parallelism, int length, byte[]? secret = null, byte[]? associatedData = null)
    {
        using var argon2 = new Argon2id(password)
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        if (secret is not null)
        {
            argon2.KnownSecret = secret;
        }

        if (associatedData is not null)
        {
            argon2.AssociatedData = associatedData;
        }

        return argon2.GetBytes(length);
    }

    private async Task<byte[]> RunAsync(Func<byte[]> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(work, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Encode(byte[] salt, int memoryKib, int iterations, int parallelism, byte[] tag) =>
        string.Create(CultureInfo.InvariantCulture, $"$argon2id$v={Version}$m={memoryKib},t={iterations},p={parallelism}${Base64(salt)}${Base64(tag)}");

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[]? FromBase64(string text)
    {
        var padded = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
        Span<byte> buffer = new byte[padded.Length];
        return Convert.TryFromBase64String(padded, buffer, out var written) ? buffer[..written].ToArray() : null;
    }

    [GeneratedRegex(@"^\$argon2id\$v=19\$m=(\d{1,7}),t=(\d{1,3}),p=(\d{1,3})\$([A-Za-z0-9+/]{11,86})\$([A-Za-z0-9+/]{22,86})$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex PhcFormat();

    private sealed record Parsed(int MemoryKib, int Iterations, int Parallelism, byte[] Salt, byte[] Tag);

    private static bool TryParse(string? encoded, out Parsed parsed)
    {
        parsed = null!;
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 256)
        {
            return false;
        }

        var match = PhcFormat().Match(encoded);
        if (!match.Success)
        {
            return false;
        }

        var memory = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var iterations = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        var lanes = int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture);
        var salt = FromBase64(match.Groups[4].Value);
        var tag = FromBase64(match.Groups[5].Value);
        if (salt is null || tag is null || lanes < 1 || lanes > MaxParallelism || iterations < 1 || iterations > MaxIterations
            || memory < 8 * lanes || memory > MaxMemoryKib || salt.Length < 8 || tag.Length < 16)
        {
            return false;
        }

        parsed = new Parsed(memory, iterations, lanes, salt, tag);
        return true;
    }
}

/// <summary>
/// A short offline list of very common passwords (the fallback when the breach service is off or unreachable). It is deliberately small: it catches the worst and
/// costs nothing; the real coverage is the k-anonymity range check. Compared ignoring case, on the normalized text.
/// </summary>
public sealed class LocalCompromisedPasswords : ICompromisedPasswordCheck
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password12", "password123", "password1234", "passw0rd123", "p@ssw0rd123", "1234567890", "12345678901", "123456789012",
        "0123456789", "1111111111", "0000000000", "qwertyuiop", "qwerty1234", "qwerty12345", "asdfghjkl", "asdfghjkl1", "1q2w3e4r5t", "1qaz2wsx3edc",
        "abcdefghij", "abcd123456", "iloveyou123", "letmein123", "welcome123", "admin12345", "administrator", "changeme123", "trustno1234",
        "senha12345", "senha123456", "senha@12345", "minhasenha", "minhasenha1", "minhasenha123", "mudar12345", "mudar123456", "bemvindo123", "brasil12345",
        "flamengo123", "corinthians1", "123mudar123", "1234qwer1234", "qazwsxedcrfv", "0987654321", "9876543210", "1234512345",
    };

    public Task<bool> IsCompromisedAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Task.FromResult(Common.Contains(password));
    }
}

/// <summary>
/// "Have I Been Pwned" range API (k-anonymity, spec 5.2.2). Sends ONLY the first 5 hex characters of the SHA-1 of the password, as the URL path
/// <c>range/XXXXX</c>; the answer is the list of suffixes of that range and the comparison happens here. The password and the rest of the hash never leave the process.
/// <c>Add-Padding</c> asks for dummy entries (count 0) so the size of the answer does not hint at the range. SHA-1 is the protocol's, not our choice: it is not used to
/// protect anything (that is Argon2id).
/// </summary>
public sealed class HibpPasswordCheck(HttpClient http) : ICompromisedPasswordCheck
{
    public async Task<bool> IsCompromisedAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        var digest = Convert.ToHexString(Sha1(Encoding.UTF8.GetBytes(password)));
        var prefix = digest[..5];
        var suffix = digest[5..];

        using var request = new HttpRequestMessage(HttpMethod.Get, "range/" + prefix);
        request.Headers.Add("Add-Padding", "true");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Flow4Tech-Identidade", "1"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0
                && string.Equals(line[..colon], suffix, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                && count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Sha1(byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }
}

/// <summary>
/// The local list first (a known-bad password never leaves the process), then the remote range check when configured. If the remote service is unreachable it logs a warning (no password, no hash, no
/// message of the exception) and then follows <c>HibpIndisponivelRecusa</c>: off (default) the person is not blocked and the local list result stands; on, it raises
/// <see cref="VerificacaoDeSenhaIndisponivelException"/> and the application services refuse the password with the typed result <c>VerificacaoIndisponivel</c>.
/// </summary>
public sealed class CompositeCompromisedPasswordCheck(ICompromisedPasswordCheck local, ICompromisedPasswordCheck? remote, ITechnicalLog log, bool recusaSeIndisponivel) : ICompromisedPasswordCheck
{
    public CompositeCompromisedPasswordCheck(ICompromisedPasswordCheck local, ICompromisedPasswordCheck? remote, ITechnicalLog log)
        : this(local, remote, log, false)
    {
    }

    public bool RecusaSeIndisponivel { get; } = recusaSeIndisponivel;

    public async Task<bool> IsCompromisedAsync(string password, CancellationToken cancellationToken = default)
    {
        if (await local.IsCompromisedAsync(password, cancellationToken))
        {
            return true;
        }

        if (remote is null)
        {
            return false;
        }

        try
        {
            return await remote.IsCompromisedAsync(password, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            log.Write(LogSeverity.Warning, "identidade.senha-vazada.indisponivel", new Dictionary<string, object?> { ["erro"] = ex.GetType().Name });
            return RecusaSeIndisponivel ? throw new VerificacaoDeSenhaIndisponivelException("Password breach check unavailable.", ex) : false;
        }
    }
}

/// <summary>
/// Temporary credential: 16 characters from a 32-symbol alphabet without look-alikes (no 0/O, 1/I), drawn from the operating system CSPRNG (80 bits), shown as
/// <c>XXXX-XXXX-XXXX-XXXX</c>. 32 symbols means no modulo bias.
/// </summary>
public sealed class TemporaryCredentialGenerator : ITemporaryCredentialGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 symbols: no 0, 1, I, O

    public string Generate()
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet.AsSpan(), 16);
        return string.Concat(new string(chars, 0, 4), "-", new string(chars, 4, 4), "-", new string(chars, 8, 4), "-", new string(chars, 12, 4));
    }
}
