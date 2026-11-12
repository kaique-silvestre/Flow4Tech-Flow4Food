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
/// F4-0054: areas, the table map derived from the open comandas, and moving a comanda to another table, through the real authenticated route. Real PostgreSQL, application credential (RLS on),
/// real sessions. Covers allowed, denied (403 without reason, 401 without session), cross-organization and cross-unit cases, the audit record, the outbox event, optimistic versions, the use-tables
/// option, rollback and the EF model.
/// </summary>
public sealed class MapaDeMesasEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";

    private static readonly Guid OrgA = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid UnitA = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid UnitA2 = Guid.Parse("0197b000-0000-7000-8000-0000000000a2");
    private static readonly Guid DonoA = Guid.Parse("0197b000-0000-7000-8000-0000000000d1");
    private static readonly Guid Garcom = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
    private static readonly Guid Gerente = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");
    private static readonly Guid Sem = Guid.Parse("0197b000-0000-7000-8000-0000000000c3");
    private static readonly Guid Outro = Guid.Parse("0197b000-0000-7000-8000-0000000000c4");
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
                foreach (var (id, nome, usuario) in new[] { (Garcom, "Garcom", "garcom"), (Gerente, "Gerente", "gerente"), (Sem, "Sem troca", "sem"), (Outro, "Outro garcom", "outro") })
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

    private sealed record Tokens(string Dono, string Garcom, string Gerente, string Sem, string Outro, string DonoB);

    private sealed record MesaBody(Guid Id, Guid UnidadeId, string Identificacao, int? Capacidade, bool Ativa, string Status, Guid? AreaId);

    private sealed record AreaBody(Guid Id, Guid UnidadeId, string Nome, bool Ativa);

    private sealed record MesaMapa(Guid Id, string Identificacao, int? Capacidade, Guid? AreaId, string Status, int ComandasAbertas, DateTimeOffset? AbertaDesde);

    private sealed record AreaMapa(Guid Id, string Nome, List<MesaMapa> Mesas);

    private sealed record Mapa(List<AreaMapa> Areas, List<MesaMapa> SemArea);

    private sealed record ComandaBody(Guid Id, int Numero, string Identificacao, Guid? MesaId, string Estado, int Versao, DateTimeOffset AbertaEm);

    private sealed record Erro(string Codigo, int? VersaoAtual = null);

    /// <summary>
    /// Waiter: sees tables, opens/lists comandas and moves them. Manager: manages tables and areas, sees tables. "Sem": opens and sees but has no move permission. "Outro": a second waiter
    /// with the move permission but without seeing other waiters' comandas.
    /// </summary>
    private async Task<Tokens> SeedAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        async Task<Guid> Perfil(string nome, params string[] permissoes)
        {
            var r = await perfis.CriarAsync(request, nome, [.. permissoes.Select(p => new RegraDeAcesso(RuleEffect.Allow, p))]);
            Assert.Equal(ResultadoGestao.Ok, r.Resultado);
            return r.Id!.Value;
        }

        var garcom = await Perfil("Garcom", "mesa:ver", "comanda:abrir", "comanda:ver", "comanda:transferir-mesa");
        var gerente = await Perfil("Gerente de mesas", "mesa:gerenciar", "mesa:ver");
        var sem = await Perfil("Sem troca", "mesa:ver", "comanda:abrir", "comanda:ver");
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Garcom, garcom));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Outro, garcom));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Gerente, gerente));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Sem, sem));
        var sessoes = api.Services.GetRequiredService<Sessoes>();
        var client = new ClienteDaSessao("127.0.0.1", "testes");

        async Task<string> Token(Guid user, Guid org)
        {
            var versao = (await QueryAsync(org, org == OrgA ? UnitA : UnitB, $"SELECT versao_credencial FROM contas WHERE id = '{user:D}'", r => r.GetInt32(0))).Single();
            return (await sessoes.IniciarAsync(user, org, versao, false, client)).AccessToken;
        }

        return new Tokens(await Token(DonoA, OrgA), await Token(Garcom, OrgA), await Token(Gerente, OrgA), await Token(Sem, OrgA), await Token(Outro, OrgA), await Token(DonoB, OrgB));
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

    private static async Task<MesaBody> CriarMesaAsync(HttpClient client, string token, string nome, Guid? areaId = null)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/mesas", token, new { identificacao = nome, areaId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MesaBody>())!;
    }

    private static async Task<AreaBody> CriarAreaAsync(HttpClient client, string token, string nome)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/mesas/areas", token, new { nome });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AreaBody>())!;
    }

    private static async Task<ComandaBody> AbrirAsync(HttpClient client, string token, Guid mesaId)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/comandas", token, new { mesaId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ComandaBody>())!;
    }

    private static async Task<Mapa> MapaAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/mesas/mapa", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Mapa>())!;
    }

    private static Task<HttpResponseMessage> TrocarAsync(HttpClient client, string? token, Guid comandaId, Guid mesaId, int versao) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comandaId:D}/trocar-mesa", token, new { mesaId, versao });

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

    private async Task ExecuteAsync(Guid org, Guid unit, string sql)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
        await Exec(tx, sql);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task The_map_groups_tables_by_area_and_derives_status_count_and_first_opening_from_the_open_comandas()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var varanda = await CriarAreaAsync(client, t.Gerente, "Varanda");
        var salao = await CriarAreaAsync(client, t.Gerente, "Salao");
        var m1 = await CriarMesaAsync(client, t.Gerente, "1", salao.Id);
        var m2 = await CriarMesaAsync(client, t.Gerente, "2", salao.Id);
        var m3 = await CriarMesaAsync(client, t.Gerente, "Varanda 3", varanda.Id);
        var solta = await CriarMesaAsync(client, t.Gerente, "Balcao");
        var primeira = await AbrirAsync(client, t.Garcom, m1.Id);
        var segunda = await AbrirAsync(client, t.Outro, m1.Id);
        var deOutra = await AbrirAsync(client, t.Garcom, m3.Id);

        var mapa = await MapaAsync(client, t.Sem);

        Assert.Equal(["Salao", "Varanda"], mapa.Areas.Select(a => a.Nome));
        var doSalao = mapa.Areas[0].Mesas;
        Assert.Equal(["1", "2"], doSalao.Select(m => m.Identificacao));
        Assert.Equal(("ocupada", 2), (doSalao[0].Status, doSalao[0].ComandasAbertas));
        Assert.True(segunda.AbertaEm >= primeira.AbertaEm);
        Assert.InRange(Math.Abs((doSalao[0].AbertaDesde!.Value - primeira.AbertaEm).TotalMilliseconds), 0, 1);
        Assert.Equal(("livre", 0, (DateTimeOffset?)null), (doSalao[1].Status, doSalao[1].ComandasAbertas, doSalao[1].AbertaDesde));
        var daVaranda = Assert.Single(mapa.Areas[1].Mesas);
        Assert.Equal(("ocupada", 1), (daVaranda.Status, daVaranda.ComandasAbertas));
        Assert.InRange(Math.Abs((daVaranda.AbertaDesde!.Value - deOutra.AbertaEm).TotalMilliseconds), 0, 1);
        var sem = Assert.Single(mapa.SemArea);
        Assert.Equal((solta.Id, "livre", (Guid?)null), (sem.Id, sem.Status, sem.AreaId));
        Assert.Equal(m2.Id, doSalao[1].Id);
    }

    [Fact]
    public async Task Closing_the_last_open_comanda_frees_the_table_again_because_status_is_never_stored()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await CriarMesaAsync(client, t.Gerente, "9");
        var comanda = await AbrirAsync(client, t.Garcom, mesa.Id);
        Assert.Equal("ocupada", Assert.Single((await MapaAsync(client, t.Sem)).SemArea).Status);

        await ExecuteAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'fechada', fechada_em = now() WHERE id = '{comanda.Id:D}'");

        var livre = Assert.Single((await MapaAsync(client, t.Sem)).SemArea);
        Assert.Equal(("livre", 0, (DateTimeOffset?)null), (livre.Status, livre.ComandasAbertas, livre.AbertaDesde));
        var columns = await QueryAsync(OrgA, UnitA, "SELECT column_name FROM information_schema.columns WHERE table_name IN ('mesas', 'areas')", r => r.GetString(0));
        Assert.DoesNotContain(columns, c => c.Contains("status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_retired_area_leaves_the_map_and_its_tables_show_without_an_area_and_can_be_renamed_back()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var area = await CriarAreaAsync(client, t.Gerente, "Fundos");
        var mesa = await CriarMesaAsync(client, t.Gerente, "5", area.Id);

        using var retire = await SendAsync(client, HttpMethod.Put, $"/mesas/areas/{area.Id:D}", t.Gerente, new { nome = "Fundos", ativa = false });

        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        var mapa = await MapaAsync(client, t.Sem);
        Assert.Empty(mapa.Areas);
        Assert.Equal(mesa.Id, Assert.Single(mapa.SemArea).Id);
        using var rename = await SendAsync(client, HttpMethod.Put, $"/mesas/areas/{area.Id:D}", t.Gerente, new { nome = "  Quintal ", ativa = true });
        Assert.Equal("Quintal", (await rename.Content.ReadFromJsonAsync<AreaBody>())!.Nome);
        Assert.Equal(mesa.Id, Assert.Single(Assert.Single((await MapaAsync(client, t.Sem)).Areas).Mesas).Id);
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'area:alterar'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Creating_an_area_audits_it_and_a_duplicate_name_in_the_unit_is_refused_ignoring_case()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var area = await CriarAreaAsync(client, t.Gerente, "  Varanda  Alta ");

        Assert.Equal(("Varanda Alta", true, UnitA), (area.Nome, area.Ativa, area.UnidadeId));
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_id, depois->>'nome' FROM auditoria_registros WHERE acao = 'area:criar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3))));
        Assert.Equal((Gerente, "sucesso", area.Id.ToString("D"), "Varanda Alta"), audit);
        using var duplicate = await SendAsync(client, HttpMethod.Post, "/mesas/areas", t.Gerente, new { nome = "varanda alta" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("ja-existe", (await duplicate.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        var other = await CriarAreaAsync(client, t.Gerente, "Salao");
        using var clash = await SendAsync(client, HttpMethod.Put, $"/mesas/areas/{other.Id:D}", t.Gerente, new { nome = "VARANDA ALTA", ativa = true });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM areas", r => r.GetInt64(0))).Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_area_without_a_usable_name_is_a_400(string? nome)
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var response = await SendAsync(client, HttpMethod.Post, "/mesas/areas", t.Gerente, new { nome });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalido", (await response.Content.ReadFromJsonAsync<Erro>())!.Codigo);
    }

    [Fact]
    public async Task Managing_areas_needs_mesa_gerenciar_and_a_session_and_reading_the_map_needs_mesa_ver()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var area = await CriarAreaAsync(client, t.Gerente, "Salao");
        var mesa = await CriarMesaAsync(client, t.Gerente, "1");

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "/mesas/areas", new { nome = "X" }),
                     (HttpMethod.Put, $"/mesas/areas/{area.Id:D}", new { nome = "Y", ativa = true }),
                     (HttpMethod.Put, $"/mesas/{mesa.Id:D}/area", new { areaId = area.Id }),
                 })
        {
            using var denied = await SendAsync(client, method, path, t.Garcom, body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Empty(await denied.Content.ReadAsStringAsync());
            using var anonymous = await SendAsync(client, method, path, null, body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using var noMap = await SendAsync(client, HttpMethod.Get, "/mesas/mapa", null);
        Assert.Equal(HttpStatusCode.Unauthorized, noMap.StatusCode);
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM areas", r => r.GetInt64(0))).Single());
        Assert.Null((await QueryAsync(OrgA, UnitA, $"SELECT area_id FROM mesas WHERE id = '{mesa.Id:D}'", r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0))).Single());
    }

    [Fact]
    public async Task A_table_is_placed_in_and_taken_out_of_an_area_with_an_audit_record_and_an_unknown_area_is_a_404()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var area = await CriarAreaAsync(client, t.Gerente, "Salao");
        var mesa = await CriarMesaAsync(client, t.Gerente, "1");

        using var put = await SendAsync(client, HttpMethod.Put, $"/mesas/{mesa.Id:D}/area", t.Gerente, new { areaId = area.Id });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(area.Id, Assert.Single(Assert.Single((await MapaAsync(client, t.Sem)).Areas).Mesas).AreaId);

        using var unknown = await SendAsync(client, HttpMethod.Put, $"/mesas/{mesa.Id:D}/area", t.Gerente, new { areaId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var noTable = await SendAsync(client, HttpMethod.Put, $"/mesas/{Guid.NewGuid():D}/area", t.Gerente, new { areaId = area.Id });
        Assert.Equal(HttpStatusCode.NotFound, noTable.StatusCode);
        using var take = await SendAsync(client, HttpMethod.Put, $"/mesas/{mesa.Id:D}/area", t.Gerente, new { areaId = (Guid?)null });
        Assert.Equal(HttpStatusCode.NoContent, take.StatusCode);
        Assert.Equal(mesa.Id, Assert.Single((await MapaAsync(client, t.Sem)).SemArea).Id);
        var audits = await QueryAsync(OrgA, UnitA, "SELECT antes->>'areaId', depois->>'areaId' FROM auditoria_registros WHERE acao = 'mesa:definir-area' ORDER BY id", r => (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        Assert.Equal([((string?)null, (string?)area.Id.ToString("D")), (area.Id.ToString("D"), null)], audits);
    }

    [Fact]
    public async Task Creating_a_table_with_an_unknown_or_retired_area_is_a_404_and_nothing_is_stored()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var area = await CriarAreaAsync(client, t.Gerente, "Velha");
        using var retire = await SendAsync(client, HttpMethod.Put, $"/mesas/areas/{area.Id:D}", t.Gerente, new { nome = "Velha", ativa = false });
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);

        foreach (var areaId in new[] { Guid.NewGuid(), area.Id })
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/mesas", t.Gerente, new { identificacao = "Z", areaId });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM mesas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Another_organization_sees_neither_the_areas_nor_the_tables_and_cannot_change_them()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var area = await CriarAreaAsync(client, t.Gerente, "Salao");
        var mesa = await CriarMesaAsync(client, t.Gerente, "1", area.Id);

        var mapaB = await MapaAsync(client, t.DonoB);

        Assert.Empty(mapaB.Areas);
        Assert.Empty(mapaB.SemArea);
        using var rename = await SendAsync(client, HttpMethod.Put, $"/mesas/areas/{area.Id:D}", t.DonoB, new { nome = "Roubada", ativa = false });
        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        using var place = await SendAsync(client, HttpMethod.Put, $"/mesas/{mesa.Id:D}/area", t.DonoB, new { areaId = (Guid?)null });
        Assert.Equal(HttpStatusCode.NotFound, place.StatusCode);
        var kept = Assert.Single((await MapaAsync(client, t.Sem)).Areas);
        Assert.Equal(("Salao", mesa.Id), (kept.Nome, Assert.Single(kept.Mesas).Id));
        Assert.Empty(await QueryAsync(OrgB, UnitB, "SELECT 1 FROM areas", r => r.GetInt32(0)));
    }

    [Fact]
    public async Task The_database_refuses_a_table_in_an_area_of_a_sibling_unit()
    {
        await using var api = new Api(_appConnection);
        _ = api.CreateClient();
        var areaOutraUnidade = Guid.CreateVersion7();
        await ExecuteAsync(OrgA, UnitA2, $"INSERT INTO areas (id, organizacao_id, unidade_id, nome, criada_em) VALUES ('{areaOutraUnidade:D}', '{OrgA:D}', '{UnitA2:D}', 'Praia', now())");

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(OrgA, UnitA, $"INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, criada_em, area_id) VALUES ('{Guid.CreateVersion7():D}', '{OrgA:D}', '{UnitA:D}', 'X', now(), '{areaOutraUnidade:D}')"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, erro.SqlState);
    }

    [Fact]
    public async Task With_tables_turned_off_the_map_is_empty_without_error_and_area_writes_are_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var area = await CriarAreaAsync(client, t.Gerente, "Salao");
        await CriarMesaAsync(client, t.Gerente, "1", area.Id);
        var configuracoes = api.Services.GetRequiredService<Configuracoes>();
        Assert.Equal(ResultadoConfiguracao.Ok, await configuracoes.AlterarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), new AlterarConfiguracao(OperacaoSettings.UsarMesas.Key, CamadaDeEscrita.Unidade, "false", null, null, null)));

        var mapa = await MapaAsync(client, t.Garcom);

        Assert.Empty(mapa.Areas);
        Assert.Empty(mapa.SemArea);
        using var create = await SendAsync(client, HttpMethod.Post, "/mesas/areas", t.Gerente, new { nome = "Nova" });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.Equal("mesas-desligadas", (await create.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM areas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Moving_a_comanda_changes_its_table_bumps_the_version_audits_and_publishes_the_event_and_the_map_follows()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var de = await CriarMesaAsync(client, t.Gerente, "1");
        var para = await CriarMesaAsync(client, t.Gerente, "2");
        var comanda = await AbrirAsync(client, t.Garcom, de.Id);
        Assert.Equal("1", comanda.Identificacao);

        using var response = await TrocarAsync(client, t.Garcom, comanda.Id, para.Id, comanda.Versao);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movida = (await response.Content.ReadFromJsonAsync<ComandaBody>())!;
        Assert.Equal((para.Id, "2", comanda.Versao + 1, "aberta"), (movida.MesaId, movida.Identificacao, movida.Versao, movida.Estado));
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT mesa_id, identificacao, versao FROM comandas WHERE id = '{comanda.Id:D}'", r => (r.GetGuid(0), r.GetString(1), r.GetInt32(2))));
        Assert.Equal((para.Id, "2", comanda.Versao + 1), row);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_id, antes->>'mesaId', depois->>'mesaId', depois->>'identificacao' FROM auditoria_registros WHERE acao = 'comanda:transferir-mesa'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5))));
        Assert.Equal((Garcom, "sucesso", comanda.Id.ToString("D"), de.Id.ToString("D"), para.Id.ToString("D"), "2"), audit);
        var evento = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT payload->>'MesaDeId', payload->>'MesaParaId' FROM outbox_eventos WHERE tipo = 'comanda.transferida' AND payload->>'ComandaId' = '{comanda.Id:D}'", r => (r.GetString(0), r.GetString(1))));
        Assert.Equal((de.Id.ToString("D"), para.Id.ToString("D")), evento);
        var mapa = await MapaAsync(client, t.Sem);
        Assert.Equal(["livre", "ocupada"], mapa.SemArea.Select(m => m.Status));
    }

    [Fact]
    public async Task A_comanda_with_its_own_name_keeps_it_when_it_changes_table()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var para = await CriarMesaAsync(client, t.Gerente, "8");
        using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", t.Garcom, new { identificacao = "Aniversario" });
        var comanda = (await aberta.Content.ReadFromJsonAsync<ComandaBody>())!;

        using var response = await TrocarAsync(client, t.Garcom, comanda.Id, para.Id, comanda.Versao);

        var movida = (await response.Content.ReadFromJsonAsync<ComandaBody>())!;
        Assert.Equal((para.Id, "Aniversario"), (movida.MesaId, movida.Identificacao));
    }

    [Fact]
    public async Task A_stale_version_is_a_409_with_the_current_one_and_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var de = await CriarMesaAsync(client, t.Gerente, "1");
        var para = await CriarMesaAsync(client, t.Gerente, "2");
        var comanda = await AbrirAsync(client, t.Garcom, de.Id);
        using var first = await TrocarAsync(client, t.Garcom, comanda.Id, para.Id, comanda.Versao);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var stale = await TrocarAsync(client, t.Garcom, comanda.Id, de.Id, comanda.Versao);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var erro = (await stale.Content.ReadFromJsonAsync<Erro>())!;
        Assert.Equal(("versao-desatualizada", comanda.Versao + 1), (erro.Codigo, erro.VersaoAtual));
        Assert.Equal(para.Id, (await QueryAsync(OrgA, UnitA, $"SELECT mesa_id FROM comandas WHERE id = '{comanda.Id:D}'", r => r.GetGuid(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:transferir-mesa'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task A_table_of_another_unit_or_organization_or_an_unknown_one_is_refused_with_a_404_and_the_comanda_stays()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var de = await CriarMesaAsync(client, t.Gerente, "1");
        var comanda = await AbrirAsync(client, t.Garcom, de.Id);
        var outraUnidade = Guid.CreateVersion7();
        await ExecuteAsync(OrgA, UnitA2, $"INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, criada_em) VALUES ('{outraUnidade:D}', '{OrgA:D}', '{UnitA2:D}', 'Praia 1', now())");
        var outraOrganizacao = Guid.CreateVersion7();
        await ExecuteAsync(OrgB, UnitB, $"INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, criada_em) VALUES ('{outraOrganizacao:D}', '{OrgB:D}', '{UnitB:D}', 'B1', now())");

        foreach (var mesaId in new[] { outraUnidade, outraOrganizacao, Guid.NewGuid() })
        {
            using var response = await TrocarAsync(client, t.Garcom, comanda.Id, mesaId, comanda.Versao);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var row = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT mesa_id, versao FROM comandas WHERE id = '{comanda.Id:D}'", r => (r.GetGuid(0), r.GetInt32(1))));
        Assert.Equal((de.Id, comanda.Versao), row);
    }

    [Fact]
    public async Task Moving_needs_comanda_transferir_mesa_and_a_session_and_other_waiters_comandas_are_invisible_without_permission()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var de = await CriarMesaAsync(client, t.Gerente, "1");
        var para = await CriarMesaAsync(client, t.Gerente, "2");
        var comanda = await AbrirAsync(client, t.Garcom, de.Id);

        using var denied = await TrocarAsync(client, t.Sem, comanda.Id, para.Id, comanda.Versao);
        using var anonymous = await TrocarAsync(client, null, comanda.Id, para.Id, comanda.Versao);
        using var invisible = await TrocarAsync(client, t.Outro, comanda.Id, para.Id, comanda.Versao);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(await denied.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, invisible.StatusCode);
        Assert.Equal(de.Id, (await QueryAsync(OrgA, UnitA, $"SELECT mesa_id FROM comandas WHERE id = '{comanda.Id:D}'", r => r.GetGuid(0))).Single());
    }

    [Fact]
    public async Task Moving_to_the_same_table_a_closed_comanda_bad_input_and_tables_turned_off_are_each_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var de = await CriarMesaAsync(client, t.Gerente, "1");
        var para = await CriarMesaAsync(client, t.Gerente, "2");
        var comanda = await AbrirAsync(client, t.Garcom, de.Id);

        using var same = await TrocarAsync(client, t.Garcom, comanda.Id, de.Id, comanda.Versao);
        using var badVersion = await TrocarAsync(client, t.Garcom, comanda.Id, para.Id, 0);
        using var noTable = await TrocarAsync(client, t.Garcom, comanda.Id, Guid.Empty, comanda.Versao);
        using var missing = await TrocarAsync(client, t.Garcom, Guid.NewGuid(), para.Id, 1);

        Assert.Equal((HttpStatusCode.Conflict, "mesma-mesa"), (same.StatusCode, (await same.Content.ReadFromJsonAsync<Erro>())!.Codigo));
        Assert.Equal(HttpStatusCode.BadRequest, badVersion.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noTable.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await ExecuteAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'fechada', fechada_em = now() WHERE id = '{comanda.Id:D}'");
        using var closed = await TrocarAsync(client, t.Garcom, comanda.Id, para.Id, comanda.Versao);
        Assert.Equal((HttpStatusCode.Conflict, "comanda-nao-aberta"), (closed.StatusCode, (await closed.Content.ReadFromJsonAsync<Erro>())!.Codigo));

        var aberta = await AbrirAsync(client, t.Garcom, de.Id);
        var configuracoes = api.Services.GetRequiredService<Configuracoes>();
        Assert.Equal(ResultadoConfiguracao.Ok, await configuracoes.AlterarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), new AlterarConfiguracao(OperacaoSettings.UsarMesas.Key, CamadaDeEscrita.Unidade, "false", null, null, null)));
        using var off = await TrocarAsync(client, t.Garcom, aberta.Id, para.Id, aberta.Versao);
        Assert.Equal((HttpStatusCode.Conflict, "mesas-desligadas"), (off.StatusCode, (await off.Content.ReadFromJsonAsync<Erro>())!.Codigo));
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:transferir-mesa'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task A_failure_after_the_move_rolls_back_the_table_the_version_and_the_audit_together()
    {
        await using var seedApi = new Api(_appConnection);
        using var seedClient = seedApi.CreateClient();
        var t = await SeedAsync(seedApi);
        var de = await CriarMesaAsync(seedClient, t.Gerente, "1");
        var para = await CriarMesaAsync(seedClient, t.Gerente, "2");
        var comanda = await AbrirAsync(seedClient, t.Garcom, de.Id);
        await using var api = new Api(_appConnection, services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IAuditLog));
            services.Remove(real);
            services.AddSingleton<IAuditLog>(p => new AuditThatThrows(ActivatorUtilities.CreateInstance<Flow4Tech.Infrastructure.Audit.PostgresAuditLog>(p)));
        });
        var troca = api.Services.GetRequiredService<GestaoDaTrocaDeMesa>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => troca.TrocarAsync(new FakeRequest(Garcom, new OrganizationContext(OrgA, UnitA)), comanda.Id, para.Id, comanda.Versao));

        var row = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT mesa_id, identificacao, versao FROM comandas WHERE id = '{comanda.Id:D}'", r => (r.GetGuid(0), r.GetString(1), r.GetInt32(2))));
        Assert.Equal((de.Id, "1", comanda.Versao), row);
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:transferir-mesa'", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'comanda.transferida'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task The_map_queries_have_the_expected_sql_with_no_organization_filter_and_the_EF_model_matches_the_database()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new MesasDbContext(tx);

        var ocupacao = PostgresMesasStore.Ocupacao(context, UnitA).ToQueryString();
        var areas = PostgresMesasStore.AreasAtivas(context, UnitA).ToQueryString();

        Assert.Contains("FROM comandas AS", ocupacao, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", ocupacao, StringComparison.Ordinal);
        Assert.Contains("'aberta'", ocupacao, StringComparison.Ordinal);
        Assert.Contains("FROM areas AS", areas, StringComparison.Ordinal);
        Assert.Contains("a.ativa", areas, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", areas, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", ocupacao + areas, StringComparison.Ordinal);
        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
