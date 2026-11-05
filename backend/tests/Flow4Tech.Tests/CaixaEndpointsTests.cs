using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flow4Tech.Caixa.Application;
using Flow4Tech.Caixa.Domain;
using Flow4Tech.Caixa.Infrastructure;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

/// <summary>
/// Dominio do salao, ticket 03: the cash shift through the real authenticated route. Real PostgreSQL, application credential (RLS on), real sessions. Covers allowed, denied (403 without
/// reason, 401 without session) and cross-organization cases, the audit record and outbox event of each change, one open shift per operator (also under a race) and the EF model against the migrated schema.
/// </summary>
public sealed class CaixaEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";

    private static readonly Guid OrgA = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid UnitA = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid DonoA = Guid.Parse("0197b000-0000-7000-8000-0000000000d1");
    private static readonly Guid Garcom = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
    private static readonly Guid Fiscal = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");
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
                await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', 'Garcom', 'garcom', now())", ("i", Garcom), ("o", org));
                await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", Garcom), ("n", unit));
                await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', 'Fiscal', 'fiscal', now())", ("i", Fiscal), ("o", org));
                await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", Fiscal), ("n", unit));
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

    private sealed class Api(string connection) : WebApplicationFactory<Program>
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
        }
    }

    private sealed class FakeRequest(Guid user, OrganizationContext organization) : IRequestContext
    {
        public Guid UserId => user;

        public Guid SessionId => Guid.NewGuid();

        public OrganizationContext Organization => organization;
    }

    private sealed record Tokens(string Dono, string Garcom, string Fiscal, string DonoB);

    private sealed record Turno(Guid Id, Guid UnidadeId, Guid OperadorId, DateTimeOffset AbertoEm, long FundoCentavos, DateTimeOffset? FechadoEm, long? ContadoCentavos, long? EsperadoCentavos, long? DiferencaCentavos, string? DiferencaTexto);

    /// <summary>The waiter gets a custom profile with caixa:ver/abrir/fechar, the inspector one with caixa:ver only (profiles are out of this slice, so tests seed them through the service).</summary>
    private async Task<Tokens> SeedAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var operador = await perfis.CriarAsync(request, "Operador", [new RegraDeAcesso(RuleEffect.Allow, "caixa:ver"), new RegraDeAcesso(RuleEffect.Allow, "caixa:abrir"), new RegraDeAcesso(RuleEffect.Allow, "caixa:fechar")]);
        var fiscal = await perfis.CriarAsync(request, "Fiscal", [new RegraDeAcesso(RuleEffect.Allow, "caixa:ver")]);
        Assert.Equal(ResultadoGestao.Ok, operador.Resultado);
        Assert.Equal(ResultadoGestao.Ok, fiscal.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Garcom, operador.Id!.Value));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Fiscal, fiscal.Id!.Value));
        var sessoes = api.Services.GetRequiredService<Sessoes>();
        var client = new ClienteDaSessao("127.0.0.1", "testes");
        var versaoGarcom = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{Garcom:D}'", r => r.GetInt32(0))).Single();
        var versaoFiscal = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{Fiscal:D}'", r => r.GetInt32(0))).Single();
        return new Tokens(
            (await sessoes.IniciarAsync(DonoA, OrgA, 1, false, client)).AccessToken,
            (await sessoes.IniciarAsync(Garcom, OrgA, versaoGarcom, false, client)).AccessToken,
            (await sessoes.IniciarAsync(Fiscal, OrgA, versaoFiscal, false, client)).AccessToken,
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

    private static async Task<Turno> OpenAsync(HttpClient client, string token, long fundo)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/caixa/turnos", token, new { fundoCentavos = fundo });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Turno>())!;
    }

    private static async Task<Turno> CloseAsync(HttpClient client, string token, long contado)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", token, new { contadoCentavos = contado });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Turno>())!;
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
    public async Task Opening_a_shift_with_a_zero_float_is_stored_audited_and_published()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var turno = await OpenAsync(client, t.Garcom, 0);

        Assert.Equal((Garcom, UnitA, 0L, null, null), (turno.OperadorId, turno.UnidadeId, turno.FundoCentavos, turno.FechadoEm, turno.ContadoCentavos));
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT operador_id, unidade_id, fundo_centavos, fechado_em IS NULL FROM turnos_caixa", r => (r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.GetBoolean(3))));
        Assert.Equal((Garcom, UnitA, 0L, true), row);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_id, depois->>'fundoCentavos' FROM auditoria_registros WHERE acao = 'caixa:abrir'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3))));
        Assert.Equal((Garcom, "sucesso", turno.Id.ToString("D"), "0"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'caixa.aberto' AND payload->>'TurnoId' = '{turno.Id:D}'", r => r.GetInt32(0)));
        using var current = await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual", t.Garcom);
        Assert.Equal(turno.Id, (await current.Content.ReadFromJsonAsync<Turno>())!.Id);
    }

    [Fact]
    public async Task A_second_open_shift_of_the_same_operator_and_unit_is_refused_but_another_operator_may_open()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenAsync(client, t.Garcom, 5000);

        using var again = await SendAsync(client, HttpMethod.Post, "/caixa/turnos", t.Garcom, new { fundoCentavos = 100 });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await OpenAsync(client, t.Dono, 1000);
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM turnos_caixa WHERE fechado_em IS NULL", r => r.GetInt64(0))).Single());
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM turnos_caixa WHERE operador_id = '{Garcom:D}'", r => r.GetInt32(0)));
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'caixa:abrir'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Two_simultaneous_opens_by_one_operator_leave_exactly_one_shift()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/caixa/turnos", t.Garcom, new { fundoCentavos = 100 });
            return response.StatusCode;
        }));

        Assert.Single(statuses, HttpStatusCode.Created);
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM turnos_caixa", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'caixa.aberto'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task The_float_is_not_a_sale_and_closing_reports_expected_difference_and_words()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var turno = await OpenAsync(client, t.Garcom, 5000);

        var fechado = await CloseAsync(client, t.Garcom, 4500);

        Assert.Equal(turno.Id, fechado.Id);
        Assert.Equal((5000L, 4500L, -500L, "Falta R$ 5,00"), (fechado.EsperadoCentavos, fechado.ContadoCentavos, fechado.DiferencaCentavos, fechado.DiferencaTexto));
        Assert.NotNull(fechado.FechadoEm);
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT contado_centavos, esperado_centavos, diferenca_centavos, fechado_em IS NOT NULL FROM turnos_caixa", r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetBoolean(3))));
        Assert.Equal((4500L, 5000L, -500L, true), row);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_id, depois->>'diferencaCentavos', depois->>'esperadoCentavos' FROM auditoria_registros WHERE acao = 'caixa:fechar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3))));
        Assert.Equal((Garcom, turno.Id.ToString("D"), "-500", "5000"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'caixa.fechado' AND payload->>'TurnoId' = '{turno.Id:D}' AND payload->>'DiferencaCentavos' = '-500'", r => r.GetInt32(0)));
        using var noCurrent = await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual", t.Garcom);
        Assert.Equal(HttpStatusCode.NotFound, noCurrent.StatusCode);
    }

    [Fact]
    public async Task Closing_accepts_a_zero_count_and_always_concludes_for_surplus_and_exact_cases()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        await OpenAsync(client, t.Garcom, 0);
        var exact = await CloseAsync(client, t.Garcom, 0);
        Assert.Equal((0L, "Confere"), (exact.DiferencaCentavos, exact.DiferencaTexto));

        // A closed shift frees the operator to open the next one; the count above the float is a surplus.
        await OpenAsync(client, t.Garcom, 2000);
        var surplus = await CloseAsync(client, t.Garcom, 2350);
        Assert.Equal((350L, "Sobra R$ 3,50"), (surplus.DiferencaCentavos, surplus.DiferencaTexto));
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM turnos_caixa WHERE fechado_em IS NOT NULL", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Closing_without_an_open_shift_or_with_a_bad_value_is_refused_and_writes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var none = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Garcom, new { contadoCentavos = 0 });
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        await OpenAsync(client, t.Garcom, 1000);
        foreach (var bad in new object[] { new { contadoCentavos = -1 }, new { contadoCentavos = RegrasDoCaixa.ValorMaximoCentavos + 1 } })
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Garcom, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var badFloat = await SendAsync(client, HttpMethod.Post, "/caixa/turnos", t.Dono, new { fundoCentavos = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, badFloat.StatusCode);
        await CloseAsync(client, t.Garcom, 1000);
        using var twice = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Garcom, new { contadoCentavos = 1000 });
        Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'caixa:fechar' AND resultado <> 'sucesso'", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'caixa:fechar'", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM turnos_caixa", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Without_see_all_only_the_own_shift_is_visible_and_with_it_every_shift_of_the_unit()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var doGarcom = await OpenAsync(client, t.Garcom, 100);
        var doDono = await OpenAsync(client, t.Dono, 200);

        using var own = await SendAsync(client, HttpMethod.Get, "/caixa/turnos", t.Garcom);
        Assert.Equal([doGarcom.Id], (await own.Content.ReadFromJsonAsync<List<Turno>>())!.Select(x => x.Id));
        using var all = await SendAsync(client, HttpMethod.Get, "/caixa/turnos", t.Dono);
        Assert.Equivalent(new[] { doGarcom.Id, doDono.Id }, (await all.Content.ReadFromJsonAsync<List<Turno>>())!.Select(x => x.Id));
        using var inspector = await SendAsync(client, HttpMethod.Get, "/caixa/turnos", t.Fiscal);
        Assert.Empty((await inspector.Content.ReadFromJsonAsync<List<Turno>>())!);
    }

    [Fact]
    public async Task Opening_and_closing_are_denied_without_the_permission_and_the_denial_is_audited()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenAsync(client, t.Garcom, 100);

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/caixa/turnos", new { fundoCentavos = 0 }),
            (HttpMethod.Post, "/caixa/turnos/atual/fechar", new { contadoCentavos = 0 }),
        })
        {
            using var denied = await SendAsync(client, method, path, t.Fiscal, body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(string.Empty, await denied.Content.ReadAsStringAsync());
            using var anonymous = await SendAsync(client, method, path, null, body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        foreach (var path in new[] { "/caixa/turnos", "/caixa/turnos/atual" })
        {
            using var anonymous = await SendAsync(client, HttpMethod.Get, path, null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var denials = await QueryAsync(OrgA, UnitA, "SELECT objeto_id FROM auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' ORDER BY objeto_id", r => r.GetString(0));
        Assert.Equal(["caixa:abrir", "caixa:fechar"], denials);
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM turnos_caixa WHERE fechado_em IS NULL", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task One_organization_never_sees_or_closes_the_shift_of_another()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenAsync(client, t.Dono, 9900);

        using var list = await SendAsync(client, HttpMethod.Get, "/caixa/turnos", t.DonoB);
        Assert.Empty((await list.Content.ReadFromJsonAsync<List<Turno>>())!);
        using var current = await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual", t.DonoB);
        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        using var close = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.DonoB, new { contadoCentavos = 0 });
        Assert.Equal(HttpStatusCode.NotFound, close.StatusCode);

        var deB = await OpenAsync(client, t.DonoB, 100);
        Assert.Equal(OrgB.ToString("D"), (await QueryAsync(OrgB, UnitB, $"SELECT organizacao_id::text FROM turnos_caixa WHERE id = '{deB.Id:D}'", r => r.GetString(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, $"SELECT count(*) FROM turnos_caixa WHERE organizacao_id <> '{OrgB:D}'", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM turnos_caixa", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Requiring_an_open_shift_to_receive_cash_is_a_layered_option_that_defaults_on()
    {
        await using var api = new Api(_appConnection);
        var settings = api.Services.GetRequiredService<ISettings>();

        var resolved = await settings.ResolveAsync(CaixaSettings.ExigirCaixaAberto, OrgA, UnitA, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("true", resolved.Value);
        Assert.Equal(SettingLayers.OrganizacaoEUnidade, CaixaSettings.ExigirCaixaAberto.Layers);
        Assert.Equal("caixa.exigir-caixa-aberto", CaixaSettings.ExigirCaixaAberto.Key);
    }

    [Fact]
    public async Task The_shift_EF_model_matches_the_migrated_database()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new CaixaDbContext(tx);

        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
