using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flow4Tech.Cardapio.Infrastructure;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
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
/// Dominio do salao, ticket 02: the minimal menu through the real authenticated route. Real PostgreSQL, application credential (RLS on), real sessions. Covers allowed, denied (403 without
/// reason, 401 without session) and cross-organization cases, the audit record and outbox event of each change, search, frequent items and the EF model against the migrated schema.
/// </summary>
public sealed class CardapioEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";

    private static readonly Guid OrgA = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid UnitA = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid DonoA = Guid.Parse("0197b000-0000-7000-8000-0000000000d1");
    private static readonly Guid Garcom = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
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

    private sealed record Tokens(string Dono, string Garcom, string DonoB);

    /// <summary>The waiter gets a custom profile with only <c>produto:ver</c>, handed by the owner (profiles are out of this slice, so tests seed them through the service).</summary>
    private async Task<Tokens> SeedAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfil = await perfis.CriarAsync(request, "Garcom", [new RegraDeAcesso(RuleEffect.Allow, "produto:ver")]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Garcom, perfil.Id!.Value));
        // Handing a profile bumps the credential version of the person (ends older sessions), so the session starts with the version read now.
        var versao = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{Garcom:D}'", r => r.GetInt32(0))).Single();
        var sessoes = api.Services.GetRequiredService<Sessoes>();
        var client = new ClienteDaSessao("127.0.0.1", "testes");
        return new Tokens(
            (await sessoes.IniciarAsync(DonoA, OrgA, 1, false, client)).AccessToken,
            (await sessoes.IniciarAsync(Garcom, OrgA, versao, false, client)).AccessToken,
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

    private sealed record Categoria(Guid Id, string Nome, bool Ativa, int Versao);

    private sealed record Produto(Guid Id, Guid CategoriaId, string Codigo, string Nome, long PrecoCentavos, string Moeda, bool Ativo, int Versao);

    private static async Task<Categoria> NewCategoryAsync(HttpClient client, string token, string nome)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/cardapio/categorias", token, new { nome });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Categoria>())!;
    }

    private static async Task<Produto> NewProductAsync(HttpClient client, string token, Guid categoria, string codigo, string nome, long preco, bool ativo = true)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", token, new { codigo, nome, precoCentavos = preco, categoriaId = categoria, ativo });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Produto>())!;
    }

    private static async Task<List<Produto>> ListAsync(HttpClient client, string path, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, path, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<Produto>>())!;
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
    public async Task A_manager_creates_a_product_that_is_stored_audited_and_published()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Cervejas");

        var produto = await NewProductAsync(client, t.Dono, categoria.Id, "ch01", "Chopp 300ml", 1250);

        Assert.Equal("CH01", produto.Codigo);
        Assert.Equal("BRL", produto.Moeda);
        Assert.Equal(1, produto.Versao);
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT codigo, preco_centavos, moeda, ativo FROM produtos", r => (r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetBoolean(3))));
        Assert.Equal(("CH01", 1250L, "BRL", true), row);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_id, depois->>'precoCentavos' FROM auditoria_registros WHERE acao = 'produto:criar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3))));
        Assert.Equal((DonoA, "sucesso", produto.Id.ToString("D"), "1250"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'cardapio.produto-criado' AND payload->>'ProdutoId' = '{produto.Id:D}'", r => r.GetInt32(0)));
    }

    [Fact]
    public async Task Altering_a_product_bumps_the_version_audits_before_and_after_and_publishes()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Porcoes");
        var produto = await NewProductAsync(client, t.Dono, categoria.Id, "BAT1", "Batata", 3000);

        using var ok = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, new { codigo = "BAT1", nome = "Batata frita", precoCentavos = 3500, categoriaId = categoria.Id, ativo = true, versao = 1 });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var novo = (await ok.Content.ReadFromJsonAsync<Produto>())!;
        Assert.Equal((3500L, 2, "Batata frita"), (novo.PrecoCentavos, novo.Versao, novo.Nome));
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT antes->>'precoCentavos', depois->>'precoCentavos' FROM auditoria_registros WHERE acao = 'produto:alterar'", r => (r.GetString(0), r.GetString(1))));
        Assert.Equal(("3000", "3500"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'cardapio.produto-alterado' AND payload->>'ProdutoId' = '{produto.Id:D}'", r => r.GetInt32(0)));

        // The editor that still holds version 1 is refused and nothing is written.
        using var stale = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, new { codigo = "BAT1", nome = "Velho", precoCentavos = 1, categoriaId = categoria.Id, ativo = true, versao = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(3500L, (await QueryAsync(OrgA, UnitA, "SELECT preco_centavos FROM produtos", r => r.GetInt64(0))).Single());
        Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT 1 FROM auditoria_registros WHERE acao = 'produto:alterar'", r => r.GetInt32(0)));
    }

    [Fact]
    public async Task A_short_code_is_unique_per_organization_and_bad_input_is_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Bebidas");
        await NewProductAsync(client, t.Dono, categoria.Id, "AGU1", "Agua", 500);

        using var duplicate = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", t.Dono, new { codigo = "agu1", nome = "Outra", precoCentavos = 100, categoriaId = categoria.Id, ativo = true });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        foreach (var bad in new object[]
        {
            new { codigo = "X1", nome = "Barato", precoCentavos = -1, categoriaId = categoria.Id, ativo = true },
            new { codigo = "X1", nome = " ", precoCentavos = 100, categoriaId = categoria.Id, ativo = true },
            new { codigo = "x 1!", nome = "Codigo ruim", precoCentavos = 100, categoriaId = categoria.Id, ativo = true },
        })
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", t.Dono, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var unknownCategory = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", t.Dono, new { codigo = "X2", nome = "Sem categoria", precoCentavos = 100, categoriaId = Guid.NewGuid(), ativo = true });
        Assert.Equal(HttpStatusCode.NotFound, unknownCategory.StatusCode);
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM produtos", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Search_finds_active_products_by_name_or_code_and_never_offers_inactive_ones()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Geral");
        await NewProductAsync(client, t.Dono, categoria.Id, "CAI1", "Caipirinha", 2200);
        await NewProductAsync(client, t.Dono, categoria.Id, "CAF1", "Cafe", 600);
        await NewProductAsync(client, t.Dono, categoria.Id, "DES50", "Desconto 50% casa", 100);
        var antigo = await NewProductAsync(client, t.Dono, categoria.Id, "CAI0", "Caipirinha antiga", 1900, ativo: false);

        Assert.Equal(["Caipirinha"], (await ListAsync(client, "/cardapio/produtos/busca?q=caipi", t.Garcom)).Select(p => p.Nome));
        Assert.Equal(["Cafe"], (await ListAsync(client, "/cardapio/produtos/busca?q=caf1", t.Garcom)).Select(p => p.Nome));
        Assert.Equal(["Desconto 50% casa"], (await ListAsync(client, "/cardapio/produtos/busca?q=50%25", t.Garcom)).Select(p => p.Nome));
        Assert.Empty(await ListAsync(client, "/cardapio/produtos/busca?q=nao-existe", t.Garcom));
        Assert.DoesNotContain(await ListAsync(client, "/cardapio/produtos", t.Garcom), p => p.Id == antigo.Id);
        Assert.Contains(await ListAsync(client, "/cardapio/produtos/gestao", t.Dono), p => p.Id == antigo.Id);
    }

    [Fact]
    public async Task Frequent_items_are_the_units_most_launched_active_products_and_empty_without_history()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Frequentes");
        var a = await NewProductAsync(client, t.Dono, categoria.Id, "F1", "Primeiro", 100);
        var b = await NewProductAsync(client, t.Dono, categoria.Id, "F2", "Segundo", 100);
        var inativo = await NewProductAsync(client, t.Dono, categoria.Id, "F3", "Inativo", 100);

        Assert.Empty(await ListAsync(client, "/cardapio/produtos/frequentes", t.Garcom));

        // History is written by the comanda module (later ticket); here it is seeded in the unit's own context.
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using (var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA)))
        {
            foreach (var (produto, n) in new[] { (a, 3L), (b, 9L), (inativo, 20L) })
            {
                await Exec(tx, "INSERT INTO produtos_frequencia (organizacao_id, unidade_id, produto_id, lancamentos) VALUES (@o, @u, @p, @n)", ("o", OrgA), ("u", UnitA), ("p", produto.Id), ("n", n));
            }

            await tx.CommitAsync();
        }

        using var off = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{inativo.Id}", t.Dono, new { codigo = "F3", nome = "Inativo", precoCentavos = 100, categoriaId = categoria.Id, ativo = false, versao = 1 });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        Assert.Equal([b.Id, a.Id], (await ListAsync(client, "/cardapio/produtos/frequentes", t.Garcom)).Select(p => p.Id));
    }

    [Fact]
    public async Task A_waiter_reads_the_menu_but_cannot_change_it_and_no_session_is_401()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Leitura");
        var produto = await NewProductAsync(client, t.Dono, categoria.Id, "L1", "Leitura", 100);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/cardapio/categorias", t.Garcom)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/cardapio/produtos", t.Garcom)).StatusCode);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/cardapio/categorias", new { nome = "Nova" }),
            (HttpMethod.Put, $"/cardapio/categorias/{categoria.Id}", new { nome = "Mudou", ativa = true, versao = 1 }),
            (HttpMethod.Post, "/cardapio/produtos", new { codigo = "N1", nome = "Novo", precoCentavos = 1, categoriaId = categoria.Id, ativo = true }),
            (HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", new { codigo = "L1", nome = "Mudou", precoCentavos = 1, categoriaId = categoria.Id, ativo = true, versao = 1 }),
            (HttpMethod.Get, "/cardapio/produtos/gestao", null),
        })
        {
            using var denied = await SendAsync(client, method, path, t.Garcom, body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(string.Empty, await denied.Content.ReadAsStringAsync());
            using var anonymous = await SendAsync(client, method, path, null, body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        Assert.Equal(100L, (await QueryAsync(OrgA, UnitA, "SELECT preco_centavos FROM produtos", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task One_organization_never_sees_or_changes_the_menu_of_another()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoriaA = await NewCategoryAsync(client, t.Dono, "Da A");
        var produtoA = await NewProductAsync(client, t.Dono, categoriaA.Id, "SAME", "Produto da A", 100);

        Assert.Empty(await ListAsync(client, "/cardapio/produtos/gestao", t.DonoB));
        using var categorias = await SendAsync(client, HttpMethod.Get, "/cardapio/categorias", t.DonoB);
        Assert.Empty((await categorias.Content.ReadFromJsonAsync<List<Categoria>>())!);
        using var change = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produtoA.Id}", t.DonoB, new { codigo = "SAME", nome = "Invadido", precoCentavos = 1, categoriaId = categoriaA.Id, ativo = true, versao = 1 });
        Assert.Equal(HttpStatusCode.NotFound, change.StatusCode);
        using var foreignCategory = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", t.DonoB, new { codigo = "ZZ", nome = "Usa categoria alheia", precoCentavos = 1, categoriaId = categoriaA.Id, ativo = true });
        Assert.Equal(HttpStatusCode.NotFound, foreignCategory.StatusCode);

        // The same short code is free in the other organization.
        var categoriaB = await NewCategoryAsync(client, t.DonoB, "Da B");
        await NewProductAsync(client, t.DonoB, categoriaB.Id, "SAME", "Produto da B", 200);
        Assert.Equal(["Produto da A"], (await ListAsync(client, "/cardapio/produtos/gestao", t.Dono)).Select(p => p.Nome));
        Assert.Equal(100L, (await QueryAsync(OrgA, UnitA, "SELECT preco_centavos FROM produtos", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, $"SELECT count(*) FROM produtos WHERE organizacao_id <> '{OrgB:D}'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task The_menu_EF_model_matches_the_migrated_database()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new CardapioDbContext(tx);

        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
