using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Api;
using System.Net.Http.Headers;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Organizacao.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

/// <summary>
/// Ticket 01 (spec 5.1): organization and unit come from the session only. Real PostgreSQL, application credential (RLS on). Since ticket 04 the sessions
/// are REAL: a row in <c>sessoes</c> and a signed access token issued by the same service the login uses (no forgeable test header exists any more).
/// </summary>
public sealed class RequestContextEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";

    private static readonly Guid OrgA = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("0197b000-0000-7000-8000-00000000000b");
    private static readonly Guid UnitA1 = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly Guid UnitA2 = Guid.Parse("0197a000-0000-7000-8000-0000000000a2");
    private static readonly Guid UnitA3 = Guid.Parse("0197a000-0000-7000-8000-0000000000a3");
    private static readonly Guid UnitB1 = Guid.Parse("0197b000-0000-7000-8000-0000000000b1");
    private static readonly Guid UserMulti = Guid.Parse("0197a000-0000-7000-8000-0000000000c1");
    private static readonly Guid UserSolo = Guid.Parse("0197a000-0000-7000-8000-0000000000c2");
    private static readonly Guid UserB = Guid.Parse("0197b000-0000-7000-8000-0000000000c3");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private string _appConnection = "";

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
            ["MIGRATIONS_DIR"] = MigrationsDirectory(),
        };
        Assert.True(await MigratorHost.RunAsync(name => env.GetValueOrDefault(name), output, CancellationToken.None) == 0, output.ToString());

        // FORCE RLS binds the owner too: seed through tenant transactions.
        await using var ownerSource = NpgsqlDataSource.Create(owner.ConnectionString);
        var tenant = new TenantDatabase(ownerSource);
        await Seed(tenant, OrgA, "Bar A", [(UnitA1, "A-Centro"), (UnitA2, "A-Praia"), (UnitA3, "A-Serra")], [(UserMulti, UnitA1), (UserMulti, UnitA2), (UserSolo, UnitA1)]);
        await Seed(tenant, OrgB, "Bar B", [(UnitB1, "B-Centro")], [(UserB, UnitB1)]);
    }

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static async Task Seed(TenantDatabase tenant, Guid org, string nome, (Guid Id, string Nome)[] units, (Guid User, Guid Unit)[] links)
    {
        await using var tx = await tenant.BeginAsync(new OrganizationContext(org, units[0].Id));
        await using (var command = new NpgsqlCommand("INSERT INTO organizacoes (id, nome) VALUES (@o, @n)", tx.Connection, tx.Transaction))
        {
            command.Parameters.AddWithValue("o", org);
            command.Parameters.AddWithValue("n", nome);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var (id, name) in units)
        {
            await using var command = new NpgsqlCommand("INSERT INTO unidades (id, organizacao_id, nome) VALUES (@i, @o, @n)", tx.Connection, tx.Transaction);
            command.Parameters.AddWithValue("i", id);
            command.Parameters.AddWithValue("o", org);
            command.Parameters.AddWithValue("n", name);
            await command.ExecuteNonQueryAsync();
        }

        // usuario_unidades.usuario_id points at a real account since ticket 03: one minimal account per distinct user.
        foreach (var user in links.Select(l => l.User).Distinct())
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', 'Teste', 'u' || replace(@i::text, '-', ''), now())",
                tx.Connection,
                tx.Transaction);
            command.Parameters.AddWithValue("i", user);
            command.Parameters.AddWithValue("o", org);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var (user, unit) in links)
        {
            await using var command = new NpgsqlCommand("INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", tx.Connection, tx.Transaction);
            command.Parameters.AddWithValue("o", org);
            command.Parameters.AddWithValue("u", user);
            command.Parameters.AddWithValue("n", unit);
            await command.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static string MigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "modules.dependencies.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("backend root not found"), "migrations");
    }

    private ApiFactory Api(string environment = "Testing", string? key = Key, CapturedLogs? logs = null) => new(_appConnection, environment, key, logs);

    private static HttpRequestMessage Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        WithToken(request, token);
        return request;
    }

    private static void WithToken(HttpRequestMessage request, string? token)
    {
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string? token)
    {
        using var request = Get(path, token);
        return await client.SendAsync(request);
    }

    /// <summary>A real session for the user (row in <c>sessoes</c>, signed token) whose active unit is <paramref name="unit"/> (null: none).</summary>
    private static async Task<(string Token, Guid SessionId)> IssueAsync(ApiFactory api, Guid user, Guid org, Guid? unit)
    {
        var sessoes = api.Services.GetRequiredService<Sessoes>();
        var emitida = await sessoes.IniciarAsync(user, org, 1, false, new ClienteDaSessao("127.0.0.1", "testes"));
        if (emitida.UnidadeAtivaId != unit)
        {
            var uow = api.Services.GetRequiredService<IUnitOfWork>();
            await uow.ExecuteAsync(OrganizationContext.WithoutUnit(org), ct => unit is null ? SetNoUnit(org, emitida.SessaoId, ct) : sessoes.DefinirUnidadeAtivaAsync(org, emitida.SessaoId, unit.Value, ct));
        }

        return (emitida.AccessToken, emitida.SessaoId);
    }

    private static async Task SetNoUnit(Guid org, Guid session, CancellationToken ct)
    {
        var tx = TenantUnitOfWork.Require(org);
        await using var command = new NpgsqlCommand("UPDATE sessoes SET unidade_ativa_id = NULL WHERE id = @i", tx.Connection, tx.Transaction);
        command.Parameters.AddWithValue("i", session);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> TokenAsync(ApiFactory api, Guid user, Guid org, Guid? unit) => (await IssueAsync(api, user, org, unit)).Token;

    private sealed record UnidadeDto(Guid Id, string Nome);

    private sealed record UnidadeAtivaDto(Guid UnidadeId);

    // ---- 401 / 403 ----

    [Fact]
    public async Task Authenticated_route_without_a_session_answers_401()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/unidades", null)).StatusCode);
    }

    [Fact]
    public async Task A_forged_or_tampered_token_answers_401()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var good = await TokenAsync(api, UserMulti, OrgA, UnitA1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/unidades", "lixo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/unidades", good[..^3] + "AAA")).StatusCode);
        // The old signed test header does not exist any more: it is just an unknown header.
        using var legacy = Get("/unidades", null);
        legacy.Headers.Add("X-Test-Session", good);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(legacy)).StatusCode);
    }

    [Fact]
    public async Task A_session_without_an_active_unit_cannot_reach_business_routes_and_answers_403()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "/sessao/unidade-ativa", await TokenAsync(api, UserMulti, OrgA, null))).StatusCode);
    }

    [Fact]
    public async Task A_session_whose_active_unit_has_no_link_answers_403()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        // UserSolo is linked to A1 only; A2 exists in the same organization but there is no link (a session row can point at it: the link is checked on every request).
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "/unidades", await TokenAsync(api, UserSolo, OrgA, UnitA2))).StatusCode);
        // A unit of another organization can no longer even be stored in a session: the composite foreign key refuses it (SessoesIntegrationTests).
    }

    // ---- context feeds the unit of work; the user only sees linked units ----

    [Fact]
    public async Task A_linked_user_sees_only_the_units_they_have_a_link_to_under_RLS()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        var response = await Send(client, "/unidades", await TokenAsync(api, UserMulti, OrgA, UnitA1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var units = await response.Content.ReadFromJsonAsync<List<UnidadeDto>>();
        Assert.Equal([UnitA1, UnitA2], units!.Select(u => u.Id).Order());
        Assert.DoesNotContain(units!, u => u.Id == UnitA3 || u.Id == UnitB1);

        var solo = await Send(client, "/unidades", await TokenAsync(api, UserSolo, OrgA, UnitA1));
        Assert.Equal([UnitA1], (await solo.Content.ReadFromJsonAsync<List<UnidadeDto>>())!.Select(u => u.Id));

        var b = await Send(client, "/unidades", await TokenAsync(api, UserB, OrgB, UnitB1));
        Assert.Equal([UnitB1], (await b.Content.ReadFromJsonAsync<List<UnidadeDto>>())!.Select(u => u.Id));
    }

    [Fact]
    public async Task Organization_and_unit_sent_by_the_client_in_header_query_or_cookie_are_ignored()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        using var request = Get($"/unidades?organizacaoId={OrgB}&unidadeId={UnitB1}&organizationId={OrgB}", await TokenAsync(api, UserMulti, OrgA, UnitA1));
        request.Headers.Add("X-Organizacao-Id", OrgB.ToString());
        request.Headers.Add("X-Unidade-Id", UnitB1.ToString());
        request.Headers.Add("Cookie", $"organizacaoId={OrgB}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var units = await response.Content.ReadFromJsonAsync<List<UnidadeDto>>();
        Assert.Equal([UnitA1, UnitA2], units!.Select(u => u.Id).Order());
    }

    // ---- explicit unit switch ----

    [Fact]
    public async Task Switching_unit_is_an_explicit_operation_and_takes_effect_on_the_next_request()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var token = await TokenAsync(api, UserMulti, OrgA, UnitA1);

        using var before = Get("/sessao/unidade-ativa", token);
        Assert.Equal(UnitA1, (await (await client.SendAsync(before)).Content.ReadFromJsonAsync<UnidadeAtivaDto>())!.UnidadeId);

        using var switchRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("/sessao/unidade-ativa", UriKind.Relative))
        {
            Content = JsonContent.Create(new { unidadeDestinoId = UnitA2 }),
        };
        WithToken(switchRequest, token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(switchRequest)).StatusCode);

        using var after = Get("/sessao/unidade-ativa", token);
        Assert.Equal(UnitA2, (await (await client.SendAsync(after)).Content.ReadFromJsonAsync<UnidadeAtivaDto>())!.UnidadeId);
    }

    [Theory]
    [InlineData("A3")] // same organization, no link
    [InlineData("B1")] // another organization
    [InlineData("none")] // does not exist
    public async Task Switching_to_a_unit_without_a_link_answers_403_and_keeps_the_active_unit(string target)
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var token = await TokenAsync(api, UserMulti, OrgA, UnitA1);
        var destination = target switch { "A3" => UnitA3, "B1" => UnitB1, _ => Guid.NewGuid() };

        using var switchRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("/sessao/unidade-ativa", UriKind.Relative))
        {
            Content = JsonContent.Create(new { unidadeDestinoId = destination }),
        };
        WithToken(switchRequest, token);
        var response = await client.SendAsync(switchRequest);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync()); // does not say why
        using var after = Get("/sessao/unidade-ativa", token);
        Assert.Equal(UnitA1, (await (await client.SendAsync(after)).Content.ReadFromJsonAsync<UnidadeAtivaDto>())!.UnidadeId);
    }

    [Fact]
    public async Task Switching_unit_without_a_session_answers_401()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(new Uri("/sessao/unidade-ativa", UriKind.Relative), new { unidadeDestinoId = UnitA2 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- there is no test door ----

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task The_old_test_session_header_is_ignored_in_every_environment(string environment)
    {
        await using var api = Api(environment);
        using var client = api.CreateClient();
        using var request = Get("/unidades", null);
        request.Headers.Add("X-Test-Session", "eyJzaWQiOiIwMDAwMDAwMC0wMDAwLTAwMDAtMDAwMC0wMDAwMDAwMDAwMDAifQ.AAAA");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Outside_development_and_testing_the_host_does_not_boot_without_the_signing_key(string environment)
    {
        await using var api = Api(environment, key: null);

        var ex = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains("Flow4Tech:Tokens:AppKey", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_testing_without_a_key_the_host_boots_with_an_ephemeral_key_so_a_token_of_another_process_is_refused()
    {
        await using var other = Api();
        var foreign = await TokenAsync(other, UserMulti, OrgA, UnitA1);
        await using var api = Api("Testing", key: null);
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/unidades", foreign)).StatusCode);
    }

    [Fact]
    public async Task Public_routes_do_not_need_a_session()
    {
        await using var api = Api("Production");
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/version", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
    }

    // ---- audit of the unit switch (ticket 02, spec 5.1.4 and 5.6) ----

    private sealed record AuditRowDto(string Acao, string Resultado, Guid? Usuario, Guid? Sessao, Guid? Unidade, string? ObjetoId, string? Antes, string? Depois, string Correlacao, long Sequencia);

    private async Task<List<AuditRowDto>> AuditRows(Guid org, Guid unit)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
        await using var command = new NpgsqlCommand(
            "SELECT acao, resultado, usuario_id, sessao_id, unidade_id, objeto_id, antes->>'unidadeId', depois->>'unidadeId', correlation_id, sequencia FROM auditoria_registros WHERE acao <> 'sessao:iniciar' ORDER BY sequencia",
            tx.Connection,
            tx.Transaction);
        var rows = new List<AuditRowDto>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRowDto(
                reader.GetString(0), reader.GetString(1),
                await reader.IsDBNullAsync(2) ? null : reader.GetGuid(2), await reader.IsDBNullAsync(3) ? null : reader.GetGuid(3), await reader.IsDBNullAsync(4) ? null : reader.GetGuid(4),
                await reader.IsDBNullAsync(5) ? null : reader.GetString(5), await reader.IsDBNullAsync(6) ? null : reader.GetString(6), await reader.IsDBNullAsync(7) ? null : reader.GetString(7),
                reader.GetString(8), reader.GetInt64(9)));
        }

        return rows;
    }

    private static HttpRequestMessage SwitchRequest(Guid destination, string token, string? traceparent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/sessao/unidade-ativa", UriKind.Relative))
        {
            Content = JsonContent.Create(new { unidadeDestinoId = destination }),
        };
        WithToken(request, token);
        if (traceparent is not null)
        {
            request.Headers.Add("traceparent", traceparent);
        }

        return request;
    }

    [Fact]
    public async Task Switching_unit_writes_an_audit_record_with_actor_before_after_and_the_correlation_id_of_the_request_and_the_technical_log()
    {
        using var logs = new CapturedLogs();
        await using var api = Api(logs: logs);
        using var client = api.CreateClient();
        var (token, session) = await IssueAsync(api, UserMulti, OrgA, UnitA1);
        const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        using var request = SwitchRequest(UnitA2, token, $"00-{TraceId}-00f067aa0ba902b7-01");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // (1) the request: the correlation id is the trace id the client sent, and it comes back in the response.
        Assert.Equal(TraceId, Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
        // (3) the audit record.
        var row = Assert.Single(await AuditRows(OrgA, UnitA1));
        Assert.Equal(("sessao:trocar-unidade", "sucesso", UserMulti, session, UnitA1), (row.Acao, row.Resultado, row.Usuario, row.Sessao, row.Unidade));
        Assert.Equal((UnitA2.ToString(), UnitA1.ToString(), UnitA2.ToString(), TraceId), (row.ObjetoId, row.Antes, row.Depois, row.Correlacao));
        // (2) the technical log line of the same request carries the same id.
        var line = Assert.Single(logs.Lines, l => l.Contains("http.requisicao", StringComparison.Ordinal) && l.Contains("/sessao/unidade-ativa", StringComparison.Ordinal));
        Assert.Contains($"CorrelationId={TraceId}", line, StringComparison.Ordinal);
        Assert.Contains("status=200", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_traceparent_the_request_still_gets_one_id_shared_by_the_response_log_and_audit()
    {
        using var logs = new CapturedLogs();
        await using var api = Api(logs: logs);
        using var client = api.CreateClient();
        using var request = SwitchRequest(UnitA2, await TokenAsync(api, UserMulti, OrgA, UnitA1));

        var response = await client.SendAsync(request);

        var id = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, Assert.Single(await AuditRows(OrgA, UnitA1)).Correlacao);
        Assert.Contains(logs.Lines, l => l.Contains($"CorrelationId={id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_switch_is_recorded_as_negado_on_its_own_transaction_and_the_unit_stays()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var token = await TokenAsync(api, UserMulti, OrgA, UnitA1);
        using var request = SwitchRequest(UnitA3, token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var row = Assert.Single(await AuditRows(OrgA, UnitA1));
        Assert.Equal(("sessao:trocar-unidade", "negado", UnitA3.ToString()), (row.Acao, row.Resultado, row.ObjetoId));
    }

    [Fact]
    public async Task A_switch_that_fails_after_being_audited_leaves_no_audit_record()
    {
        await using var api = Api();
        using var scope = api.Services.CreateScope();
        var unidades = scope.ServiceProvider.GetRequiredService<UnidadesDoUsuario>();
        var request = new FakeRequest(UserMulti, Guid.NewGuid(), new OrganizationContext(OrgA, UnitA1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => unidades.TrocarAsync(request, UnitA2, _ => throw new InvalidOperationException("a sessão não pôde ser alterada")));

        Assert.Empty(await AuditRows(OrgA, UnitA1)); // the action failed, so there is no record of it
        var ok = await unidades.TrocarAsync(request, UnitA2, _ => Task.CompletedTask);
        Assert.True(ok);
        Assert.Single(await AuditRows(OrgA, UnitA1));
    }

    [Fact]
    public async Task The_audit_of_one_organization_is_invisible_to_another()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        using var request = SwitchRequest(UnitA2, await TokenAsync(api, UserMulti, OrgA, UnitA1));
        await client.SendAsync(request);

        Assert.Single(await AuditRows(OrgA, UnitA1));
        Assert.Empty(await AuditRows(OrgB, UnitB1));
    }

    [Fact]
    public async Task The_technical_log_of_a_request_never_carries_the_query_headers_or_cookies_with_secrets()
    {
        using var logs = new CapturedLogs();
        await using var api = Api(logs: logs);
        using var client = api.CreateClient();
        const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        using var request = Get($"/unidades?token={Jwt}&senha=S3nh%40Secreta", await TokenAsync(api, UserMulti, OrgA, UnitA1));
        request.Headers.Add("X-Outro", "Bearer abcdef0123456789abcdef");
        request.Headers.Add("Cookie", "refresh=cookie-secreto-123456");

        await client.SendAsync(request);

        var all = string.Join('\n', logs.Lines);
        Assert.Contains("http.requisicao", all, StringComparison.Ordinal);
        foreach (var secret in new[] { Jwt, "S3nh", "abcdef0123456789abcdef", "cookie-secreto-123456" })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }

    private sealed class FakeRequest(Guid user, Guid session, OrganizationContext organization) : IRequestContext
    {
        public Guid UserId => user;

        public Guid SessionId => session;

        public OrganizationContext Organization => organization;
    }

    /// <summary>Every technical log line (message, state and scopes) as flat text: what any log sink could write.</summary>
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
                lock (owner.Lines)
                {
                    owner.Lines.Add(string.Join(' ', parts));
                }
            }
        }
    }

    private sealed class ApiFactory(string connectionString, string environment, string? key, CapturedLogs? logs = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }

            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString, ["Flow4Tech:Identidade:TempoMinimoDeLoginMs"] = "0" };
                if (key is not null)
                {
                    values["Flow4Tech:Tokens:AppKey"] = key;
                }

                config.AddInMemoryCollection(values);
            });
        }
    }
}
