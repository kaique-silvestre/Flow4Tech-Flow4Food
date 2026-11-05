using System.Net;
using System.Net.Http.Headers;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

/// <summary>
/// Ticket 05 (spec 5.4.1, 5.4.2, 5.4.8): the declared permission of a route is DECIDED by the engine through the permission handler. No session: 401; no permission: 403 with an
/// empty body and the reason only in the technical log; allowed: 2xx; a denial on a sensitive route is audited. Real PostgreSQL, application credential, real sessions.
/// </summary>
public sealed class PermissoesEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";

    private static readonly Guid Org = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    private static readonly Guid Unit = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Dono = Guid.Parse("0197a000-0000-7000-8000-0000000000d1");
    private static readonly Guid Comum = Guid.Parse("0197a000-0000-7000-8000-0000000000c1");

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

        await using var ownerSource = NpgsqlDataSource.Create(owner.ConnectionString);
        await using var tx = await new TenantDatabase(ownerSource).BeginAsync(new OrganizationContext(Org, Unit));
        await Exec(tx, "INSERT INTO organizacoes (id, nome) VALUES (@o, 'Bar')", ("o", Org));
        await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Centro')", ("u", Unit), ("o", Org));
        await Exec(
            tx,
            "INSERT INTO contas (id, organizacao_id, tipo, nome, email, dono, criada_em) VALUES (@i, @o, 'email', 'Dono', 'dono@exemplo.test', true, now())",
            ("i", Dono), ("o", Org));
        await Exec(
            tx,
            "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', 'Comum', 'comum', now())",
            ("i", Comum), ("o", Org));
        foreach (var user in new[] { Dono, Comum })
        {
            await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", Org), ("u", user), ("n", Unit));
        }

        await tx.CommitAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static async Task Exec(TenantTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
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

    private ApiFactory Api(CapturedLogs? logs = null, bool noProfiles = false) => new(_appConnection, logs, noProfiles);

    private static async Task<string> TokenAsync(ApiFactory api, Guid user)
    {
        var emitida = await api.Services.GetRequiredService<Sessoes>().IniciarAsync(user, Org, 1, false, new ClienteDaSessao("127.0.0.1", "testes"));
        return emitida.AccessToken;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(Org, Unit));
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    [Fact]
    public async Task Without_a_session_a_protected_route_answers_401_and_the_engine_is_not_asked()
    {
        using var api = Api();
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/unidades", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/sessoes/todas", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/unidades", "isto.nao.e-um-token")).StatusCode);
    }

    [Fact]
    public async Task A_permission_the_profile_has_is_allowed()
    {
        using var api = Api();
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/unidades", await TokenAsync(api, Comum))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/sessoes", await TokenAsync(api, Comum))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/sessoes/todas", await TokenAsync(api, Dono))).StatusCode);
    }

    [Fact]
    public async Task A_permission_the_profile_lacks_answers_403_with_an_empty_body_and_the_reason_only_in_the_log()
    {
        using var logs = new CapturedLogs();
        using var api = Api(logs);
        using var client = api.CreateClient();

        var response = await GetAsync(client, "/sessoes/todas", await TokenAsync(api, Comum));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers), h => h.Value.Any(v => v.Contains("sessao:ver-todas", StringComparison.Ordinal)));
        var line = Assert.Single(logs.Snapshot(), l => l.Contains("acesso.negado", StringComparison.Ordinal));
        Assert.Contains("sessao:ver-todas", line, StringComparison.Ordinal);
        Assert.Contains("default-deny", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_without_any_profile_is_denied_even_the_routes_that_everybody_has()
    {
        using var api = Api(noProfiles: true);
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, "/unidades", await TokenAsync(api, Dono))).StatusCode);
    }

    [Fact]
    public async Task A_denial_on_a_sensitive_route_is_audited_without_the_reason_and_a_denial_elsewhere_is_not()
    {
        using var api = Api();
        using var client = api.CreateClient();
        var token = await TokenAsync(api, Comum);
        var before = (await QueryAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'acesso:negar'", r => r.GetInt64(0))).Single();

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, "/sessoes/todas", token)).StatusCode);

        var rows = await QueryAsync(
            "SELECT resultado, usuario_id, objeto_tipo, objeto_id, depois->>'permissao', depois::text FROM auditoria_registros WHERE acao = 'acesso:negar' ORDER BY sequencia",
            r => (Result: r.GetString(0), User: r.GetGuid(1), Type: r.GetString(2), Object: r.GetString(3), Permission: r.GetString(4), After: r.GetString(5)));
        Assert.Equal(before + 1, rows.Count);
        var row = rows[^1];
        Assert.Equal("negado", row.Result);
        Assert.Equal(Comum, row.User);
        Assert.Equal("sessao:ver-todas", row.Permission);
        Assert.DoesNotContain("default-deny", row.After, StringComparison.Ordinal);

        // A denial on a route that is not marked sensitive leaves no audit record (the technical log has it).
        using var noProfiles = Api(noProfiles: true);
        using var other = noProfiles.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(other, "/unidades", await TokenAsync(noProfiles, Dono))).StatusCode);
        Assert.Equal(before + 1, (await QueryAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'acesso:negar'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Public_routes_need_neither_session_nor_decision()
    {
        using var api = Api(noProfiles: true);
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/version", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/health/live", null)).StatusCode);
    }

    private sealed class ApiFactory(string connectionString, CapturedLogs? logs, bool noProfiles) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }

            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Flow4Tech:Tokens:AppKey"] = Key,
                    ["Flow4Tech:Identidade:TempoMinimoDeLoginMs"] = "0",
                }));
            if (noProfiles)
            {
                builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IPerfisDoUsuario>(new SemPerfis())));
            }
        }
    }

    private sealed class SemPerfis : IPerfisDoUsuario
    {
        public Task<IReadOnlyList<string>> ListarAsync(IRequestContext request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class CapturedLogs : ILoggerProvider, ISupportExternalScope
    {
        private readonly List<string> _lines = [];
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public IReadOnlyList<string> Snapshot()
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

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

                lock (owner._lines)
                {
                    owner._lines.Add(string.Join(' ', parts));
                }
            }
        }
    }
}
