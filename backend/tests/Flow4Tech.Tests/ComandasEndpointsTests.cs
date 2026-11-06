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
using Flow4Tech.Kernel.Events;
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
/// Dominio do salao, ticket 05: opening and listing comandas through the real authenticated route. Real PostgreSQL, application credential (RLS on), real sessions. Covers the display
/// number (gap-free under a race, restarting each operating day with an injected clock), tables and the use-tables option, visibility of other people's comandas, denial (403 without
/// reason, 401), two organizations, rollback of state + audit + outbox together, and the EF model and critical SQL.
/// </summary>
public sealed class ComandasEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";

    private static readonly Guid OrgA = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid UnitA = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid UnitA2 = Guid.Parse("0197b000-0000-7000-8000-0000000000a2");
    private static readonly Guid DonoA = Guid.Parse("0197b000-0000-7000-8000-0000000000d1");
    private static readonly Guid Garcom = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
    private static readonly Guid Garcom2 = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");
    private static readonly Guid Supervisor = Guid.Parse("0197b000-0000-7000-8000-0000000000c3");
    private static readonly Guid Mudo = Guid.Parse("0197b000-0000-7000-8000-0000000000c4");
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
                foreach (var (id, nome, usuario) in new[] { (Garcom, "Garcom", "garcom"), (Garcom2, "Garcom Dois", "garcom2"), (Supervisor, "Supervisor", "supervisor"), (Mudo, "Mudo", "mudo") })
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

    private sealed class DayClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The operating day follows its own injected clock, so a test can cross the 05:00 rollover without expiring the sessions (which use the real clock).</summary>
    private sealed class Api(string connection, DayClock? days = null, Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
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
            if (days is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOperatingDayResolver>();
                    services.AddSingleton<IOperatingDayResolver>(new ConfiguredOperatingDayResolver(days));
                });
            }

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

    /// <summary>Real events, but fails right after writing the outbox row: audit, outbox and the comanda must roll back together.</summary>
    private sealed class EventsThatThrow(IEventosDeComandas inner) : IEventosDeComandas
    {
        public async Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
        {
            await inner.PublicarAsync(organization, evento, cancellationToken);
            throw new InvalidOperationException("boom after outbox");
        }
    }

    private sealed record Tokens(string Dono, string Garcom, string Garcom2, string Supervisor, string Mudo, string DonoB);

    private sealed record ComandaBody(Guid Id, Guid UnidadeId, int Numero, string NumeroTexto, string Identificacao, Guid? MesaId, string Canal, Guid GarcomId, int? Pessoas, string Estado, decimal TaxaServicoPercentual, bool TaxaServicoRemovida, int Versao, DateTimeOffset AbertaEm);

    private sealed record MesaBody(Guid Id, string Identificacao, bool Ativa, string Status);

    private sealed record Erro(string Codigo, int? VersaoAtual);

    /// <summary>Profiles are out of this slice: tests seed them through the service. Waiters open and list; the supervisor lists and may see others'; the mute account has none.</summary>
    private async Task<Tokens> SeedAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var waiter = await perfis.CriarAsync(request, "Garcom", [new RegraDeAcesso(RuleEffect.Allow, "comanda:abrir"), new RegraDeAcesso(RuleEffect.Allow, "comanda:ver"), new RegraDeAcesso(RuleEffect.Allow, "mesa:ver")]);
        var chief = await perfis.CriarAsync(request, "Supervisor", [new RegraDeAcesso(RuleEffect.Allow, "comanda:ver"), new RegraDeAcesso(RuleEffect.Allow, "comanda:ver-comandas-de-outros")]);
        Assert.Equal(ResultadoGestao.Ok, waiter.Resultado);
        Assert.Equal(ResultadoGestao.Ok, chief.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Garcom, waiter.Id!.Value));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Garcom2, waiter.Id!.Value));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Supervisor, chief.Id!.Value));
        var sessoes = api.Services.GetRequiredService<Sessoes>();
        var client = new ClienteDaSessao("127.0.0.1", "testes");

        async Task<string> TokenAsync(Guid user, Guid org)
        {
            var versao = (await QueryAsync(org, org == OrgA ? UnitA : UnitB, $"SELECT versao_credencial FROM contas WHERE id = '{user:D}'", r => r.GetInt32(0))).Single();
            return (await sessoes.IniciarAsync(user, org, versao, false, client)).AccessToken;
        }

        return new Tokens(await TokenAsync(DonoA, OrgA), await TokenAsync(Garcom, OrgA), await TokenAsync(Garcom2, OrgA), await TokenAsync(Supervisor, OrgA), await TokenAsync(Mudo, OrgA), await TokenAsync(DonoB, OrgB));
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

    private static async Task<ComandaBody> OpenAsync(HttpClient client, string token, object? body = null)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/comandas", token, body ?? new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ComandaBody>())!;
    }

    private static async Task<List<ComandaBody>> ListAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/comandas", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ComandaBody>>())!;
    }

    private static async Task<MesaBody> CreateTableAsync(HttpClient client, string token, string name)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/mesas", token, new { identificacao = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MesaBody>())!;
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
    public async Task Opening_with_a_free_name_stores_audits_and_publishes_in_the_active_unit()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var comanda = await OpenAsync(client, t.Garcom, new { identificacao = "  Aniversario  da Ana ", pessoas = 6 });

        Assert.Equal((1, "#1", "Aniversario da Ana", "salao", Garcom, 6, "aberta", 10m, false, 1, UnitA, null), (comanda.Numero, comanda.NumeroTexto, comanda.Identificacao, comanda.Canal, comanda.GarcomId, comanda.Pessoas, comanda.Estado, comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida, comanda.Versao, comanda.UnidadeId, comanda.MesaId));
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT numero, identificacao, garcom_id, estado, versao, canal, taxa_servico_percentual, dia_operacional::text FROM comandas", r => (r.GetInt32(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetDecimal(6), r.GetString(7))));
        Assert.Equal((1, "Aniversario da Ana", Garcom, "aberta", 1, "salao", 10m), (row.Item1, row.Item2, row.Item3, row.Item4, row.Item5, row.Item6, row.Item7));
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_tipo, objeto_id, depois->>'numero' FROM auditoria_registros WHERE acao = 'comanda:abrir'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
        Assert.Equal((Garcom, "sucesso", "comanda", comanda.Id.ToString("D"), "1"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'comanda.aberta' AND payload->>'ComandaId' = '{comanda.Id:D}'", r => r.GetInt32(0)));
    }

    [Fact]
    public async Task Opening_with_only_a_number_gets_the_default_name_and_the_next_display_number()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var first = await OpenAsync(client, t.Garcom);
        var second = await OpenAsync(client, t.Garcom2);

        Assert.Equal((1, "Comanda 1", 2, "Comanda 2"), (first.Numero, first.Identificacao, second.Numero, second.Identificacao));
        Assert.Equal(Garcom2, second.GarcomId);
    }

    [Fact]
    public async Task Several_comandas_may_hold_the_same_table_and_the_table_list_shows_derived_status()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa5 = await CreateTableAsync(client, t.Dono, "5");
        var mesa6 = await CreateTableAsync(client, t.Dono, "6");

        using var before = await SendAsync(client, HttpMethod.Get, "/mesas", t.Garcom);
        Assert.All((await before.Content.ReadFromJsonAsync<List<MesaBody>>())!, m => Assert.Equal("livre", m.Status));

        var a = await OpenAsync(client, t.Garcom, new { mesaId = mesa5.Id });
        var b = await OpenAsync(client, t.Garcom2, new { mesaId = mesa5.Id });

        Assert.Equal(("5", mesa5.Id, "5", mesa5.Id), (a.Identificacao, a.MesaId, b.Identificacao, b.MesaId));
        using var after = await SendAsync(client, HttpMethod.Get, "/mesas", t.Garcom);
        var tables = (await after.Content.ReadFromJsonAsync<List<MesaBody>>())!.ToDictionary(m => m.Id, m => m.Status);
        Assert.Equal(("ocupada", "livre"), (tables[mesa5.Id], tables[mesa6.Id]));

        // A cancelled/closed comanda stops occupying the table: simulate the later states directly.
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using (var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA)))
        {
            await Exec(tx, "UPDATE comandas SET estado = 'cancelada', fechada_em = now()");
            await tx.CommitAsync();
        }

        using var released = await SendAsync(client, HttpMethod.Get, "/mesas", t.Garcom);
        Assert.All((await released.Content.ReadFromJsonAsync<List<MesaBody>>())!, m => Assert.Equal("livre", m.Status));
    }

    [Fact]
    public async Task With_tables_off_a_comanda_takes_a_name_or_number_and_a_table_is_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await CreateTableAsync(client, t.Dono, "5");
        var configuracoes = api.Services.GetRequiredService<Configuracoes>();
        Assert.Equal(ResultadoConfiguracao.Ok, await configuracoes.AlterarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), new AlterarConfiguracao(OperacaoSettings.UsarMesas.Key, CamadaDeEscrita.Unidade, "false", null, null, null)));

        var named = await OpenAsync(client, t.Garcom, new { identificacao = "Comanda 7" });
        using var refused = await SendAsync(client, HttpMethod.Post, "/comandas", t.Garcom, new { mesaId = mesa.Id });

        Assert.Equal(("Comanda 7", null), (named.Identificacao, named.MesaId));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("mesas-desligadas", (await refused.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Parallel_opens_get_distinct_numbers_without_gaps()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/comandas", i % 2 == 0 ? t.Garcom : t.Garcom2, new { });
            return (response.StatusCode, Body: response.StatusCode == HttpStatusCode.Created ? await response.Content.ReadFromJsonAsync<ComandaBody>() : null);
        }));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(Enumerable.Range(1, 12), results.Select(r => r.Body!.Numero).Order());
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), (await QueryAsync(OrgA, UnitA, "SELECT numero::bigint FROM comandas ORDER BY numero", r => r.GetInt64(0))));
        Assert.Equal(12L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:abrir'", r => r.GetInt64(0))).Single());
        Assert.Equal(12L, (await QueryAsync(OrgA, UnitA, "SELECT ultimo FROM comanda_numeracao", r => (long)r.GetInt32(0))).Single());
    }

    [Fact]
    public async Task The_number_restarts_at_one_on_the_next_operating_day_and_not_before_the_rollover()
    {
        var days = new DayClock();
        await using var api = new Api(_appConnection, days);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        Assert.Equal(1, (await OpenAsync(client, t.Garcom)).Numero);
        Assert.Equal(2, (await OpenAsync(client, t.Garcom)).Numero);

        // 04:00 local on the 6th (07:00Z) still belongs to the operating day of the 5th.
        days.UtcNow = new DateTimeOffset(2026, 11, 6, 7, 0, 0, TimeSpan.Zero);
        Assert.Equal(3, (await OpenAsync(client, t.Garcom)).Numero);

        // 06:00 local on the 6th (09:00Z) is a new operating day.
        days.UtcNow = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, (await OpenAsync(client, t.Garcom)).Numero);
        Assert.Equal(2, (await OpenAsync(client, t.Garcom2)).Numero);

        var perDay = await QueryAsync(OrgA, UnitA, "SELECT dia_operacional::text, max(numero)::bigint, count(*) FROM comandas GROUP BY dia_operacional ORDER BY 1", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        Assert.Equal([("2026-11-05", 3L, 3L), ("2026-11-06", 2L, 2L)], perDay);
    }

    [Fact]
    public async Task Numbers_are_independent_per_unit_and_per_organization()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenAsync(client, t.Garcom);
        await OpenAsync(client, t.Garcom);

        var deB = await OpenAsync(client, t.DonoB);

        Assert.Equal(1, deB.Numero);
        Assert.Equal(UnitB, deB.UnidadeId);
        Assert.Equal(OrgB.ToString("D"), (await QueryAsync(OrgB, UnitB, $"SELECT organizacao_id::text FROM comandas WHERE id = '{deB.Id:D}'", r => r.GetString(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, $"SELECT count(*) FROM comandas WHERE organizacao_id <> '{OrgB:D}'", r => r.GetInt64(0))).Single());
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
        Assert.Equal([deB.Id], (await ListAsync(client, t.DonoB)).Select(c => c.Id));
    }

    [Fact]
    public async Task Refused_input_returns_400_and_writes_nothing_not_even_a_number()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await CreateTableAsync(client, t.Dono, "5");
        var foreign = Guid.NewGuid();

        foreach (var bad in new object[]
        {
            new { identificacao = "   " },
            new { identificacao = new string('x', 41) },
            new { identificacao = "a\tb" },
            new { pessoas = 0 },
            new { pessoas = 1000 },
            new { identificacao = "Ana", mesaId = mesa.Id },
            new { mesaId = foreign },
        })
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/comandas", t.Garcom, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalido", (await response.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        }

        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM comanda_numeracao", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:abrir'", r => r.GetInt64(0))).Single());
        Assert.Equal(1, (await OpenAsync(client, t.Garcom)).Numero);
    }

    [Fact]
    public async Task The_database_refuses_a_comanda_holding_a_table_of_a_sibling_unit_of_the_same_organization()
    {
        var mesaDaOutraUnidade = Guid.NewGuid();
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await Exec(tx, "INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, ativa, criada_em) VALUES (@m, @o, @u, 'Praia 1', true, now())", ("m", mesaDaOutraUnidade), ("o", OrgA), ("u", UnitA2));

        var erro = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            tx,
            "INSERT INTO comandas (id, organizacao_id, unidade_id, dia_operacional, numero, identificacao, mesa_id, garcom_id, aberta_em, taxa_servico_percentual) VALUES (@i, @o, @u, current_date, 1, 'x', @m, @g, now(), 10)",
            ("i", Guid.NewGuid()), ("o", OrgA), ("u", UnitA), ("m", mesaDaOutraUnidade), ("g", Garcom)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, erro.SqlState);
    }

    [Fact]
    public async Task A_table_of_another_organization_cannot_be_used()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesaB = await CreateTableAsync(client, t.DonoB, "9");

        using var response = await SendAsync(client, HttpMethod.Post, "/comandas", t.Garcom, new { mesaId = mesaB.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Without_the_permission_a_waiter_sees_only_their_own_comandas_and_with_it_every_one_of_the_active_unit()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mine = await OpenAsync(client, t.Garcom);
        var other = await OpenAsync(client, t.Garcom2);
        await using (var source = NpgsqlDataSource.Create(_appConnection))
        await using (var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA2)))
        {
            await Exec(tx, "INSERT INTO comandas (id, organizacao_id, unidade_id, dia_operacional, numero, identificacao, garcom_id, aberta_em, taxa_servico_percentual) VALUES (@i, @o, @u, '2026-11-05', 1, 'Praia', @g, now(), 10)", ("i", Guid.NewGuid()), ("o", OrgA), ("u", UnitA2), ("g", Garcom));
            await tx.CommitAsync();
        }

        Assert.Equal([mine.Id], (await ListAsync(client, t.Garcom)).Select(c => c.Id));
        Assert.Equal([other.Id], (await ListAsync(client, t.Garcom2)).Select(c => c.Id));
        Assert.Equivalent(new[] { mine.Id, other.Id }, (await ListAsync(client, t.Supervisor)).Select(c => c.Id));
        Assert.Equivalent(new[] { mine.Id, other.Id }, (await ListAsync(client, t.Dono)).Select(c => c.Id));
        Assert.All(await ListAsync(client, t.Supervisor), c => Assert.Equal(UnitA, c.UnidadeId));
        Assert.Empty(await ListAsync(client, t.DonoB));
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Each_route_is_denied_without_its_permission_and_without_a_session()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[] { (HttpMethod.Post, "/comandas", new { }), (HttpMethod.Get, "/comandas", null) })
        {
            using var denied = await SendAsync(client, method, path, t.Mudo, body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(string.Empty, await denied.Content.ReadAsStringAsync());
            using var anonymous = await SendAsync(client, method, path, null, body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        // The supervisor may list but not open.
        using var cannotOpen = await SendAsync(client, HttpMethod.Post, "/comandas", t.Supervisor, new { });
        Assert.Equal(HttpStatusCode.Forbidden, cannotOpen.StatusCode);
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task The_service_fee_is_frozen_at_opening_from_the_layered_option()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var first = await OpenAsync(client, t.Garcom);
        var configuracoes = api.Services.GetRequiredService<Configuracoes>();

        Assert.Equal(ResultadoConfiguracao.Ok, await configuracoes.AlterarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), new AlterarConfiguracao(VendaSettings.TaxaServicoPercentual.Key, CamadaDeEscrita.Organizacao, "12.5", null, null, null)));
        var second = await OpenAsync(client, t.Garcom);

        Assert.Equal((10m, 12.5m), (first.TaxaServicoPercentual, second.TaxaServicoPercentual));
        Assert.Equal([10m, 12.5m], (await QueryAsync(OrgA, UnitA, "SELECT taxa_servico_percentual FROM comandas ORDER BY numero", r => r.GetDecimal(0))));
        Assert.Equal(("venda.taxa-servico-percentual", SettingLayers.OrganizacaoEUnidade, 0m, 20m), (VendaSettings.TaxaServicoPercentual.Key, VendaSettings.TaxaServicoPercentual.Layers, VendaSettings.TaxaServicoPercentual.Minimum, VendaSettings.TaxaServicoPercentual.Maximum));
    }

    [Fact]
    public async Task When_the_unit_of_work_throws_after_the_outbox_the_comanda_the_audit_the_event_and_the_number_roll_back_together()
    {
        await using var api = new Api(_appConnection, configure: services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IEventosDeComandas));
            services.Remove(real);
            services.AddSingleton<IEventosDeComandas>(p => new EventsThatThrow(ActivatorUtilities.CreateInstance<OutboxEventosDeComandas>(p)));
        });
        var gestao = api.Services.GetRequiredService<GestaoDeComandas>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gestao.AbrirAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), "Fantasma", null, null));

        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:abrir'", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'comanda.aberta'", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM comanda_numeracao", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task The_critical_queries_have_the_expected_sql_with_no_organization_filter_and_the_EF_model_matches_the_database()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new MesasDbContext(tx);

        var list = PostgresComandasStore.Abertas(context, UnitA, Garcom).ToQueryString();
        var all = PostgresComandasStore.Abertas(context, UnitA, null).ToQueryString();
        var occupied = PostgresMesasStore.Ocupadas(context, UnitA).ToQueryString();

        Assert.Contains("FROM comandas AS", list, StringComparison.Ordinal);
        Assert.Contains("c.unidade_id = @", list, StringComparison.Ordinal);
        Assert.Contains("c.estado = 'aberta'", list, StringComparison.Ordinal);
        Assert.Contains("c.garcom_id = @", list, StringComparison.Ordinal);
        Assert.DoesNotContain("c.garcom_id = @", all, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", list, StringComparison.Ordinal);
        Assert.Contains("FROM comandas AS", occupied, StringComparison.Ordinal);
        Assert.Contains("c.estado = 'aberta'", occupied, StringComparison.Ordinal);
        Assert.Contains("DISTINCT", occupied, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", list + all + occupied, StringComparison.Ordinal);
        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
