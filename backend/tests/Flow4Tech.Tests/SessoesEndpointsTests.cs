using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Flow4Tech.Api;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Flow4Tech.Kernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

/// <summary>
/// Ticket 04 (spec 5.3) over HTTP: login, access token, rotating refresh cookie, CSRF, the "must change password" and "no active unit" states, listing and
/// revocation. Real PostgreSQL with the application credential (RLS on) and the real token service.
/// <para>Cookie testing without TLS: the server always sets <c>Secure</c> (never depends on the request scheme). The test client talks plain HTTP to an in-memory server, where a
/// real cookie jar would refuse to send a Secure cookie, so these tests read <c>Set-Cookie</c> themselves, assert its attributes and replay it in a <c>Cookie</c> header.</para>
/// </summary>
public sealed class SessoesEndpointsTests : IClassFixture<SessoesEndpointsDatabase>
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";
    private const string Csrf = "X-F4T-Csrf";
    private const string CookieName = "__Host-f4t_refresh";
    private const string DonoSenha = "Senha-do-dono-123";

    internal static readonly Guid OrgA = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    internal static readonly Guid OrgB = Guid.Parse("0197b000-0000-7000-8000-00000000000b");
    internal static readonly Guid UnitA1 = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    internal static readonly Guid UnitA2 = Guid.Parse("0197a000-0000-7000-8000-0000000000a2");
    internal static readonly Guid UnitB1 = Guid.Parse("0197b000-0000-7000-8000-0000000000b1");

    private readonly SessoesEndpointsDatabase _db;

    public SessoesEndpointsTests(SessoesEndpointsDatabase db) => _db = db;

    private string _appConnection => _db.AppConnection;

    internal static async Task<int> Exec(TenantTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    internal static string MigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "modules.dependencies.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("backend root not found"), "migrations");
    }

    // ---- fixtures ---------------------------------------------------------------------------------------------------------------------------------

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed record Conta(Guid Id, string Usuario, string Email, string Senha);

    private ApiFactory Api(TestClock? clock = null, CapturedLogs? logs = null, Dictionary<string, string?>? extra = null, Action<IServiceCollection>? services = null) => new(_appConnection, clock, logs, extra, services);

    private const string NovaSenhaPadrao = "senha-longa-da-pessoa-2026";

    /// <summary>
    /// A fresh owner for an organization is created once (the founder only exists once); every call returns a NEW operational account with a known password (temporary credential
    /// already replaced), linked to the given units. Several tests mutate accounts, so none shares one.
    /// </summary>
    private async Task<Conta> NewAccountAsync(ApiFactory api, Guid[] units, string? name = null, bool keepTemporary = false)
    {
        var contas = api.Services.GetRequiredService<GestaoDeContas>();
        var dono = await EnsureOwnerAsync(api);
        name ??= "u" + Guid.NewGuid().ToString("N")[..10];
        var criada = await contas.CriarOperacionalAsync(new FakeRequest(dono, Guid.NewGuid(), new OrganizationContext(OrgA, UnitA1)), name, "Pessoa " + name);
        Assert.Equal(StatusCriacao.Criada, criada.Status);
        var senha = criada.Credencial!.Valor;
        if (!keepTemporary)
        {
            var troca = await api.Services.GetRequiredService<TrocaDeSenha>().TrocarAsync(OrgA, criada.ContaId!.Value, senha, NovaSenhaPadrao);
            Assert.Equal(StatusTrocaDeSenha.Trocada, troca.Status);
            senha = NovaSenhaPadrao;
        }

        await LinkAsync(criada.ContaId!.Value, units);
        return new Conta(criada.ContaId.Value, name, "", senha);
    }

    private async Task<Guid> EnsureOwnerAsync(ApiFactory api)
    {
        var existing = await OwnerIdAsync();
        if (existing is { } id)
        {
            return id;
        }

        var result = await api.Services.GetRequiredService<GestaoDeContas>().CriarDonoAsync(OrgA, "dono@a.com", "Dono A", DonoSenha);
        Assert.Equal(StatusCriacao.Criada, result.Status);
        _db.Owner = result.ContaId;
        await LinkAsync(result.ContaId!.Value, [UnitA1]);
        return result.ContaId.Value;
    }

    private async Task<Guid?> OwnerIdAsync()
    {
        if (_db.Owner is not null)
        {
            return _db.Owner;
        }

        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA1));
        await using var command = new NpgsqlCommand("SELECT id FROM contas WHERE dono LIMIT 1", tx.Connection, tx.Transaction);
        return _db.Owner = await command.ExecuteScalarAsync() as Guid?;
    }

    private async Task LinkAsync(Guid conta, Guid[] units)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA1));
        foreach (var unit in units)
        {
            await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @c, @u)", ("o", OrgA), ("c", conta), ("u", unit));
        }

        await tx.CommitAsync();
    }

    private async Task<List<T>> QueryAsync<T>(Guid org, string sql, Func<NpgsqlDataReader, T> map, params (string Name, object Value)[] args)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, org == OrgA ? UnitA1 : UnitB1));
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    // ---- HTTP helpers -----------------------------------------------------------------------------------------------------------------------------

    private sealed record SessaoDto(string AccessToken, int ExpiraEmSegundos, Guid SessaoId, Guid? UnidadeAtivaId, bool DeveTrocarSenha, bool RequerSelecaoDeUnidade, DateTimeOffset SessaoExpiraEm);

    private sealed record UnidadeDto(Guid Id, string Nome);

    private sealed record UnidadeAtivaDto(Guid UnidadeId);

    private sealed record SessaoItemDto(Guid Id, Guid ContaId, Guid? UnidadeAtivaId, DateTimeOffset CriadaEm, DateTimeOffset UltimaAtividadeEm, DateTimeOffset ExpiraEm, string? Ip, string? Agente, bool Atual);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Cookie(string Name, string Value, Dictionary<string, string> Attributes)
    {
        public string Header => $"{Name}={Value}";
    }

    private static HttpRequestMessage Post(string path, object? body = null, string? token = null, string? cookie = null, bool csrf = false, Action<HttpRequestMessage>? tweak = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (csrf)
        {
            request.Headers.Add(Csrf, "1");
        }

        tweak?.Invoke(request);
        return request;
    }

    private static HttpRequestMessage Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, Func<HttpRequestMessage> build)
    {
        using var request = build();
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static Cookie? ReadCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var line = values.SingleOrDefault(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var parts = line.Split(';', StringSplitOptions.TrimEntries);
        var (name, value) = (parts[0][..parts[0].IndexOf('=', StringComparison.Ordinal)], parts[0][(parts[0].IndexOf('=', StringComparison.Ordinal) + 1)..]);
        var attributes = parts.Skip(1).Select(p => p.Split('=', 2)).ToDictionary(p => p[0].ToUpperInvariant(), p => p.Length > 1 ? p[1] : "", StringComparer.Ordinal);
        return new Cookie(name, value, attributes);
    }

    private static async Task<(SessaoDto Sessao, Cookie Cookie)> LoginAsync(HttpClient client, Conta conta, string? senha = null)
    {
        using var request = Post("/login", new { organizacaoDoLogin = OrgA, usuario = conta.Usuario, senha = senha ?? conta.Senha }, csrf: true);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ((await response.Content.ReadFromJsonAsync<SessaoDto>())!, ReadCookie(response)!);
    }

    private static async Task<(HttpStatusCode Status, SessaoDto? Sessao, Cookie? Cookie)> RefreshAsync(HttpClient client, Cookie cookie)
    {
        using var request = Post("/refresh", cookie: cookie.Header, csrf: true);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SessaoDto>() : null, ReadCookie(response));
    }

    // ---- login -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_by_user_name_returns_the_access_token_in_the_body_and_the_refresh_only_in_a_hardened_cookie()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);

        using var request = Post("/login", new { organizacaoDoLogin = OrgA, usuario = conta.Usuario, senha = conta.Senha }, csrf: true);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<SessaoDto>(body, Web)!;
        var cookie = ReadCookie(response)!;
        Assert.Equal(CookieName, cookie.Name);
        Assert.DoesNotContain(cookie.Value, body, StringComparison.Ordinal); // the refresh token is not in the body
        Assert.True(cookie.Attributes.ContainsKey("HTTPONLY"));
        Assert.True(cookie.Attributes.ContainsKey("SECURE")); // always, also over plain HTTP in tests
        Assert.Equal("STRICT", cookie.Attributes["SAMESITE"].ToUpperInvariant());
        Assert.Equal("/", cookie.Attributes["PATH"]);
        Assert.False(cookie.Attributes.ContainsKey("DOMAIN")); // __Host- rule
        Assert.True(int.Parse(cookie.Attributes["MAX-AGE"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal(UnitA1, dto.UnidadeAtivaId); // one linked unit: it becomes the active one
        Assert.False(dto.DeveTrocarSenha);
        Assert.False(dto.RequerSelecaoDeUnidade);
        Assert.Equal(300, dto.ExpiraEmSegundos);
        Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
        // The token works.
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", dto.AccessToken)));
    }

    [Fact]
    public async Task Login_by_email_works_for_an_account_with_email()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        await EnsureOwnerAsync(api);

        using var request = Post("/login", new { email = " DONO@a.com ", senha = DonoSenha }, csrf: true);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UnitA1, (await response.Content.ReadFromJsonAsync<SessaoDto>())!.UnidadeAtivaId);
    }

    [Fact]
    public async Task Every_login_failure_is_the_same_response_without_cookie_for_known_and_unknown_accounts()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var desativada = await NewAccountAsync(api, [UnitA1]);
        var dono = await EnsureOwnerAsync(api);
        await api.Services.GetRequiredService<GestaoDeContas>().DesativarAsync(new FakeRequest(dono, Guid.NewGuid(), new OrganizationContext(OrgA, UnitA1)), desativada.Id);

        var attempts = new object[]
        {
            new { organizacaoDoLogin = OrgA, usuario = conta.Usuario, senha = "senha-errada-123456" },
            new { organizacaoDoLogin = OrgA, usuario = "naoexiste", senha = conta.Senha },
            new { organizacaoDoLogin = OrgB, usuario = conta.Usuario, senha = conta.Senha },
            new { organizacaoDoLogin = Guid.NewGuid(), usuario = conta.Usuario, senha = conta.Senha },
            new { organizacaoDoLogin = OrgA, usuario = desativada.Usuario, senha = desativada.Senha },
            new { email = "ninguem@x.com", senha = conta.Senha },
            new { email = "dono@a.com", senha = "senha-errada-123456" },
        };
        var results = new List<(HttpStatusCode, string, bool)>();
        foreach (var attempt in attempts)
        {
            using var request = Post("/login", attempt, csrf: true);
            using var response = await client.SendAsync(request);
            results.Add((response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.Contains("Set-Cookie")));
        }

        Assert.All(results, r => Assert.Equal((HttpStatusCode.Unauthorized, string.Empty, false), r));
    }

    [Fact]
    public async Task Repeated_failures_lock_the_login_with_429_and_Retry_After_for_known_and_unknown_names_alike()
    {
        await using var api = Api(extra: new Dictionary<string, string?> { ["Flow4Tech:Identidade:Bloqueio:FalhasOperacional"] = "3" });
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var observed = new Dictionary<string, List<(HttpStatusCode Status, string Body, bool Cookie, bool RetryAfter)>>();

        foreach (var usuario in new[] { conta.Usuario, "naoexiste01" })
        {
            var list = observed[usuario] = [];
            for (var i = 0; i < 5; i++)
            {
                using var request = Post("/login", new { organizacaoDoLogin = OrgA, usuario, senha = "senha-errada-123456" }, csrf: true);
                using var response = await client.SendAsync(request);
                list.Add((response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.Contains("Set-Cookie"), response.Headers.Contains("Retry-After")));
            }
        }

        var expected = new (HttpStatusCode, string, bool, bool)[]
        {
            (HttpStatusCode.Unauthorized, string.Empty, false, false),
            (HttpStatusCode.Unauthorized, string.Empty, false, false),
            (HttpStatusCode.Unauthorized, string.Empty, false, false),
            (HttpStatusCode.TooManyRequests, string.Empty, false, true),
            (HttpStatusCode.TooManyRequests, string.Empty, false, true),
        };
        Assert.Equal(expected, observed[conta.Usuario]);
        Assert.Equal(expected, observed["naoexiste01"]); // no enumeration: the unknown name is locked exactly like the known one
        // The right password does not open a locked account.
        using var certa = Post("/login", new { organizacaoDoLogin = OrgA, usuario = conta.Usuario, senha = conta.Senha }, csrf: true);
        using var resposta = await client.SendAsync(certa);
        Assert.Equal(HttpStatusCode.TooManyRequests, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_malformed_login_is_a_bad_request_and_a_big_body_is_cut_off_with_413()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, () => Post("/login", new { usuario = "maria" }, csrf: true))); // no password
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, () => Post("/login", new { senha = "x" }, csrf: true))); // no identifier
        using var notJson = Post("/login", csrf: true);
        notJson.Content = new StringContent("usuario=a&senha=b", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await Status(client, () => notJson));
        // Declared length above the limit.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Status(client, () => Post("/login", new { usuario = "a", senha = new string('x', 20_000) }, csrf: true)));
        // Chunked (no Content-Length): the limit still holds.
        using var chunked = Post("/login", csrf: true);
        chunked.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"usuario\":\"a\",\"senha\":\"" + new string('x', 20_000) + "\"}")));
        chunked.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        chunked.Content.Headers.ContentLength = null;
        chunked.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Status(client, () => chunked));
    }

    [Fact]
    public async Task A_login_with_a_temporary_credential_yields_the_pending_state()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1], keepTemporary: true);

        var (sessao, _) = await LoginAsync(client, conta);

        Assert.True(sessao.DeveTrocarSenha);
    }

    [Fact]
    public async Task Login_audits_the_success_and_a_wrong_password_of_a_known_account_without_secrets()
    {
        using var logs = new CapturedLogs();
        await using var api = Api(logs: logs);
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);

        var (sessao, cookie) = await LoginAsync(client, conta);
        using var wrong = Post("/login", new { organizacaoDoLogin = OrgA, usuario = conta.Usuario, senha = "senha-errada-123456" }, csrf: true);
        await client.SendAsync(wrong);

        var actions = await QueryAsync(OrgA, "SELECT acao || ':' || resultado FROM auditoria_registros WHERE objeto_id = @s OR usuario_id = @c", r => r.GetString(0), ("s", conta.Id.ToString("D")), ("c", conta.Id));
        Assert.Contains("sessao:iniciar:sucesso", actions);
        Assert.Contains("autenticacao:falhar:negado", actions);
        await AssertNoSecretAnywhere(logs, sessao.AccessToken, cookie.Value, conta.Senha, "senha-errada-123456");
    }

    private async Task AssertNoSecretAnywhere(CapturedLogs logs, params string[] secrets)
    {
        var all = string.Join('\n', logs.Lines);
        var dump = await QueryAsync(OrgA, "SELECT row_to_json(a)::text FROM auditoria_registros a", r => r.GetString(0));
        var dumpSessions = await QueryAsync(OrgA, "SELECT row_to_json(s)::text FROM sessoes s", r => r.GetString(0));
        var dumpRefresh = await QueryAsync(OrgA, "SELECT row_to_json(s)::text FROM sessao_refresh s", r => r.GetString(0));
        var dumpOutbox = await QueryAsync(OrgA, "SELECT row_to_json(s)::text FROM outbox_eventos s", r => r.GetString(0));
        var stored = string.Join('\n', dump.Concat(dumpSessions).Concat(dumpRefresh).Concat(dumpOutbox));
        foreach (var secret in secrets.Where(s => s.Length > 6))
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        }
    }

    // ---- CSRF on the cookie routes ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cookie_routes_refuse_a_request_without_the_custom_header()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (_, cookie) = await LoginAsync(client, conta);

        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/refresh", cookie: cookie.Header)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/logout", cookie: cookie.Header)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/login", new { usuario = conta.Usuario, senha = conta.Senha })));
        // Nothing was consumed: the same cookie still refreshes.
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, cookie)).Status);
    }

    [Theory]
    [InlineData("https://evil.example", "cross-site")]
    [InlineData("https://evil.example", null)]
    [InlineData(null, "cross-site")]
    [InlineData(null, "same-site")]
    [InlineData("null", "cross-site")]
    public async Task A_cross_site_post_with_the_cookie_is_refused_and_does_not_consume_the_refresh(string? origin, string? fetchSite)
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, cookie) = await LoginAsync(client, conta);
        void Cross(HttpRequestMessage r)
        {
            if (origin is not null)
            {
                r.Headers.Add("Origin", origin);
            }

            if (fetchSite is not null)
            {
                r.Headers.Add("Sec-Fetch-Site", fetchSite);
            }
        }

        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/refresh", cookie: cookie.Header, csrf: true, tweak: Cross)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/logout", cookie: cookie.Header, csrf: true, tweak: Cross)));
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", sessao.AccessToken))); // the session survived the attempted logout
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, cookie)).Status);
    }

    [Theory]
    [InlineData("http://localhost", "same-origin")]
    [InlineData(null, "none")]
    [InlineData(null, null)] // non-browser client with the custom header
    [InlineData("https://localhost", "same-origin")] // behind a TLS proxy the scheme differs; the authority is what is compared
    public async Task A_same_origin_post_with_the_header_is_accepted(string? origin, string? fetchSite)
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (_, cookie) = await LoginAsync(client, conta);

        var status = await Status(client, () => Post("/refresh", cookie: cookie.Header, csrf: true, tweak: r =>
        {
            if (origin is not null)
            {
                r.Headers.Add("Origin", origin);
            }

            if (fetchSite is not null)
            {
                r.Headers.Add("Sec-Fetch-Site", fetchSite);
            }
        }));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_configured_front_origin_is_accepted_cross_site_and_others_are_not()
    {
        await using var api = Api(extra: new() { ["Flow4Tech:Sessoes:AllowedOrigins:0"] = "https://app.exemplo.com.br" });
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (_, cookie) = await LoginAsync(client, conta);

        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/refresh", cookie: cookie.Header, csrf: true, tweak: r => r.Headers.Add("Origin", "https://app.exemplo.com.br.evil.com"))));
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Post("/refresh", cookie: cookie.Header, csrf: true, tweak: r =>
        {
            r.Headers.Add("Origin", "https://app.exemplo.com.br");
            r.Headers.Add("Sec-Fetch-Site", "same-site");
        })));
    }

    // ---- refresh ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_rotates_the_cookie_and_returns_a_new_access_token_for_the_same_session()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, cookie) = await LoginAsync(client, conta);

        var (status, nova, novoCookie) = await RefreshAsync(client, cookie);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(sessao.SessaoId, nova!.SessaoId);
        Assert.NotEqual(sessao.AccessToken, nova.AccessToken);
        Assert.NotEqual(cookie.Value, novoCookie!.Value);
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", nova.AccessToken)));
    }

    [Fact]
    public async Task Replaying_a_used_refresh_cookie_kills_the_session_including_the_newest_token_and_is_audited()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (_, cookie) = await LoginAsync(client, conta);
        var (_, nova, novoCookie) = await RefreshAsync(client, cookie);

        var replay = await RefreshAsync(client, cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", nova!.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, novoCookie!)).Status);
        var rows = await QueryAsync(OrgA, "SELECT resultado FROM auditoria_registros WHERE acao = 'sessao:refresh-reutilizado' AND usuario_id = @c", r => r.GetString(0), ("c", conta.Id));
        Assert.Equal(["negado"], rows);
        Assert.Equal(1, (await QueryAsync(OrgA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'seguranca.refresh-reutilizado' AND payload::text LIKE '%' || @s || '%'", r => r.GetInt32(0), ("s", nova!.SessaoId.ToString("D"))))[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("__Host-f4t_refresh=lixo")]
    [InlineData("__Host-f4t_refresh=r1.0197a00000007000800000000000000a.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("outro=r1.x.y")]
    public async Task Refresh_without_a_valid_cookie_answers_401_and_clears_the_cookie(string? cookie)
    {
        await using var api = Api();
        using var client = api.CreateClient();
        using var request = Post("/refresh", cookie: cookie, csrf: true);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        var cleared = ReadCookie(response);
        Assert.NotNull(cleared);
        Assert.Equal(string.Empty, cleared.Value);
    }

    [Fact]
    public async Task The_access_token_expires_in_five_minutes_and_the_refresh_brings_a_new_one()
    {
        var clock = new TestClock();
        await using var api = Api(clock);
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, cookie) = await LoginAsync(client, conta);

        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", sessao.AccessToken)));
        var (status, nova, _) = await RefreshAsync(client, cookie);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", nova!.AccessToken)));
    }

    [Fact]
    public async Task The_session_ends_after_the_configured_inactivity_even_though_the_refresh_cookie_is_there()
    {
        var clock = new TestClock();
        await using var api = Api(clock, extra: new() { ["Flow4Tech:Sessoes:InatividadeHoras"] = "1" });
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (_, cookie) = await LoginAsync(client, conta);

        clock.UtcNow = clock.UtcNow.AddMinutes(61);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, cookie)).Status);
    }

    // ---- layered settings over HTTP (ticket 10, spec 5.7) --------------------------------------------------------------------------------------------

    private static HttpRequestMessage Put(string path, object body, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task The_settings_routes_need_organizacao_configurar_and_a_denied_change_is_audited()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, _) = await LoginAsync(client, conta);

        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/configuracoes", null)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/configuracoes", sessao.AccessToken)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/configuracoes/sessao.absoluta-dias/historico?camada=organizacao", sessao.AccessToken)));
        using var negadaRequest = Put("/configuracoes/sessao.absoluta-dias", new { camada = "organizacao", valor = "1" }, sessao.AccessToken);
        using var negada = await client.SendAsync(negadaRequest);
        Assert.Equal(HttpStatusCode.Forbidden, negada.StatusCode);
        Assert.Equal(string.Empty, await negada.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/configuracoes/sessao.absoluta-dias/restaurar", new { camada = "organizacao", versao = 1 }, sessao.AccessToken)));

        Assert.Equal(["negado", "negado"], await QueryAsync(OrgA, "SELECT resultado FROM auditoria_registros WHERE acao = 'acesso:negar' AND usuario_id = @c AND objeto_id = 'organizacao:configurar'", r => r.GetString(0), ("c", conta.Id)));
        Assert.Empty(await QueryAsync(OrgA, "SELECT 1 FROM configuracoes_versoes", r => r.GetInt32(0)));
    }

    [Fact]
    public async Task A_session_reads_its_clocks_from_the_organization_setting_and_a_change_does_not_reach_sessions_already_started()
    {
        var clock = new TestClock();
        await using var api = Api(clock);
        using var client = api.CreateClient();
        var antes = await NewAccountAsync(api, [UnitA1]);
        var depois = await NewAccountAsync(api, [UnitA1]);
        var (_, cookieAntes) = await LoginAsync(client, antes);
        using var dono = await LoginOwnerAsync(client);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Put("/configuracoes/sessao.absoluta-dias", new { camada = "organizacao", valor = "0.05", motivo = "turno curto" }, dono.Token))); // 72 minutes
            var (_, cookieDepois) = await LoginAsync(client, depois);

            clock.UtcNow = clock.UtcNow.AddMinutes(90);

            Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, cookieAntes)).Status); // started before the change: keeps the 14 days it was born with
            Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, cookieDepois)).Status); // started after: the organization's 72 minutes
            Assert.Equal(["configuracao:alterar|sucesso"], await QueryAsync(OrgA, "SELECT acao || '|' || resultado FROM auditoria_registros WHERE acao LIKE 'configuracao:%'", r => r.GetString(0)));
        }
        finally
        {
            clock.UtcNow = DateTimeOffset.UtcNow.AddHours(1);
            using var dono2 = await LoginOwnerAsync(client);
            Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Put("/configuracoes/sessao.absoluta-dias", new { camada = "organizacao", valor = (string?)null }, dono2.Token)));
        }
    }

    [Fact]
    public async Task The_settings_routes_validate_lock_keep_the_history_and_restore()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        await EnsureOwnerAsync(api);
        using var dono = await LoginOwnerAsync(client);
        var amanha = DateTimeOffset.UtcNow.AddDays(1);
        const string Virada = "/configuracoes/dia-operacional.virada";

        using var semInstanteRequest = Put(Virada, new { camada = "unidade", valor = "04:00" }, dono.Token);
        using var semInstante = await client.SendAsync(semInstanteRequest);
        Assert.Equal(HttpStatusCode.BadRequest, semInstante.StatusCode);
        Assert.Equal("vigencia-invalida", (await semInstante.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, () => Put(Virada, new { camada = "unidade", valor = "25:99", vigenteAPartirDe = amanha }, dono.Token)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, () => Put(Virada, new { camada = "sistema", valor = "04:00", vigenteAPartirDe = amanha }, dono.Token)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, () => Put("/configuracoes/nao.existe", new { camada = "organizacao", valor = "1" }, dono.Token)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, () => Put("/configuracoes/bloqueio.falhas-email", new { camada = "organizacao", valor = "9" }, dono.Token))); // system-only option

        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Put(Virada, new { camada = "organizacao", valor = "04:30", travado = true, vigenteAPartirDe = amanha, motivo = "padrao da rede" }, dono.Token)));
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Put(Virada, new { camada = "organizacao", valor = "06:00", vigenteAPartirDe = amanha.AddDays(1) }, dono.Token)));
        using var historicoRequest = Get($"{Virada}/historico?camada=organizacao", dono.Token);
        using var historico = await client.SendAsync(historicoRequest);
        var linhas = (await historico.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal([2, 1], linhas.Select(l => l.GetProperty("versao").GetInt32()));
        Assert.Equal(["06:00", "04:30"], linhas.Select(l => l.GetProperty("valor").GetString()));
        Assert.Equal([true, true], linhas.Select(l => l.GetProperty("agendada").GetBoolean())); // both start in the future, one after the other
        Assert.True(linhas[0].GetProperty("travado").GetBoolean()); // the lock stays when a change says nothing about it

        // The lock is scheduled for tomorrow, so today the unit may still change, and the change is audited with the person who made it.
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Put(Virada, new { camada = "unidade", valor = "03:00", vigenteAPartirDe = amanha.AddDays(-0.5) }, dono.Token)));
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, () => Put(Virada, new { camada = "unidade", valor = "03:30", vigenteAPartirDe = amanha.AddHours(2) }, dono.Token)));

        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Post($"{Virada}/restaurar", new { camada = "organizacao", versao = 1, vigenteAPartirDe = amanha.AddDays(3) }, dono.Token)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, () => Post($"{Virada}/restaurar", new { camada = "organizacao", versao = 77, vigenteAPartirDe = amanha }, dono.Token)));

        using var listaRequest = Get("/configuracoes", dono.Token);
        using var lista = await client.SendAsync(listaRequest);
        var item = (await lista.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(i => i.GetProperty("chave").GetString() == "dia-operacional.virada");
        Assert.Equal("05:00", item.GetProperty("padrao").GetString());
        Assert.Equal("05:00", item.GetProperty("valor").GetString()); // nothing in force yet: the organization's versions start tomorrow
        Assert.Equal(JsonValueKind.Null, item.GetProperty("valorDaUnidade").ValueKind); // the unit's version starts in 12 hours
        Assert.Equal(["sistema", "organizacao", "unidade"], item.GetProperty("camadas").EnumerateArray().Select(c => c.GetString()));
        Assert.True(item.GetProperty("exigeVigencia").GetBoolean());
    }

    // ---- logout ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Logout_revokes_the_session_clears_the_cookie_and_is_audited()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, cookie) = await LoginAsync(client, conta);

        using var request = Post("/logout", cookie: cookie.Header, csrf: true);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, ReadCookie(response)!.Value);
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", sessao.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, cookie)).Status);
        Assert.Equal(["sucesso"], await QueryAsync(OrgA, "SELECT resultado FROM auditoria_registros WHERE acao = 'sessao:encerrar' AND usuario_id = @c", r => r.GetString(0), ("c", conta.Id)));
    }

    [Fact]
    public async Task Logout_without_a_cookie_is_harmless()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Post("/logout", csrf: true)));
    }

    // ---- tokens: realm, algorithm, forgery ----------------------------------------------------------------------------------------------------------

    private static string Forged(string alg, string key, string audience, Guid conta, Guid sessao, int version = 1)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "flow4tech",
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", conta.ToString("D")), new Claim("org", OrgA.ToString("D")), new Claim("sid", sessao.ToString("D")), new Claim("ver", version.ToString(System.Globalization.CultureInfo.InvariantCulture))]),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(4),
        };
        if (alg != "none")
        {
            descriptor.SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), alg);
        }

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task Tokens_of_the_other_realm_with_none_or_with_another_algorithm_or_key_never_get_in_even_for_a_real_session()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, _) = await LoginAsync(client, conta);

        // NewAccountAsync changed the temporary password once, so the credential version of the account (and of this session) is 2.
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", Forged("HS256", Key, "app", conta.Id, sessao.SessaoId, version: 2)))); // control: the forging recipe is right when the key is
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", Forged("HS256", Key, "control", conta.Id, sessao.SessaoId, version: 2))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", Forged("none", Key, "app", conta.Id, sessao.SessaoId, version: 2))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", Forged("HS512", Key + Key, "app", conta.Id, sessao.SessaoId, version: 2))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", Forged("HS256", "outra-chave-outra-chave-outra-chave-123", "app", conta.Id, sessao.SessaoId, version: 2))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", Forged("HS256", Key, "app", conta.Id, sessao.SessaoId, version: 3)))); // version that does not match
    }

    [Fact]
    public async Task The_access_token_is_read_only_from_the_authorization_header_never_from_cookie_or_query()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, _) = await LoginAsync(client, conta);

        using var viaQuery = Get($"/unidades?access_token={sessao.AccessToken}", null);
        using var viaCookie = Get("/unidades", null);
        viaCookie.Headers.Add("Cookie", $"token={sessao.AccessToken}; Authorization={sessao.AccessToken}");
        using var wrongScheme = Get("/unidades", null);
        wrongScheme.Headers.TryAddWithoutValidation("Authorization", $"Basic {sessao.AccessToken}");

        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => viaQuery));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => viaCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => wrongScheme));
    }

    // ---- revocation triggers over HTTP ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deactivating_the_account_ends_the_session_on_the_next_request()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, _) = await LoginAsync(client, conta);
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", sessao.AccessToken)));
        var dono = await EnsureOwnerAsync(api);

        await api.Services.GetRequiredService<GestaoDeContas>().DesativarAsync(new FakeRequest(dono, Guid.NewGuid(), new OrganizationContext(OrgA, UnitA1)), conta.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", sessao.AccessToken)));
    }

    [Fact]
    public async Task Removing_the_link_of_the_active_unit_ends_the_session_on_the_next_request()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, _) = await LoginAsync(client, conta);

        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using (var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA1)))
        {
            await Exec(tx, "DELETE FROM usuario_unidades WHERE usuario_id = @c", ("c", conta.Id));
            await tx.CommitAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", sessao.AccessToken)));
    }

    // ---- must change the password ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pending_password_change_session_reaches_only_the_change_route_and_logout()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1], keepTemporary: true);
        var (sessao, cookie) = await LoginAsync(client, conta);
        Assert.True(sessao.DeveTrocarSenha);

        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/unidades", sessao.AccessToken)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/sessao/unidade-ativa", sessao.AccessToken)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/sessoes", sessao.AccessToken)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/sessao/unidade-ativa", new { unidadeDestinoId = UnitA1 }, sessao.AccessToken)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post($"/sessoes/{sessao.SessaoId}/revogar", token: sessao.AccessToken)));
        // The state survives a refresh.
        var (_, refreshed, _) = await RefreshAsync(client, cookie);
        Assert.True(refreshed!.DeveTrocarSenha);
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/unidades", refreshed.AccessToken)));
        // Logout is allowed in this state.
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Post("/logout", cookie: cookie.Header, csrf: true)));
    }

    [Fact]
    public async Task Changing_the_password_issues_a_new_normal_session_and_ends_every_other_one_of_the_account()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1], keepTemporary: true);
        var (pendente, _) = await LoginAsync(client, conta);
        var (outra, outraCookie) = await LoginAsync(client, conta); // another device, also pending

        using var request = Post("/sessao/trocar-senha", new { senhaAtual = conta.Senha, novaSenha = "outra-senha-comprida-2026" }, pendente.AccessToken);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var nova = (await response.Content.ReadFromJsonAsync<SessaoDto>())!;
        var novoCookie = ReadCookie(response)!;
        Assert.False(nova.DeveTrocarSenha);
        Assert.NotEqual(pendente.SessaoId, nova.SessaoId);
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", nova.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", pendente.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", outra.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, outraCookie)).Status);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, novoCookie)).Status);
        // The old password no longer logs in, the new one does.
        using var old = Post("/login", new { organizacaoDoLogin = OrgA, usuario = conta.Usuario, senha = conta.Senha }, csrf: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(old)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, conta, "outra-senha-comprida-2026")).Sessao is { } ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_wrong_current_password_or_a_weak_new_one_changes_nothing_and_says_which_in_a_code()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, _) = await LoginAsync(client, conta);

        using var wrongCurrent = Post("/sessao/trocar-senha", new { senhaAtual = "errada-errada-123", novaSenha = "outra-senha-comprida-2026" }, sessao.AccessToken);
        using var wrongResponse = await client.SendAsync(wrongCurrent);
        using var weak = Post("/sessao/trocar-senha", new { senhaAtual = conta.Senha, novaSenha = "curta" }, sessao.AccessToken);
        using var weakResponse = await client.SendAsync(weak);

        Assert.Equal(HttpStatusCode.BadRequest, wrongResponse.StatusCode);
        Assert.Contains("senha-atual-invalida", await wrongResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, weakResponse.StatusCode);
        Assert.Contains("senha-invalida", await weakResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", sessao.AccessToken))); // still the same session
    }

    [Fact]
    public async Task The_change_password_route_has_a_body_limit_and_needs_a_session()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, _) = await LoginAsync(client, conta);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Status(client, () => Post("/sessao/trocar-senha", new { senhaAtual = new string('a', 50_000), novaSenha = "x" }, sessao.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Post("/sessao/trocar-senha", new { senhaAtual = "a", novaSenha = "b" })));
    }

    // ---- no active unit ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_several_units_the_session_starts_without_one_and_only_the_selection_routes_work_until_one_is_chosen()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1, UnitA2]);
        var (sessao, cookie) = await LoginAsync(client, conta);
        Assert.Null(sessao.UnidadeAtivaId);
        Assert.True(sessao.RequerSelecaoDeUnidade);

        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/sessao/unidade-ativa", sessao.AccessToken))); // business route without unit
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/sessoes", sessao.AccessToken)));
        using var list = Get("/unidades", sessao.AccessToken);
        using var listed = await client.SendAsync(list);
        Assert.Equal([UnitA1, UnitA2], (await listed.Content.ReadFromJsonAsync<List<UnidadeDto>>())!.Select(u => u.Id).Order());

        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Post("/sessao/unidade-ativa", new { unidadeDestinoId = UnitA2 }, sessao.AccessToken)));

        using var after = Get("/sessao/unidade-ativa", sessao.AccessToken);
        Assert.Equal(UnitA2, (await (await client.SendAsync(after)).Content.ReadFromJsonAsync<UnidadeAtivaDto>())!.UnidadeId);
        // The choice lives in the session row: a refresh (new token) and a new request keep it.
        var (_, refreshed, _) = await RefreshAsync(client, cookie);
        Assert.False(refreshed!.RequerSelecaoDeUnidade);
        Assert.Equal(UnitA2, refreshed.UnidadeAtivaId);
        Assert.Equal(["sessao:trocar-unidade"], await QueryAsync(OrgA, "SELECT acao FROM auditoria_registros WHERE acao = 'sessao:trocar-unidade' AND usuario_id = @c", r => r.GetString(0), ("c", conta.Id)));
    }

    [Fact]
    public async Task An_account_with_no_unit_gets_an_empty_list_and_cannot_choose_one()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, []);
        var (sessao, _) = await LoginAsync(client, conta);

        using var list = Get("/unidades", sessao.AccessToken);
        using var listed = await client.SendAsync(list);

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Empty((await listed.Content.ReadFromJsonAsync<List<UnidadeDto>>())!);
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/sessao/unidade-ativa", new { unidadeDestinoId = UnitA1 }, sessao.AccessToken)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/sessao/unidade-ativa", sessao.AccessToken)));
    }

    // ---- listing and revocation ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_user_lists_their_own_sessions_and_revokes_another_one_of_theirs()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (a, _) = await LoginAsync(client, conta);
        var (b, _) = await LoginAsync(client, conta);
        var outra = await NewAccountAsync(api, [UnitA1]);
        await LoginAsync(client, outra);

        using var request = Get("/sessoes", a.AccessToken);
        using var response = await client.SendAsync(request);
        var list = (await response.Content.ReadFromJsonAsync<List<SessaoItemDto>>())!;

        Assert.Equal([a.SessaoId, b.SessaoId], list.Select(x => x.Id).Order());
        Assert.Equal(a.SessaoId, Assert.Single(list, x => x.Atual).Id);
        Assert.All(list, x => Assert.Equal(conta.Id, x.ContaId));
        using var rawRequest = Get("/sessoes", a.AccessToken);
        using var rawResponse = await client.SendAsync(rawRequest);
        var raw = await rawResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", raw, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Post($"/sessoes/{b.SessaoId}/revogar", token: a.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", b.AccessToken)));
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", a.AccessToken)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, () => Post($"/sessoes/{Guid.NewGuid()}/revogar", token: a.AccessToken)));
    }

    [Fact]
    public async Task Someone_elses_sessions_are_for_owners_only_and_a_non_owner_sees_not_found()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var maria = await NewAccountAsync(api, [UnitA1]);
        var joao = await NewAccountAsync(api, [UnitA1]);
        var (mariaSessao, _) = await LoginAsync(client, maria);
        var (joaoSessao, _) = await LoginAsync(client, joao);
        using var dono = await LoginOwnerAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Get("/sessoes/todas", joaoSessao.AccessToken)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, () => Post($"/sessoes/{mariaSessao.SessaoId}/revogar", token: joaoSessao.AccessToken)));
        Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/unidades", mariaSessao.AccessToken)));

        using var todas = Get("/sessoes/todas", dono.Token);
        using var listed = await client.SendAsync(todas);
        Assert.Contains(mariaSessao.SessaoId, (await listed.Content.ReadFromJsonAsync<List<SessaoItemDto>>())!.Select(x => x.Id));
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Post($"/sessoes/{mariaSessao.SessaoId}/revogar", token: dono.Token)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", mariaSessao.AccessToken)));
        Assert.Equal(["sucesso"], await QueryAsync(OrgA, "SELECT resultado FROM auditoria_registros WHERE acao = 'sessao:revogar' AND objeto_id = @s", r => r.GetString(0), ("s", mariaSessao.SessaoId.ToString("D"))));
    }

    // ---- ticket 09: invitations, recovery, credential reset, Google ---------------------------------------------------------------------------

    private sealed class ProvedorStub : IProvedorOidc
    {
        public IdentidadeOidc? Identidade { get; set; }

        public bool Configurado => true;

        public Uri UrlDeAutorizacao(string state, string nonce, string codeChallenge) => new($"https://provedor.invalido/auth?state={Uri.EscapeDataString(state)}");

        public Task<IdentidadeOidc?> TrocarCodigoAsync(string code, string codeVerifier, string nonceEsperado, CancellationToken cancellationToken) => Task.FromResult(code == "bom" ? Identidade : null);
    }

    private static string LastLinkToken(ApiFactory api, string subject) =>
        api.Services.GetRequiredService<Flow4Tech.Identidade.Infrastructure.EnviadorDeEmailDeDesenvolvimento>().Enviadas
            .Last(m => m.Assunto.Contains(subject, StringComparison.Ordinal)).Texto.Split("#token=")[1].Split(['\n', ' '])[0];

    /// <summary>An account with e-mail created through the real invitation routes (owner invites, person accepts); returns its address and password.</summary>
    private async Task<(string Email, string Senha)> InvitedAccountAsync(ApiFactory api, HttpClient client)
    {
        await EnsureOwnerAsync(api);
        using var dono = await LoginOwnerAsync(client);
        var email = $"pessoa{Guid.NewGuid():N}"[..16] + "@a.com";
        using var convite = Post("/convites", new { email, nome = "Pessoa Convidada" }, token: dono.Token);
        using var criado = await client.SendAsync(convite);
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        const string senha = "senha-da-pessoa-convidada-2026";
        using var aceitar = Post("/convites/aceitar", new { token = LastLinkToken(api, "convidado"), senha }, csrf: true);
        using var aceito = await client.SendAsync(aceitar);
        Assert.Equal(HttpStatusCode.NoContent, aceito.StatusCode);
        return (email, senha);
    }

    private static async Task<SessaoDto> LoginByEmailAsync(HttpClient client, string email, string senha)
    {
        using var request = Post("/login", new { email, senha }, csrf: true);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessaoDto>())!;
    }

    [Fact]
    public async Task Invitation_over_http_creates_an_account_with_the_chosen_password_once_and_only_for_whoever_may_invite()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        await EnsureOwnerAsync(api);
        var operacional = await NewAccountAsync(api, [UnitA1]);
        var (_, sessaoOperacional) = (0, (await LoginAsync(client, operacional)).Sessao);

        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Post("/convites", new { email = "x@a.com", nome = "X" })));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/convites", new { email = "x@a.com", nome = "X" }, token: sessaoOperacional.AccessToken)));

        var (email, senha) = await InvitedAccountAsync(api, client);
        var token = LastLinkToken(api, "convidado");
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, () => Post("/convites/aceitar", new { token, senha }, csrf: true))); // single use
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/convites/aceitar", new { token, senha }))); // CSRF guard
        Assert.Equal(email, (await QueryAsync(OrgA, "SELECT email FROM contas WHERE email = @e", r => r.GetString(0), ("e", email))).Single());
        _ = await LoginByEmailAsync(client, email, senha);
    }

    [Fact]
    public async Task Recovery_over_http_answers_the_same_for_known_and_unknown_addresses_and_ends_every_session()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var (email, senha) = await InvitedAccountAsync(api, client);
        var sessao = await LoginByEmailAsync(client, email, senha);
        const string nova = "uma-nova-senha-bem-longa-2027";

        var respostas = new List<(HttpStatusCode, string)>();
        foreach (var endereco in new[] { email, "ninguem@a.com", "isto-nao-e-email" })
        {
            using var request = Post("/recuperacao/solicitar", new { email = endereco }, csrf: true);
            using var response = await client.SendAsync(request);
            respostas.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(respostas, r => Assert.Equal((HttpStatusCode.Accepted, string.Empty), r));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/recuperacao/solicitar", new { email }))); // CSRF guard

        var token = LastLinkToken(api, "Recupera");
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, () => Post("/recuperacao/redefinir", new { token, novaSenha = nova }, csrf: true)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, () => Post("/recuperacao/redefinir", new { token, novaSenha = nova }, csrf: true))); // single use
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Get("/unidades", sessao.AccessToken))); // the old session died
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Post("/login", new { email, senha }, csrf: true)));
        _ = await LoginByEmailAsync(client, email, nova);
        Assert.True((await QueryAsync(OrgA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'seguranca.acesso-recuperado'", r => r.GetInt64(0))).Single() >= 1);
    }

    [Fact]
    public async Task Resetting_the_access_of_an_operational_account_over_http_returns_the_credential_once_and_only_for_who_may()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        await EnsureOwnerAsync(api);
        var (emailDaPessoa, _) = await InvitedAccountAsync(api, client);
        var pessoaComEmail = (await QueryAsync(OrgA, "SELECT id FROM contas WHERE email = @e", r => r.GetGuid(0), ("e", emailDaPessoa))).Single();
        var garcom = await NewAccountAsync(api, [UnitA1]);
        var outro = await NewAccountAsync(api, [UnitA1]);
        var sessaoDoOutro = (await LoginAsync(client, outro)).Sessao;
        using var login = await LoginOwnerAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post($"/contas/{garcom.Id}/redefinir-acesso", token: sessaoDoOutro.AccessToken)));
        using var request = Post($"/contas/{garcom.Id}/redefinir-acesso", token: login.Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var credencial = (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("credencial").GetString()!;

        var entrou = await LoginAsync(client, garcom, credencial);
        Assert.True(entrou.Sessao.DeveTrocarSenha);
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, () => Post("/login", new { organizacaoDoLogin = OrgA, usuario = garcom.Usuario, senha = garcom.Senha }, csrf: true))); // the old password is gone
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, () => Post($"/contas/{pessoaComEmail}/redefinir-acesso", token: login.Token))); // e-mail accounts recover by e-mail
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, () => Post($"/contas/{Guid.NewGuid()}/redefinir-acesso", token: login.Token)));
    }

    [Fact]
    public async Task Google_is_off_without_configuration_and_the_callback_always_ends_in_the_neutral_redirect()
    {
        await using var api = Api();
        using var client = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var inicio = await client.GetAsync(new Uri("/login/google", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, inicio.StatusCode);

        using var retorno = await client.GetAsync(new Uri("/login/google/retorno?code=qualquer&state=qualquer", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, retorno.StatusCode);
        Assert.EndsWith("/entrar?google=erro", retorno.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Null(ReadCookie(retorno)); // no session
    }

    [Fact]
    public async Task Google_over_http_links_with_the_password_signs_in_through_the_refresh_cookie_and_refuses_operational_accounts()
    {
        var provedor = new ProvedorStub();
        await using var api = Api(services: s => s.Replace(ServiceDescriptor.Singleton<IProvedorOidc>(provedor)));
        using var client = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (email, senha) = await InvitedAccountAsync(api, client);
        provedor.Identidade = new IdentidadeOidc("sub-da-pessoa", email, true);
        var sessao = await LoginByEmailAsync(client, email, senha);

        // not linked: neutral, no session
        var (selo0, state0) = await StartGoogleAsync(client);
        using var semVinculo = await Callback(client, selo0, state0);
        Assert.EndsWith("/entrar?google=erro", semVinculo.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Null(ReadCookie(semVinculo));

        // link: wrong password refused, right one starts the round trip
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, () => Post("/conta/google/vinculo", new { senhaAtual = "senha-errada-123456" }, token: sessao.AccessToken)));
        using var vinculo = Post("/conta/google/vinculo", new { senhaAtual = senha }, token: sessao.AccessToken);
        using var iniciado = await client.SendAsync(vinculo);
        Assert.Equal(HttpStatusCode.OK, iniciado.StatusCode);
        var selo = OidcCookieValue(iniciado)!;
        var state = System.Web.HttpUtility.ParseQueryString(new Uri((await iniciado.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("url").GetString()!).Query)["state"]!;
        using var vinculado = await Callback(client, selo, state);
        Assert.EndsWith("/conta?google=vinculado", vinculado.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Null(ReadCookie(vinculado)); // linking never starts a session

        // now it signs in: the refresh cookie arrives and /refresh turns it into an access token
        var (selo1, state1) = await StartGoogleAsync(client);
        using var entrou = await Callback(client, selo1, state1);
        Assert.EndsWith("/entrar?google=ok", entrou.Headers.Location!.ToString(), StringComparison.Ordinal);
        var cookie = ReadCookie(entrou);
        Assert.NotNull(cookie);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, cookie!)).Status);

        // a state that does not match the cookie is refused, and an operational account may not even start
        var (selo2, _) = await StartGoogleAsync(client);
        using var errado = await Callback(client, selo2, "state-errado");
        Assert.EndsWith("/entrar?google=erro", errado.Headers.Location!.ToString(), StringComparison.Ordinal);
        var operacional = await NewAccountAsync(api, [UnitA1]);
        var sessaoOperacional = (await LoginAsync(client, operacional)).Sessao;
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, () => Post("/conta/google/vinculo", new { senhaAtual = operacional.Senha }, token: sessaoOperacional.AccessToken)));
    }

    private static string? OidcCookieValue(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("__Host-f4t_oidc=", StringComparison.Ordinal))?.Split(';')[0]["__Host-f4t_oidc=".Length..]
            : null;

    private static async Task<(string Selo, string State)> StartGoogleAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/login/google", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("__Host-f4t_oidc=", StringComparison.Ordinal));
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        return (OidcCookieValue(response)!, System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["state"]!);
    }

    private static async Task<HttpResponseMessage> Callback(HttpClient client, string selo, string state)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/login/google/retorno?code=bom&state={Uri.EscapeDataString(state)}", UriKind.Relative));
        request.Headers.Add("Cookie", $"__Host-f4t_oidc={selo}");
        return await client.SendAsync(request);
    }

    private sealed class OwnerLogin(string token) : IDisposable
    {
        public string Token { get; } = token;

        public void Dispose()
        {
        }
    }

    private static async Task<OwnerLogin> LoginOwnerAsync(HttpClient client)
    {
        using var request = Post("/login", new { email = "dono@a.com", senha = DonoSenha }, csrf: true);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new OwnerLogin((await response.Content.ReadFromJsonAsync<SessaoDto>())!.AccessToken);
    }

    [Fact]
    public async Task Revoking_a_session_of_another_organization_is_not_found_even_for_an_owner()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        await EnsureOwnerAsync(api);
        var donoB = await api.Services.GetRequiredService<GestaoDeContas>().CriarDonoAsync(OrgB, "dono@b.com", "Dono B", DonoSenha);
        Assert.Equal(StatusCriacao.Criada, donoB.Status);
        using var requestB = Post("/login", new { email = "dono@b.com", senha = DonoSenha }, csrf: true);
        var sessaoB = (await (await client.SendAsync(requestB)).Content.ReadFromJsonAsync<SessaoDto>())!;
        using var dono = await LoginOwnerAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, await Status(client, () => Post($"/sessoes/{sessaoB.SessaoId}/revogar", token: dono.Token)));
        using var todas = Get("/sessoes/todas", dono.Token);
        using var listed = await client.SendAsync(todas);
        Assert.DoesNotContain(sessaoB.SessaoId, (await listed.Content.ReadFromJsonAsync<List<SessaoItemDto>>())!.Select(x => x.Id));
    }

    // ---- no secret in logs --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tokens_and_cookies_never_reach_the_technical_log_the_audit_the_outbox_or_the_session_tables()
    {
        using var logs = new CapturedLogs();
        await using var api = Api(logs: logs);
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, cookie) = await LoginAsync(client, conta);
        var (_, nova, novoCookie) = await RefreshAsync(client, cookie);
        await RefreshAsync(client, cookie); // reuse: audit + outbox
        using var request = Get("/unidades?token=" + sessao.AccessToken, sessao.AccessToken);
        request.Headers.Add("Cookie", cookie.Header);
        await client.SendAsync(request);

        await AssertNoSecretAnywhere(logs, sessao.AccessToken, nova!.AccessToken, cookie.Value, novoCookie!.Value, conta.Senha);
        Assert.Contains(logs.Lines, l => l.Contains("http.requisicao", StringComparison.Ordinal));
    }

    // ---- latency ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_authenticated_request_path_latency_is_measured()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var conta = await NewAccountAsync(api, [UnitA1]);
        var (sessao, _) = await LoginAsync(client, conta);
        for (var i = 0; i < 5; i++)
        {
            await Status(client, () => Get("/sessao/unidade-ativa", sessao.AccessToken));
        }

        var tempos = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.Equal(HttpStatusCode.OK, await Status(client, () => Get("/sessao/unidade-ativa", sessao.AccessToken)));
            tempos.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        tempos.Sort();
        Console.WriteLine($"[latencia-requisicao-autenticada] n=100 p50={tempos[50]:F2} ms p95={tempos[95]:F2} ms (token + sessao + vinculo, em memoria + PostgreSQL local)");
        Assert.True(tempos[95] < 250);
    }

    // ---- infrastructure -----------------------------------------------------------------------------------------------------------------------------------

    private sealed class FakeRequest(Guid user, Guid session, OrganizationContext organization) : IRequestContext
    {
        public Guid UserId => user;

        public Guid SessionId => session;

        public OrganizationContext Organization => organization;
    }

    private sealed class CapturedLogs : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                var parts = new List<string> { formatter(state, exception) };
                void Add(object? item)
                {
                    if (item is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        parts.AddRange(pairs.Select(kv => $"{kv.Key}={kv.Value}"));
                    }
                }

                Add(state);
                owner._scopes.ForEachScope((scope, _) => Add(scope), 0);
                if (exception is not null)
                {
                    parts.Add(exception.ToString());
                }

                lock (owner.Lines)
                {
                    owner.Lines.Add(string.Join(' ', parts));
                }
            }
        }
    }

    private sealed class ApiFactory(string connectionString, TestClock? clock, CapturedLogs? logs, Dictionary<string, string?>? extra, Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging =>
            {
                if (logs is not null)
                {
                    logging.AddProvider(logs);
                }
            });
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Flow4Tech:Tokens:AppKey"] = Key,
                    ["Flow4Tech:Identidade:TempoMinimoDeLoginMs"] = "0",
                };
                foreach (var (k, v) in extra ?? [])
                {
                    values[k] = v;
                }

                config.AddInMemoryCollection(values);
            });
            if (clock is not null)
            {
                builder.ConfigureTestServices(replaced => replaced.Replace(ServiceDescriptor.Singleton<IClock>(clock)));
            }

            if (services is not null)
            {
                builder.ConfigureTestServices(services);
            }
        }
    }
}

