using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Comandas.Infrastructure;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

/// <summary>
/// Dominio do salao, ticket 04: the tables of a unit through the real authenticated route. Real PostgreSQL, application credential (RLS on), real sessions. Covers allowed, denied (403 without
/// reason, 401 without session) and cross-organization cases, the audit record, uniqueness per unit (also under a race), the use-tables option, validation, rollback and the EF model.
/// </summary>
public sealed class MesasEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";

    private static readonly Guid OrgA = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid UnitA = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid UnitA2 = Guid.Parse("0197b000-0000-7000-8000-0000000000a2");
    private static readonly Guid DonoA = Guid.Parse("0197b000-0000-7000-8000-0000000000d1");
    private static readonly Guid Garcom = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
    private static readonly Guid Gerente = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");
    private static readonly Guid OrgB = Guid.Parse("0197b000-0000-7000-8000-00000000000b");
    private static readonly Guid UnitB = Guid.Parse("0197b000-0000-7000-8000-0000000000b1");
    private static readonly Guid DonoB = Guid.Parse("0197b000-0000-7000-8000-0000000000e1");

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
        foreach (var (org, unit, dono, email) in new[] { (OrgA, UnitA, DonoA, "dono@a.test"), (OrgB, UnitB, DonoB, "dono@b.test") })
        {
            await using var tx = await new TenantDatabase(ownerSource).BeginAsync(new OrganizationContext(org, unit));
            await Exec(tx, "INSERT INTO organizacoes (id, nome) VALUES (@o, 'Bar')", ("o", org));
            await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Centro')", ("u", unit), ("o", org));
            await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, email, dono, criada_em) VALUES (@i, @o, 'email', 'Dono', @e, true, now())", ("i", dono), ("o", org), ("e", email));
            await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", dono), ("n", unit));
            if (org == OrgA)
            {
                await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Praia')", ("u", UnitA2), ("o", org));
                foreach (var (id, nome, usuario) in new[] { (Garcom, "Garcom", "garcom"), (Gerente, "Gerente", "gerente") })
                {
                    await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', @n, @s, now())", ("i", id), ("o", org), ("n", nome), ("s", usuario));
                    await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", id), ("n", unit));
                }
            }

            await tx.CommitAsync();
        }
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

    private sealed class Api(string connection, Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connection,
                    ["Flow4Tech:Tokens:AppKey"] = Key,
                    ["Flow4Tech:Identidade:TempoMinimoDeLoginMs"] = "0",
                }));
            if (configure is not null)
            {
                builder.ConfigureTestServices(configure);
            }
        }
    }

    private sealed class FakeRequest(Guid user, OrganizationContext organization) : IRequestContext
    {
        public Guid UserId => user;

        public Guid SessionId => Guid.NewGuid();

        public OrganizationContext Organization => organization;
    }

    /// <summary>Real audit, but fails right after writing: the unit of work must roll the audit record back with the table.</summary>
    private sealed class AuditThatThrows(IAuditLog inner) : IAuditLog
    {
        public async Task RecordAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default)
        {
            await inner.RecordAsync(organization, entry, cancellationToken);
            throw new InvalidOperationException("boom after audit");
        }

        public Task RecordDetachedAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default) =>
            inner.RecordDetachedAsync(organization, entry, cancellationToken);
    }

    private sealed record Tokens(string Dono, string Garcom, string Gerente, string DonoB);

    private sealed record MesaBody(Guid Id, Guid UnidadeId, string Identificacao, int? Capacidade, bool Ativa);

    private sealed record Erro(string Codigo);

    /// <summary>The waiter may see tables only; the manager may manage them only (profiles are out of this slice, so tests seed them through the service).</summary>
    private async Task<Tokens> SeedAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var ver = await perfis.CriarAsync(request, "Garcom", [new RegraDeAcesso(RuleEffect.Allow, "mesa:ver")]);
        var gerir = await perfis.CriarAsync(request, "Gerente de mesas", [new RegraDeAcesso(RuleEffect.Allow, "mesa:gerenciar")]);
        Assert.Equal(ResultadoGestao.Ok, ver.Resultado);
        Assert.Equal(ResultadoGestao.Ok, gerir.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Garcom, ver.Id!.Value));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Gerente, gerir.Id!.Value));
        var sessoes = api.Services.GetRequiredService<Sessoes>();
        var client = new ClienteDaSessao("127.0.0.1", "testes");
        var versaoGarcom = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{Garcom:D}'", r => r.GetInt32(0))).Single();
        var versaoGerente = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{Gerente:D}'", r => r.GetInt32(0))).Single();
        return new Tokens(
            (await sessoes.IniciarAsync(DonoA, OrgA, 1, false, client)).AccessToken,
            (await sessoes.IniciarAsync(Garcom, OrgA, versaoGarcom, false, client)).AccessToken,
            (await sessoes.IniciarAsync(Gerente, OrgA, versaoGerente, false, client)).AccessToken,
            (await sessoes.IniciarAsync(DonoB, OrgB, 1, false, client)).AccessToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<MesaBody> CreateAsync(HttpClient client, string token, string name, int? capacity = null)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/mesas", token, new { identificacao = name, capacidade = capacity });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MesaBody>())!;
    }

    private static async Task<List<MesaBody>> ListAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/mesas", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<MesaBody>>())!;
    }

    private async Task<List<T>> QueryAsync<T>(Guid org, Guid unit, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
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
    public async Task Creating_a_table_stores_it_in_the_active_unit_audits_it_and_the_waiter_lists_it()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var mesa = await CreateAsync(client, t.Gerente, "  Varanda 3 ", 4);

        Assert.Equal(("Varanda 3", 4, true, UnitA), (mesa.Identificacao, mesa.Capacidade, mesa.Ativa, mesa.UnidadeId));
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT unidade_id, identificacao, capacidade, ativa FROM mesas", r => (r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3))));
        Assert.Equal((UnitA, "Varanda 3", 4, true), row);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_id, depois->>'identificacao' FROM auditoria_registros WHERE acao = 'mesa:criar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3))));
        Assert.Equal((Gerente, "sucesso", mesa.Id.ToString("D"), "Varanda 3"), audit);
        Assert.Equal(mesa.Id, Assert.Single(await ListAsync(client, t.Garcom)).Id);
    }

    [Fact]
    public async Task A_table_has_no_status_column_and_capacity_may_be_omitted()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var mesa = await CreateAsync(client, t.Gerente, "7");

        Assert.Null(mesa.Capacidade);
        var columns = await QueryAsync(OrgA, UnitA, "SELECT column_name FROM information_schema.columns WHERE table_name = 'mesas'", r => r.GetString(0));
        Assert.DoesNotContain(columns, c => c.Contains("status", StringComparison.OrdinalIgnoreCase) || c.Contains("ocupada", StringComparison.OrdinalIgnoreCase));
        using var raw = await SendAsync(client, HttpMethod.Get, "/mesas", t.Garcom);
        // Ticket 05: the list now reports a status, but it is derived from the open comandas (no column), and a table with none is free.
        Assert.Contains("\"status\":\"livre\"", await raw.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_identification_in_the_same_unit_is_refused_ignoring_case_and_writes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await CreateAsync(client, t.Gerente, "Mesa 1");

        foreach (var again in new[] { "Mesa 1", "mesa 1", "  MESA  1 " })
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/mesas", t.Gerente, new { identificacao = again });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("ja-existe", (await response.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        }

        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'mesa:criar'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Simultaneous_creations_of_one_identification_leave_exactly_one_table_and_the_rest_get_409()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/mesas", t.Gerente, new { identificacao = "Corrida" });
            return response.StatusCode;
        }));

        Assert.Single(statuses, HttpStatusCode.Created);
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'mesa:criar'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task The_same_identification_may_exist_in_another_unit_and_each_unit_lists_only_its_own()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await CreateAsync(client, t.Gerente, "Mesa 1");
        await using (var source = NpgsqlDataSource.Create(_appConnection))
        await using (var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA2)))
        {
            await Exec(tx, "INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, ativa, criada_em) VALUES (@i, @o, @u, 'Mesa 1', true, now())", ("i", Guid.CreateVersion7()), ("o", OrgA), ("u", UnitA2));
            await Exec(tx, "INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, ativa, criada_em) VALUES (@i, @o, @u, 'So da praia', true, now())", ("i", Guid.CreateVersion7()), ("o", OrgA), ("u", UnitA2));
            await tx.CommitAsync();
        }

        Assert.Equal([mesa.Id], (await ListAsync(client, t.Garcom)).Select(m => m.Id));
        Assert.Equal(3L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Missing_blank_or_out_of_range_input_is_a_400_and_nothing_is_written()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        foreach (var bad in new object[]
        {
            new { },
            new { capacidade = 4 },
            new { identificacao = (string?)null },
            new { identificacao = "   " },
            new { identificacao = new string('x', RegrasDeMesa.IdentificacaoMaxima + 1) },
            new { identificacao = "ok", capacidade = 0 },
            new { identificacao = "ok", capacidade = -2 },
            new { identificacao = "ok", capacidade = RegrasDeMesa.CapacidadeMaxima + 1 },
        })
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/mesas", t.Gerente, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalido", (await response.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        }

        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'mesa:criar'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Using_tables_is_a_layered_option_on_by_default()
    {
        await using var api = new Api(_appConnection);
        var settings = api.Services.GetRequiredService<ISettings>();

        var resolved = await settings.ResolveAsync(OperacaoSettings.UsarMesas, OrgA, UnitA, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("true", resolved.Value);
        Assert.Equal(SettingLayers.OrganizacaoEUnidade, OperacaoSettings.UsarMesas.Layers);
        Assert.Equal(SettingType.Booleano, OperacaoSettings.UsarMesas.Type);
        Assert.Equal("operacao.usar-mesas", OperacaoSettings.UsarMesas.Key);
    }

    [Fact]
    public async Task With_tables_turned_off_for_the_unit_the_list_is_empty_and_creating_is_refused_until_turned_on_again()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await CreateAsync(client, t.Gerente, "Antes");
        var configuracoes = api.Services.GetRequiredService<Configuracoes>();
        var dono = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));

        Assert.Equal(ResultadoConfiguracao.Ok, await configuracoes.AlterarAsync(dono, new AlterarConfiguracao(OperacaoSettings.UsarMesas.Key, CamadaDeEscrita.Unidade, "false", null, null, null)));

        Assert.Empty(await ListAsync(client, t.Garcom));
        using var refused = await SendAsync(client, HttpMethod.Post, "/mesas", t.Gerente, new { identificacao = "Depois" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("mesas-desligadas", (await refused.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'mesa:criar'", r => r.GetInt64(0))).Single());

        Assert.Equal(ResultadoConfiguracao.Ok, await configuracoes.AlterarAsync(dono, new AlterarConfiguracao(OperacaoSettings.UsarMesas.Key, CamadaDeEscrita.Unidade, "true", null, null, null)));
        Assert.Single(await ListAsync(client, t.Garcom));
        await CreateAsync(client, t.Gerente, "Depois");
    }

    [Fact]
    public async Task Turning_tables_off_for_the_organization_reaches_its_units_but_not_another_organization()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var configuracoes = api.Services.GetRequiredService<Configuracoes>();

        Assert.Equal(
            ResultadoConfiguracao.Ok,
            await configuracoes.AlterarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), new AlterarConfiguracao(OperacaoSettings.UsarMesas.Key, CamadaDeEscrita.Organizacao, "false", null, null, null)));

        using var refused = await SendAsync(client, HttpMethod.Post, "/mesas", t.Dono, new { identificacao = "X" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await CreateAsync(client, t.DonoB, "X");
    }

    [Fact]
    public async Task Each_route_is_denied_without_its_permission_and_without_a_session()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var cannotCreate = await SendAsync(client, HttpMethod.Post, "/mesas", t.Garcom, new { identificacao = "Nao" });
        Assert.Equal(HttpStatusCode.Forbidden, cannotCreate.StatusCode);
        Assert.Equal(string.Empty, await cannotCreate.Content.ReadAsStringAsync());
        using var cannotList = await SendAsync(client, HttpMethod.Get, "/mesas", t.Gerente);
        Assert.Equal(HttpStatusCode.Forbidden, cannotList.StatusCode);
        Assert.Equal(string.Empty, await cannotList.Content.ReadAsStringAsync());
        using var anonymousList = await SendAsync(client, HttpMethod.Get, "/mesas", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
        using var anonymousCreate = await SendAsync(client, HttpMethod.Post, "/mesas", null, new { identificacao = "Nao" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCreate.StatusCode);
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task One_organization_never_sees_the_tables_of_another_and_may_reuse_the_identification()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await CreateAsync(client, t.Dono, "Mesa 1");

        Assert.Empty(await ListAsync(client, t.DonoB));
        var deB = await CreateAsync(client, t.DonoB, "Mesa 1");

        Assert.Equal(UnitB, deB.UnidadeId);
        Assert.Equal(OrgB.ToString("D"), (await QueryAsync(OrgB, UnitB, $"SELECT organizacao_id::text FROM mesas WHERE id = '{deB.Id:D}'", r => r.GetString(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, $"SELECT count(*) FROM mesas WHERE organizacao_id <> '{OrgB:D}'", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task When_the_unit_of_work_throws_after_the_audit_the_table_and_the_audit_record_roll_back_together()
    {
        await using var api = new Api(_appConnection, services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IAuditLog));
            services.Remove(real);
            services.AddSingleton<IAuditLog>(p => new AuditThatThrows(ActivatorUtilities.CreateInstance<Flow4Tech.Infrastructure.Audit.PostgresAuditLog>(p)));
        });
        var gestao = api.Services.GetRequiredService<GestaoDeMesas>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gestao.CriarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), "Fantasma", null));

        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'mesa:criar'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task The_list_query_has_the_expected_sql_with_no_organization_filter_and_the_EF_model_matches_the_database()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new MesasDbContext(tx);

        var sql = PostgresMesasStore.Ativas(context, UnitA).ToQueryString();

        Assert.Contains("FROM mesas AS", sql, StringComparison.Ordinal);
        Assert.Contains("m.unidade_id = @", sql, StringComparison.Ordinal);
        Assert.Contains("m.ativa", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", sql, StringComparison.Ordinal);
        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
