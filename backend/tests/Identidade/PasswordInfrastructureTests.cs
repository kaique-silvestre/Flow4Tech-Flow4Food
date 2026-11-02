using System.Net;
using System.Text.RegularExpressions;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.2 / ADR-005: Argon2id, k-anonymity breach check, temporary credential. No database.</summary>
public sealed partial class PasswordInfrastructureTests
{
    private static readonly Argon2Options Fast = new() { MemoryKib = 1024, Iterations = 1, Parallelism = 1, MaxConcurrentHashes = 2 };

    [Fact]
    public void The_argon2id_primitive_matches_the_RFC_9106_test_vector()
    {
        // RFC 9106 section 5.3 (Argon2id): P = 32 x 0x01, S = 16 x 0x02, K = 8 x 0x03, X = 12 x 0x04, t = 3, m = 32 KiB, p = 4, tag length 32.
        var tag = Argon2PasswordHasher.Derive(
            Enumerable.Repeat((byte)1, 32).ToArray(),
            Enumerable.Repeat((byte)2, 16).ToArray(),
            memoryKib: 32,
            iterations: 3,
            parallelism: 4,
            length: 32,
            secret: Enumerable.Repeat((byte)3, 8).ToArray(),
            associatedData: Enumerable.Repeat((byte)4, 12).ToArray());

        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659", Convert.ToHexStringLower(tag));
    }