/// <summary>One migrated PostgreSQL for the class (every test creates its own accounts, so they do not depend on each other).</summary>
public sealed class SessoesEndpointsDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private string _appConnection = "";

    public string AppConnection => _appConnection;

    public Guid? Owner { get; set; }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var owner = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        var app = new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Username = "flow4tech_app", Password = "app-secret-for-test" };
        _appConnection = app.ConnectionString;
        using var output = new StringWriter();
        var env = new Dictionary<string, string?>
        {
            ["MIGRATION_DATABASE_URL"] = $"postgres://{owner.Username}:{Uri.EscapeDataString(owner.Password!)}@{owner.Host}:{owner.Port}/{owner.Database}",
            ["APP_DB_PASSWORD"] = app.Password,
            ["ConnectionStrings__Default"] = app.ConnectionString,
            ["MIGRATIONS_DIR"] = SessoesEndpointsTests.MigrationsDirectory(),
        };
        Assert.True(await MigratorHost.RunAsync(name => env.GetValueOrDefault(name), output, CancellationToken.None) == 0, output.ToString());

        await using var ownerSource = NpgsqlDataSource.Create(owner.ConnectionString);
        var tenant = new TenantDatabase(ownerSource);
        foreach (var (org, name, units) in new[] { (SessoesEndpointsTests.OrgA, "Bar A", new[] { (SessoesEndpointsTests.UnitA1, "A-Centro"), (SessoesEndpointsTests.UnitA2, "A-Praia") }), (SessoesEndpointsTests.OrgB, "Bar B", new[] { (SessoesEndpointsTests.UnitB1, "B-Centro") }) })
        {
            await using var tx = await tenant.BeginAsync(new OrganizationContext(org, units[0].Item1));
            await SessoesEndpointsTests.Exec(tx, "INSERT INTO organizacoes (id, nome) VALUES (@o, @n)", ("o", org), ("n", name));
            foreach (var (id, unitName) in units)
            {
                await SessoesEndpointsTests.Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@i, @o, @n)", ("i", id), ("o", org), ("n", unitName));
            }

            await tx.CommitAsync();
        }
    }

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();
}
