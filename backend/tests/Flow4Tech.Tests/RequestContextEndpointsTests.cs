using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Api;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Flow4Tech.Kernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

/// <summary>
/// Ticket 01 (spec 5.1): organization and unit come from the session only. Real PostgreSQL, application credential (RLS on), test session
/// (signed header, Development/Testing only; ticket 04 replaces it with the real session).
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

    private ApiFactory Api(string environment = "Testing", string? key = Key) => new(_appConnection, environment, key);

    private static HttpRequestMessage Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Add(TestSession.HeaderName, token);
        }

        return request;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string? token)
    {
        using var request = Get(path, token);
        return await client.SendAsync(request);
    }

    private static string Token(Guid user, Guid org, Guid? unit, Guid? session = null, string key = Key) =>
        TestSession.CreateToken(key, session ?? Guid.NewGuid(), user, org, unit);

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
    public async Task A_forged_or_wrongly_signed_session_answers_401()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var good = Token(UserMulti, OrgA, UnitA1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/unidades", Token(UserMulti, OrgA, UnitA1, key: "outra-chave-outra-chave-outra-chave-123"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/unidades", "lixo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/unidades", good[..^3] + "AAA")).StatusCode);
    }

    [Fact]
    public async Task A_session_without_an_active_unit_has_no_resolved_context_and_answers_401()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/unidades", Token(UserMulti, OrgA, null))).StatusCode);
    }

    [Fact]
    public async Task A_user_without_a_link_to_the_requested_unit_answers_403()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        // UserSolo is linked to A1 only; A2 exists in the same organization but there is no link.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "/unidades", Token(UserSolo, OrgA, UnitA2))).StatusCode);
        // A unit of another organization (and an organization the user does not belong to) never resolves.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "/unidades", Token(UserMulti, OrgA, UnitB1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "/unidades", Token(UserMulti, OrgB, UnitA1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "/unidades", Token(UserMulti, Guid.NewGuid(), Guid.NewGuid()))).StatusCode);
    }

    // ---- context feeds the unit of work; the user only sees linked units ----

    [Fact]
    public async Task A_linked_user_sees_only_the_units_they_have_a_link_to_under_RLS()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        var response = await Send(client, "/unidades", Token(UserMulti, OrgA, UnitA1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var units = await response.Content.ReadFromJsonAsync<List<UnidadeDto>>();
        Assert.Equal([UnitA1, UnitA2], units!.Select(u => u.Id).Order());
        Assert.DoesNotContain(units!, u => u.Id == UnitA3 || u.Id == UnitB1);

        var solo = await Send(client, "/unidades", Token(UserSolo, OrgA, UnitA1));
        Assert.Equal([UnitA1], (await solo.Content.ReadFromJsonAsync<List<UnidadeDto>>())!.Select(u => u.Id));

        var b = await Send(client, "/unidades", Token(UserB, OrgB, UnitB1));
        Assert.Equal([UnitB1], (await b.Content.ReadFromJsonAsync<List<UnidadeDto>>())!.Select(u => u.Id));
    }

    [Fact]
    public async Task Organization_and_unit_sent_by_the_client_in_header_query_or_cookie_are_ignored()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        using var request = Get($"/unidades?organizacaoId={OrgB}&unidadeId={UnitB1}&organizationId={OrgB}", Token(UserMulti, OrgA, UnitA1));
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
        var token = Token(UserMulti, OrgA, UnitA1, session: Guid.NewGuid());

        using var before = Get("/sessao/unidade-ativa", token);
        Assert.Equal(UnitA1, (await (await client.SendAsync(before)).Content.ReadFromJsonAsync<UnidadeAtivaDto>())!.UnidadeId);

        using var switchRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("/sessao/unidade-ativa", UriKind.Relative))
        {
            Content = JsonContent.Create(new { unidadeDestinoId = UnitA2 }),
        };
        switchRequest.Headers.Add(TestSession.HeaderName, token);
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
        var token = Token(UserMulti, OrgA, UnitA1, session: Guid.NewGuid());
        var destination = target switch { "A3" => UnitA3, "B1" => UnitB1, _ => Guid.NewGuid() };

        using var switchRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("/sessao/unidade-ativa", UriKind.Relative))
        {
            Content = JsonContent.Create(new { unidadeDestinoId = destination }),
        };
        switchRequest.Headers.Add(TestSession.HeaderName, token);
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

    // ---- the test session is never a production door ----

    [Theory]
    [InlineData("Production", Key)] // key configured, valid signature: still ignored
    [InlineData("Staging", Key)]
    [InlineData("Testing", null)] // test environment but no key configured: nothing enabled
    [InlineData("Production", null)]
    public async Task The_test_session_is_only_enabled_in_development_or_testing_with_a_configured_key(string environment, string? key)
    {
        await using var api = Api(environment, key);
        using var client = api.CreateClient();

        var response = await Send(client, "/unidades", Token(UserMulti, OrgA, UnitA1));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Public_routes_do_not_need_a_session()
    {
        await using var api = Api("Production", null);
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/version", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
    }

    private sealed class ApiFactory(string connectionString, string environment, string? key) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString };
                if (key is not null)
                {
                    values["Flow4Tech:TestSession:Key"] = key;
                }

                config.AddInMemoryCollection(values);
            });
        }
    }
}