    [Fact]
    public async Task A_hash_is_an_argon2id_string_with_its_parameters_and_a_fresh_salt()
    {
        using var hasher = new Argon2PasswordHasher(Fast);

        var first = await hasher.HashAsync("uma-senha-longa-123");
        var second = await hasher.HashAsync("uma-senha-longa-123");

        Assert.Matches(@"^\$argon2id\$v=19\$m=1024,t=1,p=1\$[A-Za-z0-9+/]{22}\$[A-Za-z0-9+/]{43}$", first);
        Assert.NotEqual(first, second); // random salt per hash
        Assert.DoesNotContain("uma-senha-longa-123", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_accepts_the_right_password_and_refuses_any_other()
    {
        using var hasher = new Argon2PasswordHasher(Fast);
        var hash = await hasher.HashAsync("uma-senha-longa-123");

        Assert.True(await hasher.VerifyAsync("uma-senha-longa-123", hash));
        Assert.False(await hasher.VerifyAsync("uma-senha-longa-124", hash));
        Assert.False(await hasher.VerifyAsync("", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto qualquer")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$AAAA$BBBB")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=4294967295,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=1024,t=999999,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_malformed_or_hostile_stored_hash_fails_closed_without_throwing_or_allocating_a_huge_buffer(string stored)
    {
        using var hasher = new Argon2PasswordHasher(Fast);

        Assert.False(await hasher.VerifyAsync("uma-senha-longa-123", stored));
        Assert.True(hasher.NeedsRehash(stored));
    }

    [Fact]
    public async Task A_hash_made_with_other_parameters_needs_a_rehash_and_a_current_one_does_not()
    {
        using var old = new Argon2PasswordHasher(Fast);
        using var current = new Argon2PasswordHasher(Fast with { MemoryKib = 2048 });
        using var moreIterations = new Argon2PasswordHasher(Fast with { Iterations = 2 });
        using var moreLanes = new Argon2PasswordHasher(Fast with { Parallelism = 2 });
        var oldHash = await old.HashAsync("uma-senha-longa-123");

        Assert.False(old.NeedsRehash(oldHash));
        Assert.True(current.NeedsRehash(oldHash));
        Assert.True(await current.VerifyAsync("uma-senha-longa-123", oldHash)); // verifies with the parameters stored in the hash
        Assert.True(moreIterations.NeedsRehash(oldHash));
        Assert.True(moreLanes.NeedsRehash(oldHash));
    }

    [Fact]
    public async Task The_dummy_verification_does_the_work_and_answers_nothing_useful()
    {
        using var hasher = new Argon2PasswordHasher(Fast);
        await hasher.VerifyDummyAsync("qualquer-senha-1234");
        await hasher.VerifyDummyAsync("qualquer-senha-1234");
    }

    [Fact]
    public void The_default_parameters_are_at_least_the_owasp_floor()
    {
        var defaults = new Argon2Options();
        Assert.True(defaults.MemoryKib >= 19 * 1024);
        Assert.True(defaults.Iterations >= 2);
        Assert.True(defaults.Parallelism >= 1);
    }

    [Fact]
    public async Task The_local_list_refuses_common_passwords_ignoring_case_and_accepts_others()
    {
        var local = new LocalCompromisedPasswords();
        Assert.True(await local.IsCompromisedAsync("Password123"));
        Assert.True(await local.IsCompromisedAsync("1234567890"));
        Assert.False(await local.IsCompromisedAsync("cavalo-bateria-grampo-9"));
    }

    // SHA-1("password") = 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8
    private const string Prefix = "5BAA6";
    private const string Suffix = "1E4C9B93F3F0682250B6CF8331B7EE68FD8";

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task The_breach_check_sends_only_the_first_five_hex_characters_of_the_sha1_never_the_password_or_the_full_hash()
    {
        using var handler = new RecordingHandler(_ => Ok($"0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n{Suffix}:9545824\r\n00D4F6E8FA6EECAD2A3AA415EEC418D38EC:2\r\n"));
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.pwnedpasswords.com/") };

        var compromised = await new HibpPasswordCheck(http).IsCompromisedAsync("password");

        Assert.True(compromised);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/range/" + Prefix, request.RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, request.RequestUri.Query);
        var everythingSent = request.RequestUri.PathAndQuery + " " + string.Join(" ", request.Headers.Select(h => h.Key + "=" + string.Join(",", h.Value)));
        Assert.DoesNotContain("password", everythingSent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Suffix[..6], everythingSent, StringComparison.OrdinalIgnoreCase); // not even the start of the rest of the hash
        Assert.Contains("Add-Padding", request.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task A_password_missing_from_the_range_or_listed_only_as_padding_is_not_compromised()
    {
        using var absentHandler = new RecordingHandler(_ => Ok("0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n"));
        using var paddedHandler = new RecordingHandler(_ => Ok($"{Suffix}:0\r\n"));
        using var absent = new HttpClient(absentHandler, disposeHandler: false) { BaseAddress = new Uri("https://x.test/") };
        using var padded = new HttpClient(paddedHandler, disposeHandler: false) { BaseAddress = new Uri("https://x.test/") };

        Assert.False(await new HibpPasswordCheck(absent).IsCompromisedAsync("password"));
        Assert.False(await new HibpPasswordCheck(padded).IsCompromisedAsync("password")); // padding entries have count 0
    }

    [Fact]
    public async Task A_failing_breach_service_raises_so_the_caller_can_decide()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://x.test/") };

        await Assert.ThrowsAsync<HttpRequestException>(() => new HibpPasswordCheck(http).IsCompromisedAsync("password"));
    }

    private sealed class FakeCheck(bool answer, Exception? raises = null) : ICompromisedPasswordCheck
    {
        public int Calls { get; private set; }

        public Task<bool> IsCompromisedAsync(string password, CancellationToken cancellationToken = default)
        {
            Calls++;
            return raises is null ? Task.FromResult(answer) : Task.FromException<bool>(raises);
        }
    }

    private sealed class CapturingLog : ITechnicalLog
    {
        public List<string> Lines { get; } = [];

        public void Write(LogSeverity severity, string eventName, IReadOnlyDictionary<string, object?>? fields = null) =>
            Lines.Add(severity + " " + eventName + " " + string.Join(",", (fields ?? new Dictionary<string, object?>()).Select(f => f.Key + "=" + f.Value)));
    }

    [Fact]
    public async Task The_composite_asks_the_local_list_first_and_only_then_the_remote_service()
    {
        var local = new FakeCheck(true);
        var remote = new FakeCheck(true);
        Assert.True(await new CompositeCompromisedPasswordCheck(local, remote, new CapturingLog()).IsCompromisedAsync("abc"));
        Assert.Equal(0, remote.Calls); // a known-bad password never leaves the process

        var localMiss = new FakeCheck(false);
        Assert.True(await new CompositeCompromisedPasswordCheck(localMiss, remote, new CapturingLog()).IsCompromisedAsync("abc"));
        Assert.Equal(1, remote.Calls);

        Assert.False(await new CompositeCompromisedPasswordCheck(new FakeCheck(false), null, new CapturingLog()).IsCompromisedAsync("abc"));
    }

    [Fact]
    public async Task When_the_remote_service_is_down_the_local_list_still_applies_and_the_outage_is_logged_without_the_password()
    {
        var log = new CapturingLog();
        var composite = new CompositeCompromisedPasswordCheck(new FakeCheck(false), new FakeCheck(false, new HttpRequestException("falhou para senha-secreta-1234")), log);

        Assert.False(await composite.IsCompromisedAsync("senha-secreta-1234"));

        var line = Assert.Single(log.Lines);
        Assert.Contains("identidade.senha-vazada.indisponivel", line, StringComparison.Ordinal);
        Assert.DoesNotContain("senha-secreta-1234", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_temporary_credential_is_random_readable_and_passes_the_password_policy()
    {
        var generator = new TemporaryCredentialGenerator();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 500; i++)
        {
            var value = generator.Generate();
            Assert.Matches(Shape(), value);
            Assert.Empty(PoliticaDeSenha.Avaliar(value));
            seen.Add(value);
        }

        Assert.Equal(500, seen.Count);
    }

    [GeneratedRegex("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$")]
    private static partial Regex Shape();

    [Fact]
    public void A_temporary_credential_object_never_prints_its_value()
    {
        var credential = new CredencialTemporaria("ABCD-EFGH-JKLM-NPQR");
        Assert.DoesNotContain("ABCD", credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ABCD", $"{new ResultadoCriacaoOperacional(StatusCriacao.Criada, Guid.NewGuid(), credential)}", StringComparison.Ordinal);
    }

    [Fact]
    public void The_login_view_of_an_account_never_prints_the_hash()
    {
        var conta = new ContaParaLogin(Guid.NewGuid(), Guid.NewGuid(), TipoConta.Email, "$argon2id$v=19$m=1,t=1,p=1$c2FsdA$aGFzaA", false, true, 1);
        Assert.DoesNotContain("argon2", conta.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("argon2", $"{conta}", StringComparison.Ordinal);
    }

    [Fact]
    public void The_breach_service_is_only_ever_reached_over_https()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddIdentidade(o =>
        {
            o.UsarHibp = true;
            o.HibpBaseAddress = new Uri("http://api.pwnedpasswords.com/");
        }));
        services.AddIdentidade(o =>
        {
            o.UsarHibp = true;
            o.HibpBaseAddress = new Uri("https://api.pwnedpasswords.com/");
        });
    }
}
