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
public sealed class PerfisEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";

    private static readonly Guid OrgA = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid UnitA = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid UnitA2 = Guid.Parse("0197b000-0000-7000-8000-0000000000a2");
    private static readonly Guid DonoA = Guid.Parse("0197b000-0000-7000-8000-0000000000d1");
    private static readonly Guid Garcom = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
    private static readonly Guid Alvo = Guid.Parse("0197b000-0000-7000-8000-0000000000c3");
    private static readonly Guid Gerente = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");
    private static readonly Guid ContaB = Guid.Parse("0197b000-0000-7000-8000-0000000000e2");
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
                foreach (var (id, nome, usuario) in new[] { (Garcom, "Garcom", "garcom"), (Gerente, "Gestor", "gestor"), (Alvo, "Alvo", "alvo") })
                {
                    await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', @n, @s, now())", ("i", id), ("o", org), ("n", nome), ("s", usuario));
                    await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", id), ("n", unit));
                }
            }

            if (org == OrgB)
            {
                await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', 'ContaB', 'contab', now())", ("i", ContaB), ("o", org));
                await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", ContaB), ("n", unit));
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

    private sealed record ModeloBody(string Chave, string Nome, bool Criado, Guid? PerfilId);

    private sealed record RegraBody(string Efeito, string Permissao);

    private sealed record PerfilBody(Guid Id, string Nome, List<RegraBody> Regras);

    private sealed record DaContaBody(List<PerfilBody> Perfis, List<RegraBody> Overrides);

    private sealed record Erro(string Codigo);

    private async Task<string> TokenAsync(Api api, Guid user, Guid org, Guid unit)
    {
        var version = (await QueryAsync(org, unit, $"SELECT versao_credencial FROM contas WHERE id = '{user:D}'", r => r.GetInt32(0))).Single();
        return (await api.Services.GetRequiredService<Sessoes>().IniciarAsync(user, org, version, false, new ClienteDaSessao("127.0.0.1", "testes"))).AccessToken;
    }

    private Task<int> VersionAsync(Guid user) =>
        QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{user:D}'", r => r.GetInt32(0)).ContinueWith(t => t.Result.Single(), TaskScheduler.Default);

    /// <summary>A custom profile with exactly these permissions, given to <paramref name="user"/> by the owner (through the service, as the other suites do).</summary>
    private static async Task GiveAsync(Api api, Guid user, string name, params string[] permissions)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var created = await perfis.CriarAsync(request, name, [.. permissions.Select(p => new RegraDeAcesso(RuleEffect.Allow, p))]);
        Assert.Equal(ResultadoGestao.Ok, created.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, user, created.Id!.Value));
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

    private Task<List<long>> CountAsync(Guid org, Guid unit, string sql) => QueryAsync(org, unit, sql, r => r.GetInt64(0));

    private static async Task<List<ModeloBody>> CreateModelsAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/perfis/modelos", token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ModeloBody>>())!;
    }

    private static async Task AssertForbiddenEmptyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_owner_creates_the_three_models_in_one_call_audited_and_repeating_is_harmless()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);

        var first = await CreateModelsAsync(client, dono);

        Assert.Equal(["Garçom", "Caixa", "Gerente"], first.Select(m => m.Nome));
        Assert.All(first, m => Assert.True(m.Criado && m.PerfilId is not null));
        var names = await QueryAsync(OrgA, UnitA, "SELECT nome FROM perfis ORDER BY nome", r => r.GetString(0));
        Assert.Equal(["Caixa", "Garçom", "Gerente"], names);
        var audit = await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado FROM auditoria_registros WHERE acao = 'perfil:criar'", r => (r.GetGuid(0), r.GetString(1)));
        Assert.Equal(3, audit.Count);
        Assert.All(audit, a => Assert.Equal((DonoA, "sucesso"), a));

        var second = await CreateModelsAsync(client, dono);

        Assert.All(second, m => Assert.False(m.Criado));
        Assert.Equal(3, Assert.Single(await CountAsync(OrgA, UnitA, "SELECT count(*) FROM perfis")));
        Assert.Equal(3, Assert.Single(await CountAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'perfil:criar'")));
    }

    [Fact]
    public async Task Model_profiles_carry_the_data_defined_permissions_and_the_listing_shows_them()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        await CreateModelsAsync(client, dono);

        using var response = await SendAsync(client, HttpMethod.Get, "/perfis", dono);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var perfis = (await response.Content.ReadFromJsonAsync<List<PerfilBody>>())!;
        var catalog = api.Services.GetRequiredService<PolicySet>().KnownPermissions;
        foreach (var modelo in ModelosDePerfil.Todos)
        {
            var perfil = Assert.Single(perfis, p => p.Nome == modelo.Nome);
            Assert.Equal(modelo.Permissoes.Where(catalog.Contains).Order(StringComparer.Ordinal), perfil.Regras.Select(r => r.Permissao).Order(StringComparer.Ordinal));
            Assert.All(perfil.Regras, r => Assert.Equal("allow", r.Efeito));
        }

        // The cashier takes the money, the waiter does not; the manager can do both and manage tables, but not profiles.
        var garcom = perfis.Single(p => p.Nome == "Garçom").Regras.Select(r => r.Permissao).ToList();
        var gerente = perfis.Single(p => p.Nome == "Gerente").Regras.Select(r => r.Permissao).ToList();
        Assert.DoesNotContain("comanda:receber-pagamento", garcom);
        Assert.Contains("comanda:receber-pagamento", gerente);
        Assert.DoesNotContain(gerente, p => p.StartsWith("perfil:", StringComparison.Ordinal) || p == "permissao:atribuir");
    }

    [Fact]
    public async Task Each_new_permission_is_asked_by_its_route_matrix_owner_holder_non_holder_and_no_session()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        await GiveAsync(api, Gerente, "So ver perfis", "perfil:ver");
        await GiveAsync(api, Garcom, "So gerir perfis", "perfil:gerir");
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var soVer = await TokenAsync(api, Gerente, OrgA, UnitA);
        var soGerir = await TokenAsync(api, Garcom, OrgA, UnitA);
        var sobre = $"/contas/{Alvo:D}/perfis";

        // perfil:ver -> GET /perfis and GET /contas/{id}/perfis
        foreach (var path in new[] { "/perfis", sobre })
        {
            using var asOwner = await SendAsync(client, HttpMethod.Get, path, dono);
            using var asHolder = await SendAsync(client, HttpMethod.Get, path, soVer);
            using var asOther = await SendAsync(client, HttpMethod.Get, path, soGerir);
            using var anonymous = await SendAsync(client, HttpMethod.Get, path, null);
            Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
            Assert.Equal(HttpStatusCode.OK, asHolder.StatusCode);
            await AssertForbiddenEmptyAsync(asOther);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        // perfil:gerir -> POST /perfis/modelos
        using (var asHolder = await SendAsync(client, HttpMethod.Post, "/perfis/modelos", soGerir))
        {
            // The holder has only perfil:gerir, so handing the models' permissions on is refused as escalation (still 403, nothing created).
            await AssertForbiddenEmptyAsync(asHolder);
        }

        using (var asNonHolder = await SendAsync(client, HttpMethod.Post, "/perfis/modelos", soVer))
        {
            await AssertForbiddenEmptyAsync(asNonHolder);
        }

        using (var anonymous = await SendAsync(client, HttpMethod.Post, "/perfis/modelos", null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        Assert.Equal(0, Assert.Single(await CountAsync(OrgA, UnitA, "SELECT count(*) FROM perfis WHERE nome IN ('Garçom','Caixa','Gerente')")));

        // permissao:atribuir -> PUT/DELETE assignment and PUT overrides: the owner may; the one with perfil:ver / perfil:gerir only may not.
        var perfilId = (await CreateModelsAsync(client, dono)).First().PerfilId!.Value;
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Put, $"{sobre}/{perfilId:D}", null),
            (HttpMethod.Delete, $"{sobre}/{perfilId:D}", null),
            (HttpMethod.Put, $"/contas/{Alvo:D}/overrides", new { regras = Array.Empty<object>() }),
        })
        {
            using var asOwner = await SendAsync(client, method, path, dono, body);
            using var asNonHolder = await SendAsync(client, method, path, soVer, body);
            using var asOtherNonHolder = await SendAsync(client, method, path, soGerir, body);
            using var anonymous = await SendAsync(client, method, path, null, body);
            Assert.Equal(HttpStatusCode.NoContent, asOwner.StatusCode);
            await AssertForbiddenEmptyAsync(asNonHolder);
            await AssertForbiddenEmptyAsync(asOtherNonHolder);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }

    [Fact]
    public async Task A_holder_of_perfil_gerir_who_lacks_what_a_model_carries_is_refused_and_the_refusal_is_audited()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        await GiveAsync(api, Gerente, "Gestor de perfis", "perfil:gerir", "mesa:ver");
        var gestor = await TokenAsync(api, Gerente, OrgA, UnitA);

        using var response = await SendAsync(client, HttpMethod.Post, "/perfis/modelos", gestor);

        await AssertForbiddenEmptyAsync(response);
        var denied = await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado FROM auditoria_registros WHERE acao = 'permissao:negar-escalada'", r => (r.GetGuid(0), r.GetString(1)));
        Assert.Equal((Gerente, "negado"), Assert.Single(denied));
    }

    [Fact]
    public async Task Assigning_and_removing_a_profile_bumps_the_credential_version_and_ends_the_session_over_http()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var waiter = (await CreateModelsAsync(client, dono)).Single(m => m.Chave == "garcom").PerfilId!.Value;
        var alvoToken = await TokenAsync(api, Alvo, OrgA, UnitA);
        using (var before = await SendAsync(client, HttpMethod.Get, "/perfis", alvoToken))
        {
            await AssertForbiddenEmptyAsync(before);
        }

        var v0 = await VersionAsync(Alvo);
        using (var assigned = await SendAsync(client, HttpMethod.Put, $"/contas/{Alvo:D}/perfis/{waiter:D}", dono))
        {
            Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        }

        var v1 = await VersionAsync(Alvo);
        Assert.True(v1 > v0);
        using (var ended = await SendAsync(client, HttpMethod.Get, "/sessao/permissoes", alvoToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        }

        var fresh = await TokenAsync(api, Alvo, OrgA, UnitA);
        using (var permissions = await SendAsync(client, HttpMethod.Get, "/sessao/permissoes", fresh))
        {
            Assert.Contains("comanda:lancar-item", await permissions.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var listed = await SendAsync(client, HttpMethod.Get, $"/contas/{Alvo:D}/perfis", dono))
        {
            var body = (await listed.Content.ReadFromJsonAsync<DaContaBody>())!;
            Assert.Equal(waiter, Assert.Single(body.Perfis).Id);
        }

        using (var removed = await SendAsync(client, HttpMethod.Delete, $"/contas/{Alvo:D}/perfis/{waiter:D}", dono))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.True(await VersionAsync(Alvo) > v1);
        using (var ended = await SendAsync(client, HttpMethod.Get, "/sessao/permissoes", fresh))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        }

        var audited = await QueryAsync(OrgA, UnitA, "SELECT acao FROM auditoria_registros WHERE acao IN ('perfil:atribuir','perfil:remover-atribuicao') ORDER BY acao", r => r.GetString(0));
        Assert.Equal(["perfil:atribuir", "perfil:remover-atribuicao"], audited);
    }

    [Fact]
    public async Task Setting_overrides_bumps_the_version_is_audited_and_unknown_inputs_are_400()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var path = $"/contas/{Alvo:D}/overrides";
        var v0 = await VersionAsync(Alvo);

        using (var ok = await SendAsync(client, HttpMethod.Put, path, dono, new { regras = new[] { new { efeito = "allow", permissao = "mesa:ver" } } }))
        {
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }

        Assert.True(await VersionAsync(Alvo) > v0);
        using (var listed = await SendAsync(client, HttpMethod.Get, $"/contas/{Alvo:D}/perfis", dono))
        {
            var body = (await listed.Content.ReadFromJsonAsync<DaContaBody>())!;
            Assert.Equal("mesa:ver", Assert.Single(body.Overrides).Permissao);
        }

        Assert.Equal(1, Assert.Single(await CountAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'permissao:definir-overrides'")));

        foreach (var invalid in new object[]
        {
            new { regras = new[] { new { efeito = "allow", permissao = "nao:existe" } } },
            new { regras = new[] { new { efeito = "talvez", permissao = "mesa:ver" } } },
            new { regras = new[] { new { efeito = "allow", permissao = "mesa:ver", unidadeDaRegraId = UnitB } } },
            new { regras = (object?)null },
        })
        {
            using var response = await SendAsync(client, HttpMethod.Put, path, dono, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalido", (await response.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        }
    }

    [Fact]
    public async Task Assigning_to_oneself_or_handing_on_more_than_held_is_403_without_reason_and_each_refusal_is_audited()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var waiter = (await CreateModelsAsync(client, dono)).Single(m => m.Chave == "garcom").PerfilId!.Value;
        var cashier = (await QueryAsync(OrgA, UnitA, "SELECT id FROM perfis WHERE nome = 'Caixa'", r => r.GetGuid(0))).Single();
        await GiveAsync(api, Gerente, "Gestor de atribuicoes", "permissao:atribuir", "comanda:ver", "comanda:abrir", "comanda:lancar-item", "comanda:cancelar-item", "comanda:desfazer-rodada", "mesa:ver", "produto:ver");
        var gestor = await TokenAsync(api, Gerente, OrgA, UnitA);

        using (var self = await SendAsync(client, HttpMethod.Put, $"/contas/{Gerente:D}/perfis/{waiter:D}", gestor))
        {
            await AssertForbiddenEmptyAsync(self);
        }

        using (var selfOverride = await SendAsync(client, HttpMethod.Put, $"/contas/{Gerente:D}/overrides", gestor, new { regras = Array.Empty<object>() }))
        {
            await AssertForbiddenEmptyAsync(selfOverride);
        }

        using (var more = await SendAsync(client, HttpMethod.Put, $"/contas/{Alvo:D}/perfis/{cashier:D}", gestor))
        {
            await AssertForbiddenEmptyAsync(more);
        }

        using (var moreOverride = await SendAsync(client, HttpMethod.Put, $"/contas/{Alvo:D}/overrides", gestor, new { regras = new[] { new { efeito = "allow", permissao = "caixa:abrir" } } }))
        {
            await AssertForbiddenEmptyAsync(moreOverride);
        }

        Assert.Equal(0, Assert.Single(await CountAsync(OrgA, UnitA, $"SELECT count(*) FROM usuario_perfis WHERE usuario_id IN ('{Gerente:D}','{Alvo:D}') AND perfil_id IN ('{waiter:D}','{cashier:D}')")));
        var refusals = await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado FROM auditoria_registros WHERE acao = 'permissao:negar-escalada'", r => (r.GetGuid(0), r.GetString(1)));
        Assert.Equal(4, refusals.Count);
        Assert.All(refusals, r => Assert.Equal((Gerente, "negado"), r));

        // What the holder does hold, they may hand on.
        using var allowed = await SendAsync(client, HttpMethod.Put, $"/contas/{Alvo:D}/perfis/{waiter:D}", gestor);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task A_profile_of_one_organization_is_invisible_and_unassignable_in_the_other()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var donoA = await TokenAsync(api, DonoA, OrgA, UnitA);
        var donoB = await TokenAsync(api, DonoB, OrgB, UnitB);
        var perfilA = (await CreateModelsAsync(client, donoA)).First().PerfilId!.Value;

        using (var listB = await SendAsync(client, HttpMethod.Get, "/perfis", donoB))
        {
            Assert.Empty((await listB.Content.ReadFromJsonAsync<List<PerfilBody>>())!);
        }

        // B's owner cannot give A's profile to B's account, nor touch A's account.
        using (var foreignProfile = await SendAsync(client, HttpMethod.Put, $"/contas/{ContaB:D}/perfis/{perfilA:D}", donoB))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignProfile.StatusCode);
        }

        using (var foreignAccount = await SendAsync(client, HttpMethod.Put, $"/contas/{Alvo:D}/perfis/{perfilA:D}", donoB))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignAccount.StatusCode);
        }

        using (var foreignRead = await SendAsync(client, HttpMethod.Get, $"/contas/{Alvo:D}/perfis", donoB))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        }

        using (var foreignOverride = await SendAsync(client, HttpMethod.Put, $"/contas/{Alvo:D}/overrides", donoB, new { regras = Array.Empty<object>() }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignOverride.StatusCode);
        }

        // A's owner cannot reach B's account either, and B's own models are separate rows (same names allowed in both).
        using (var toB = await SendAsync(client, HttpMethod.Put, $"/contas/{ContaB:D}/perfis/{perfilA:D}", donoA))
        {
            Assert.Equal(HttpStatusCode.NotFound, toB.StatusCode);
        }

        Assert.Equal(3, (await CreateModelsAsync(client, donoB)).Count(m => m.Criado));
        Assert.Equal(0, Assert.Single(await CountAsync(OrgB, UnitB, "SELECT count(*) FROM usuario_perfis")));
        Assert.Equal(0, Assert.Single(await CountAsync(OrgA, UnitA, "SELECT count(*) FROM usuario_perfis")));
    }
}
