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
using Microsoft.EntityFrameworkCore;
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
    private static readonly Guid UnitA2 = Guid.Parse("0197b000-0000-7000-8000-0000000000a2");
    private static readonly Guid GerenteSemPreco = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");
    private static readonly Guid GerenteComLimite = Guid.Parse("0197b000-0000-7000-8000-0000000000c3");
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
                foreach (var (gerente, usuario) in new[] { (GerenteSemPreco, "gerente1"), (GerenteComLimite, "gerente2") })
                {
                    await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', @n, @n, now())", ("i", gerente), ("o", org), ("n", usuario));
                    await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", gerente), ("n", unit));
                }
            }

            await tx.CommitAsync();
        }
    }

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>A second unit of organization A, linked to its owner. Called BEFORE the sessions start: with two units the session's active unit is chosen at login.</summary>
    private async Task AddSecondUnitAsync()
    {
        await using var ownerSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await using var tx = await new TenantDatabase(ownerSource).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Filial')", ("u", UnitA2), ("o", OrgA));
        await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", OrgA), ("u", DonoA), ("n", UnitA2));
        await tx.CommitAsync();
    }

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

    private sealed record Tokens(string Dono, string Garcom, string DonoB, string GerenteSemPreco, string GerenteComLimite);

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

        // Two managers: both manage the menu; only the second may move a price, and by at most 10 percent (a limit decided by the engine).
        var gerente = await perfis.CriarAsync(request, "Gerente sem preco", [new RegraDeAcesso(RuleEffect.Allow, "produto:ver"), new RegraDeAcesso(RuleEffect.Allow, "produto:gerenciar")]);
        var gerenteLimite = await perfis.CriarAsync(request, "Gerente com limite", [new RegraDeAcesso(RuleEffect.Allow, "produto:ver"), new RegraDeAcesso(RuleEffect.Allow, "produto:gerenciar"), new RegraDeAcesso(RuleEffect.Allow, "cardapio:preco-alterar", MaxPercent: 10m)]);
        Assert.Equal(ResultadoGestao.Ok, gerente.Resultado);
        Assert.Equal(ResultadoGestao.Ok, gerenteLimite.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, GerenteSemPreco, gerente.Id!.Value));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, GerenteComLimite, gerenteLimite.Id!.Value));
        var v1 = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{GerenteSemPreco:D}'", r => r.GetInt32(0))).Single();
        var v2 = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{GerenteComLimite:D}'", r => r.GetInt32(0))).Single();
        return new Tokens(
            (await sessoes.IniciarAsync(DonoA, OrgA, 1, false, client)).AccessToken,
            (await sessoes.IniciarAsync(Garcom, OrgA, versao, false, client)).AccessToken,
            (await sessoes.IniciarAsync(DonoB, OrgB, 1, false, client)).AccessToken,
            (await sessoes.IniciarAsync(GerenteSemPreco, OrgA, v1, false, client)).AccessToken,
            (await sessoes.IniciarAsync(GerenteComLimite, OrgA, v2, false, client)).AccessToken);
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

    private sealed record Categoria(Guid Id, string Nome, bool Ativa, int Versao, Guid? CategoriaPaiId = null);

    private sealed record PromocaoBody(long PrecoCentavos, DateTimeOffset Inicio, DateTimeOffset Fim);

    private sealed record Produto(Guid Id, Guid CategoriaId, string Codigo, string Nome, long PrecoCentavos, string Moeda, bool Ativo, int Versao, bool Indisponivel = false, PromocaoBody? Promocao = null, long? PrecoDaUnidadeCentavos = null, long PrecoEfetivoCentavos = 0);

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

    private static object ProductBody(Produto p, object? promocao = null, bool? indisponivel = null, long? preco = null, string? nome = null) => new
    {
        codigo = p.Codigo,
        nome = nome ?? p.Nome,
        precoCentavos = preco ?? p.PrecoCentavos,
        categoriaId = p.CategoriaId,
        ativo = p.Ativo,
        versao = p.Versao,
        indisponivel = indisponivel ?? p.Indisponivel,
        promocao,
    };

    private static async Task<Produto> ReadProductAsync(HttpClient client, string token, Guid id) =>
        (await ListAsync(client, "/cardapio/produtos/gestao", token)).Single(p => p.Id == id);

    [Fact]
    public async Task Categories_have_at_most_two_levels_and_the_database_keeps_that_whatever_the_writer()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var bebidas = await NewCategoryAsync(client, t.Dono, "Bebidas");
        using var created = await SendAsync(client, HttpMethod.Post, "/cardapio/categorias", t.Dono, new { nome = "Cervejas", categoriaPaiId = bebidas.Id });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var cervejas = (await created.Content.ReadFromJsonAsync<Categoria>())!;
        Assert.Equal(bebidas.Id, cervejas.CategoriaPaiId);

        // A third level, a parent that is itself a child, a category under itself, a parent with children moving down, and an unknown parent are all refused.
        using var third = await SendAsync(client, HttpMethod.Post, "/cardapio/categorias", t.Dono, new { nome = "Artesanais", categoriaPaiId = cervejas.Id });
        Assert.Equal(HttpStatusCode.BadRequest, third.StatusCode);
        Assert.Equal("nivel-invalido", (await third.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("codigo").GetString());
        var outra = await NewCategoryAsync(client, t.Dono, "Porcoes");
        using var underChild = await SendAsync(client, HttpMethod.Put, $"/cardapio/categorias/{outra.Id}", t.Dono, new { nome = "Porcoes", ativa = true, versao = 1, categoriaPaiId = cervejas.Id });
        Assert.Equal(HttpStatusCode.BadRequest, underChild.StatusCode);
        using var underSelf = await SendAsync(client, HttpMethod.Put, $"/cardapio/categorias/{outra.Id}", t.Dono, new { nome = "Porcoes", ativa = true, versao = 1, categoriaPaiId = outra.Id });
        Assert.Equal(HttpStatusCode.BadRequest, underSelf.StatusCode);
        using var parentMovesDown = await SendAsync(client, HttpMethod.Put, $"/cardapio/categorias/{bebidas.Id}", t.Dono, new { nome = "Bebidas", ativa = true, versao = 1, categoriaPaiId = outra.Id });
        Assert.Equal(HttpStatusCode.BadRequest, parentMovesDown.StatusCode);
        using var unknown = await SendAsync(client, HttpMethod.Post, "/cardapio/categorias", t.Dono, new { nome = "Fantasma", categoriaPaiId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // A top-level category can be made a child (when it has none), renamed keeping its parent, and made top level again.
        using var moved = await SendAsync(client, HttpMethod.Put, $"/cardapio/categorias/{outra.Id}", t.Dono, new { nome = "Porcoes", ativa = true, versao = 1, categoriaPaiId = bebidas.Id });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        using var top = await SendAsync(client, HttpMethod.Put, $"/cardapio/categorias/{outra.Id}", t.Dono, new { nome = "Porcoes", ativa = true, versao = 2 });
        Assert.Equal(HttpStatusCode.OK, top.StatusCode);
        Assert.Null((await top.Content.ReadFromJsonAsync<Categoria>())!.CategoriaPaiId);
        Assert.Equal(3L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM categorias", r => r.GetInt64(0))).Single());
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'cardapio.categoria-criada' AND payload->>'CategoriaPaiId' = '{bebidas.Id:D}'", r => r.GetInt32(0)));

        // Even a writer that skips the application (SQL on the application credential) cannot build a third level.
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        var error = await Assert.ThrowsAsync<PostgresException>(() => Exec(tx, "INSERT INTO categorias (id, organizacao_id, nome, categoria_pai_id, criada_em, atualizada_em) VALUES (@i, @o, 'Neta', @p, now(), now())", ("i", Guid.NewGuid()), ("o", OrgA), ("p", cervejas.Id)));
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task A_promotion_overrides_the_price_only_inside_its_window_and_a_bad_promotion_is_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Promo");
        var produto = await NewProductAsync(client, t.Dono, categoria.Id, "PR1", "Chopp", 1000);
        var agora = DateTimeOffset.UtcNow;

        using var current = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, ProductBody(produto, new { precoCentavos = 800, inicio = agora.AddHours(-1), fim = agora.AddHours(1) }));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var viva = (await current.Content.ReadFromJsonAsync<Produto>())!;
        Assert.Equal((1000L, 800L, 800L), (viva.PrecoCentavos, viva.Promocao!.PrecoCentavos, viva.PrecoEfetivoCentavos));
        Assert.Equal(800L, (await ReadProductAsync(client, t.Dono, produto.Id)).PrecoEfetivoCentavos);
        Assert.Equal(800L, (await ListAsync(client, "/cardapio/produtos", t.Garcom)).Single().PrecoEfetivoCentavos);

        using var future = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, ProductBody(viva, new { precoCentavos = 800, inicio = agora.AddHours(1), fim = agora.AddHours(2) }));
        Assert.Equal(1000L, (await future.Content.ReadFromJsonAsync<Produto>())!.PrecoEfetivoCentavos);
        var depois = await ReadProductAsync(client, t.Dono, produto.Id);
        using var past = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, ProductBody(depois, new { precoCentavos = 800, inicio = agora.AddHours(-2), fim = agora.AddHours(-1) }));
        Assert.Equal(1000L, (await past.Content.ReadFromJsonAsync<Produto>())!.PrecoEfetivoCentavos);

        // Stored in UTC whatever offset the client wrote, and removed by sending none.
        var stored = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT preco_promocional_centavos, promocao_inicio, promocao_fim FROM produtos", r => (r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetFieldValue<DateTimeOffset>(2))));
        Assert.Equal((800L, TimeSpan.Zero), (stored.Item1, stored.Item2.Offset));
        var atual = await ReadProductAsync(client, t.Dono, produto.Id);
        using var none = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, ProductBody(atual));
        Assert.Null((await none.Content.ReadFromJsonAsync<Produto>())!.Promocao);
        Assert.Null((await QueryAsync(OrgA, UnitA, "SELECT preco_promocional_centavos FROM produtos", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0))).Single());

        var fresh = await ReadProductAsync(client, t.Dono, produto.Id);
        foreach (var bad in new object[]
        {
            new { precoCentavos = 1000, inicio = agora, fim = agora.AddHours(1) },
            new { precoCentavos = 1500, inicio = agora, fim = agora.AddHours(1) },
            new { precoCentavos = -1, inicio = agora, fim = agora.AddHours(1) },
            new { precoCentavos = 500, inicio = agora, fim = agora },
            new { precoCentavos = 500, inicio = agora.AddHours(1), fim = agora },
        })
        {
            using var refused = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, ProductBody(fresh, bad));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using var onCreate = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", t.Dono, new { codigo = "PR2", nome = "Nasce em promocao", precoCentavos = 1000, categoriaId = categoria.Id, ativo = true, promocao = new { precoCentavos = 700, inicio = agora.AddMinutes(-1), fim = agora.AddMinutes(1) } });
        Assert.Equal(HttpStatusCode.Created, onCreate.StatusCode);
        Assert.Equal(700L, (await onCreate.Content.ReadFromJsonAsync<Produto>())!.PrecoEfetivoCentavos);
    }

    [Fact]
    public async Task The_unit_price_overrides_one_unit_only_and_never_leaks_to_another_unit_or_organization()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        await AddSecondUnitAsync();
        var t = await SeedAsync(api);
        using var first = await SendAsync(client, HttpMethod.Post, "/sessao/unidade-ativa", t.Dono, new { unidadeDestinoId = UnitA });
        Assert.True(first.IsSuccessStatusCode, first.StatusCode.ToString()); // with two units the session starts without an active one
        var categoria = await NewCategoryAsync(client, t.Dono, "Unidades");
        var produto = await NewProductAsync(client, t.Dono, categoria.Id, "UN1", "Caipirinha", 2000);
        var agora = DateTimeOffset.UtcNow;
        using var promo = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, ProductBody(produto, new { precoCentavos = 1800, inicio = agora.AddHours(-1), fim = agora.AddHours(1) }));
        Assert.Equal(HttpStatusCode.OK, promo.StatusCode);

        using var set = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.Dono, new { precoCentavos = 2500 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var vista = (await set.Content.ReadFromJsonAsync<Produto>())!;
        Assert.Equal((2500L, 2500L, 2000L), (vista.PrecoDaUnidadeCentavos, vista.PrecoEfetivoCentavos, vista.PrecoCentavos));
        Assert.Equal(2500L, (await ListAsync(client, "/cardapio/produtos", t.Garcom)).Single().PrecoEfetivoCentavos);
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT unidade_id, preco_centavos FROM produtos_preco_unidade", r => (r.GetGuid(0), r.GetInt64(1))));
        Assert.Equal((UnitA, 2500L), row);

        // The other unit of the same organization still pays the promotion (the override is not there).
        using var switched = await SendAsync(client, HttpMethod.Post, "/sessao/unidade-ativa", t.Dono, new { unidadeDestinoId = UnitA2 });
        Assert.True(switched.IsSuccessStatusCode, switched.StatusCode.ToString());
        var naFilial = (await ListAsync(client, "/cardapio/produtos/gestao", t.Dono)).Single();
        Assert.Equal((null, 1800L), (naFilial.PrecoDaUnidadeCentavos, naFilial.PrecoEfetivoCentavos));
        using var removeOther = await SendAsync(client, HttpMethod.Delete, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.Dono);
        Assert.Equal(HttpStatusCode.NotFound, removeOther.StatusCode); // nothing to remove in this unit; the first unit's override is untouched
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());
        using var setOther = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.Dono, new { precoCentavos = 2100 });
        Assert.Equal(HttpStatusCode.OK, setOther.StatusCode);
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());
        using var back = await SendAsync(client, HttpMethod.Post, "/sessao/unidade-ativa", t.Dono, new { unidadeDestinoId = UnitA });
        Assert.True(back.IsSuccessStatusCode);
        Assert.Equal(2500L, (await ListAsync(client, "/cardapio/produtos/gestao", t.Dono)).Single().PrecoEfetivoCentavos);

        // Another organization neither sees the product nor writes a price for it, and has no override rows.
        using var foreign = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.DonoB, new { precoCentavos = 1 });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var foreignRemove = await SendAsync(client, HttpMethod.Delete, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.DonoB);
        Assert.Equal(HttpStatusCode.NotFound, foreignRemove.StatusCode);
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());
        var produtoB = await NewProductAsync(client, t.DonoB, (await NewCategoryAsync(client, t.DonoB, "B")).Id, "UN1", "Caipirinha B", 2000);
        Assert.Null(produtoB.PrecoDaUnidadeCentavos);
        Assert.Equal(2000L, (await ListAsync(client, "/cardapio/produtos", t.DonoB)).Single().PrecoEfetivoCentavos);

        // Removing goes back to the promotion in force; audit and events record each step; bad prices are refused.
        using var removed = await SendAsync(client, HttpMethod.Delete, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.Dono);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(1800L, (await removed.Content.ReadFromJsonAsync<Produto>())!.PrecoEfetivoCentavos);
        using var negative = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.Dono, new { precoCentavos = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'produto:preco-unidade-definir'", r => r.GetInt64(0))).Single());
        var removal = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT antes->>'precoDaUnidadeCentavos', depois->>'precoDaUnidadeCentavos' FROM auditoria_registros WHERE acao = 'produto:preco-unidade-remover'", r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1))));
        Assert.Equal(("2500", null), removal);
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'cardapio.preco-unidade-definido'", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'cardapio.preco-unidade-removido'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Moving_a_price_needs_cardapio_preco_alterar_with_the_percent_limit_decided_by_the_engine()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Limites");
        var produto = await NewProductAsync(client, t.Dono, categoria.Id, "LM1", "Chopp", 1000);
        var agora = DateTimeOffset.UtcNow;

        // Without the permission: everything but a price still works; any price move is 403 with an empty body and nothing is written.
        using var rename = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.GerenteSemPreco, ProductBody(produto, nome: "Chopp 300ml", indisponivel: true));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var atual = (await rename.Content.ReadFromJsonAsync<Produto>())!;
        Assert.True(atual.Indisponivel);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", ProductBody(atual, preco: 1001)),
            (HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", ProductBody(atual, new { precoCentavos = 999, inicio = agora, fim = agora.AddHours(1) })),
            (HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", new { precoCentavos = 1001 }),
        })
        {
            using var denied = await SendAsync(client, method, path, t.GerenteSemPreco, body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(string.Empty, await denied.Content.ReadAsStringAsync());
        }

        var after = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT preco_centavos, versao, preco_promocional_centavos FROM produtos", r => (r.GetInt64(0), r.GetInt32(1), r.IsDBNull(2) ? (long?)null : r.GetInt64(2))));
        Assert.Equal((1000L, 2, (long?)null), after);
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'produto:alterar'", r => r.GetInt64(0))).Single());

        // With a 10 percent limit: 10 percent passes (edge), 11 and 50 do not, in the base price, in a promotion and in the unit price.
        using var edge = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.GerenteComLimite, ProductBody(atual, preco: 1100));
        Assert.Equal(HttpStatusCode.OK, edge.StatusCode);
        var v3 = (await edge.Content.ReadFromJsonAsync<Produto>())!;
        using var tooMuch = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.GerenteComLimite, ProductBody(v3, preco: 1222));
        Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);
        using var halved = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.GerenteComLimite, ProductBody(v3, new { precoCentavos = 550, inicio = agora, fim = agora.AddHours(1) }));
        Assert.Equal(HttpStatusCode.Forbidden, halved.StatusCode);
        using var smallPromo = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.GerenteComLimite, ProductBody(v3, new { precoCentavos = 1000, inicio = agora, fim = agora.AddHours(1) }));
        Assert.Equal(HttpStatusCode.OK, smallPromo.StatusCode);
        using var unitTooMuch = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.GerenteComLimite, new { precoCentavos = 2000 });
        Assert.Equal(HttpStatusCode.Forbidden, unitTooMuch.StatusCode);
        using var unitSmall = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.GerenteComLimite, new { precoCentavos = 1050 });
        Assert.Equal(HttpStatusCode.OK, unitSmall.StatusCode);
        var final = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT preco_centavos, preco_promocional_centavos FROM produtos", r => (r.GetInt64(0), r.GetInt64(1))));
        Assert.Equal((1100L, 1000L), final);
        Assert.Equal(1050L, (await QueryAsync(OrgA, UnitA, "SELECT preco_centavos FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());

        // The owner holds the whole catalog without a limit.
        var dono = await ReadProductAsync(client, t.Dono, produto.Id);
        using var big = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, ProductBody(dono, preco: 9000));
        Assert.Equal(HttpStatusCode.OK, big.StatusCode);
    }

    [Fact]
    public async Task The_percent_limit_is_measured_against_the_price_in_force_not_the_base()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Vigente");
        var produto = await NewProductAsync(client, t.Dono, categoria.Id, "VG1", "Chopp", 1000);
        var agora = DateTimeOffset.UtcNow;
        using var promo = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.Dono, ProductBody(produto, new { precoCentavos = 800, inicio = agora.AddHours(-1), fim = agora.AddHours(1) }));
        Assert.Equal(HttpStatusCode.OK, promo.StatusCode);
        var viva = (await promo.Content.ReadFromJsonAsync<Produto>())!;

        // In force: 800. An override of 880 is +10 percent of that (allowed, though 12 percent below the base); 881 is over the limit.
        using var over = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.GerenteComLimite, new { precoCentavos = 881 });
        Assert.Equal(HttpStatusCode.Forbidden, over.StatusCode);
        using var edge = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.GerenteComLimite, new { precoCentavos = 880 });
        Assert.Equal(HttpStatusCode.OK, edge.StatusCode);

        // Removing it goes from 880 back to 800 (9.09 percent of 880): allowed, and the price in force is again the promotion.
        using var removed = await SendAsync(client, HttpMethod.Delete, $"/cardapio/produtos/{produto.Id}/preco-unidade", t.GerenteComLimite);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        // A window change that moves the price in force is measured the same way: ending the promotion now takes 800 to 1000 (25 percent): denied; shifting it keeps 800: allowed.
        using var ends = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.GerenteComLimite, ProductBody(viva, new { precoCentavos = 800, inicio = agora.AddHours(-3), fim = agora.AddHours(-2) }));
        Assert.Equal(HttpStatusCode.Forbidden, ends.StatusCode);
        using var shifts = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id}", t.GerenteComLimite, ProductBody(viva, new { precoCentavos = 800, inicio = agora.AddHours(-2), fim = agora.AddHours(5) }));
        Assert.Equal(HttpStatusCode.OK, shifts.StatusCode);
    }

    [Fact]
    public async Task The_new_menu_routes_are_denied_without_the_permission_and_without_a_session()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Negados");
        var produto = await NewProductAsync(client, t.Dono, categoria.Id, "NG1", "Negado", 1000);

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Put, $"/cardapio/produtos/{produto.Id}/preco-unidade", new { precoCentavos = 10 }),
            (HttpMethod.Delete, $"/cardapio/produtos/{produto.Id}/preco-unidade", null),
            (HttpMethod.Post, "/cardapio/categorias", new { nome = "Sub", categoriaPaiId = categoria.Id }),
        })
        {
            using var denied = await SendAsync(client, method, path, t.Garcom, body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(string.Empty, await denied.Content.ReadAsStringAsync());
            using var anonymous = await SendAsync(client, method, path, null, body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Search_ignores_accents_and_case_puts_the_exact_code_first_then_names_that_start_with_the_text_and_keeps_acabou_visible()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Busca");
        var caldo = await NewProductAsync(client, t.Dono, categoria.Id, "ACAI", "Caldo de açaí", 1000);
        var tigela = await NewProductAsync(client, t.Dono, categoria.Id, "B1", "Açaí na tigela", 1000);
        var suco = await NewProductAsync(client, t.Dono, categoria.Id, "C1", "Suco de AÇAÍ", 1000);
        var pao = await NewProductAsync(client, t.Dono, categoria.Id, "PAO", "Pão de queijo", 800);
        var antigo = await NewProductAsync(client, t.Dono, categoria.Id, "OLD", "Açaí antigo", 900, ativo: false);
        using var acabou = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{pao.Id}", t.Dono, new { codigo = "PAO", nome = "Pão de queijo", precoCentavos = 800, categoriaId = categoria.Id, ativo = true, versao = pao.Versao, indisponivel = true });
        Assert.Equal(HttpStatusCode.OK, acabou.StatusCode);

        // The exact short code beats names, then names that start with the text, then the rest by name; accents and case never matter, on either side.
        Assert.Equal([caldo.Id, tigela.Id, suco.Id], (await ListAsync(client, "/cardapio/produtos/busca?q=acai", t.Garcom)).Select(p => p.Id));
        Assert.Equal([tigela.Id, caldo.Id, suco.Id], (await ListAsync(client, "/cardapio/produtos/busca?q=A%C3%87A%C3%8D", t.Garcom)).Select(p => p.Id));
        Assert.Equal([tigela.Id], (await ListAsync(client, "/cardapio/produtos/busca?q=" + Uri.EscapeDataString("  açaí na "), t.Garcom)).Select(p => p.Id));
        Assert.Equal([caldo.Id], (await ListAsync(client, "/cardapio/produtos/busca?q=ACAI", t.Garcom)).Select(p => p.Id).Take(1));

        // A code is matched whole, not by prefix: "PA" is not "PAO" by code, but it is the start of "Pão de queijo" by name. "Acabou" stays listed, flagged.
        var porNome = Assert.Single(await ListAsync(client, "/cardapio/produtos/busca?q=pa", t.Garcom));
        Assert.Equal((pao.Id, true), (porNome.Id, porNome.Indisponivel));
        Assert.Equal([pao.Id], (await ListAsync(client, "/cardapio/produtos/busca?q=pao", t.Garcom)).Select(p => p.Id));
        Assert.Equal([pao.Id], (await ListAsync(client, "/cardapio/produtos/busca?q=PÃO", t.Garcom)).Select(p => p.Id));

        // Inactive never shows; blank finds nothing; wildcards are plain text; too long is a 400.
        Assert.DoesNotContain(await ListAsync(client, "/cardapio/produtos/busca?q=acai", t.Garcom), p => p.Id == antigo.Id);
        Assert.Empty(await ListAsync(client, "/cardapio/produtos/busca?q=%20%20", t.Garcom));
        Assert.Empty(await ListAsync(client, "/cardapio/produtos/busca", t.Garcom));
        Assert.Empty(await ListAsync(client, "/cardapio/produtos/busca?q=%25", t.Garcom));
        using var tooLong = await SendAsync(client, HttpMethod.Get, "/cardapio/produtos/busca?q=" + new string('a', 81), t.Garcom);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Search_is_capped_at_twenty_ordered_by_name_and_never_crosses_organizations_or_sessions()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Lote");
        foreach (var n in Enumerable.Range(1, 25).Reverse())
        {
            await NewProductAsync(client, t.Dono, categoria.Id, $"L{n:00}", $"Lote {n:00}", 100);
        }

        var found = await ListAsync(client, "/cardapio/produtos/busca?q=lote", t.Garcom);
        Assert.Equal(Enumerable.Range(1, 20).Select(n => $"Lote {n:00}"), found.Select(p => p.Nome));

        // Organization B sees none of it, even by exact code; no session and no permission are refused.
        var categoriaB = await NewCategoryAsync(client, t.DonoB, "Outra");
        var deB = await NewProductAsync(client, t.DonoB, categoriaB.Id, "L01", "Lote de B", 100);
        Assert.Equal([deB.Id], (await ListAsync(client, "/cardapio/produtos/busca?q=L01", t.DonoB)).Select(p => p.Id));
        Assert.Equal(["Lote 01"], (await ListAsync(client, "/cardapio/produtos/busca?q=L01", t.Dono)).Select(p => p.Nome));
        using var anonymous = await SendAsync(client, HttpMethod.Get, "/cardapio/produtos/busca?q=lote", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var noSession = await SendAsync(client, HttpMethod.Get, "/cardapio/produtos/favoritos", null);
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
    }

    [Fact]
    public async Task Search_is_one_statement_with_the_unit_price_inside_and_the_normalized_name_in_the_database_equals_the_one_in_code()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new CardapioDbContext(tx);

        var sql = PostgresCardapioStore.Busca(context, UnitA, "ACAI", "ACAI", 20).ToQueryString();
        Assert.Equal(1, sql.Split("FROM produtos AS", StringSplitOptions.None).Length - 1);
        Assert.Contains("LIMIT @", sql, StringComparison.Ordinal);
        Assert.Contains("nome_busca", sql, StringComparison.Ordinal);
        Assert.Contains("produtos_preco_unidade", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", sql, StringComparison.Ordinal);

        // Every letter with a diacritic of Latin-1 and Latin Extended-A: the database and the API fold it to the same text, so the two sides of a comparison always agree.
        var letters = Enumerable.Range(0x41, 0x180 - 0x41).Select(cp => ((char)cp).ToString()).Where(c => char.IsLetter(c[0])).ToList();
        foreach (var letter in letters)
        {
            await using var command = new NpgsqlCommand("SELECT normaliza_busca(@t)", tx.Connection, tx.Transaction);
            command.Parameters.AddWithValue("t", letter);
            var folded = (string)(await command.ExecuteScalarAsync())!;
            if (letter.Normalize(System.Text.NormalizationForm.FormD).Length > 1 || letter[0] < 0x80)
            {
                Assert.True(folded == Flow4Tech.Cardapio.Domain.RegrasDeBusca.Normalizar(letter), $"'{letter}' folds to '{folded}' in the database and '{Flow4Tech.Cardapio.Domain.RegrasDeBusca.Normalizar(letter)}' in code");
            }
        }
    }

    [Fact]
    public async Task Favorites_are_the_callers_own_most_launched_active_products_in_a_stable_order_and_empty_without_history()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var categoria = await NewCategoryAsync(client, t.Dono, "Favoritos");
        var b = await NewProductAsync(client, t.Dono, categoria.Id, "F2", "Beta", 100);
        var a = await NewProductAsync(client, t.Dono, categoria.Id, "F1", "Alfa", 100);
        var c = await NewProductAsync(client, t.Dono, categoria.Id, "F3", "Gama", 100);
        var inativo = await NewProductAsync(client, t.Dono, categoria.Id, "F4", "Inativo", 100);

        Assert.Empty(await ListAsync(client, "/cardapio/produtos/favoritos", t.Garcom));

        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using (var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA)))
        {
            // The waiter: Gama 9, Alfa 3 and Beta 3 (a tie, broken by name), an inactive one with 20. The owner launched Beta 50 times: not the waiter's favorite.
            foreach (var (quem, produto, n) in new[] { (Garcom, c, 9L), (Garcom, a, 3L), (Garcom, b, 3L), (Garcom, inativo, 20L), (DonoA, b, 50L) })
            {
                await Exec(tx, "INSERT INTO produtos_frequencia_garcom (organizacao_id, unidade_id, garcom_id, produto_id, lancamentos) VALUES (@o, @u, @g, @p, @n)", ("o", OrgA), ("u", UnitA), ("g", quem), ("p", produto.Id), ("n", n));
            }

            await tx.CommitAsync();
        }

        using var off = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{inativo.Id}", t.Dono, new { codigo = "F4", nome = "Inativo", precoCentavos = 100, categoriaId = categoria.Id, ativo = false, versao = 1 });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        Assert.Equal([c.Id, a.Id, b.Id], (await ListAsync(client, "/cardapio/produtos/favoritos", t.Garcom)).Select(p => p.Id));
        Assert.Equal([b.Id], (await ListAsync(client, "/cardapio/produtos/favoritos", t.Dono)).Select(p => p.Id));
        Assert.Empty(await ListAsync(client, "/cardapio/produtos/favoritos", t.DonoB));
    }

    [Fact]
    public async Task Favorites_and_search_are_denied_to_an_account_without_the_product_permission()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        _ = await SeedAsync(api);
        var semPerfil = Guid.Parse("0197b000-0000-7000-8000-0000000000c9");
        await using (var ownerSource = NpgsqlDataSource.Create(_postgres.GetConnectionString()))
        {
            await using var tx = await new TenantDatabase(ownerSource).BeginAsync(new OrganizationContext(OrgA, UnitA));
            await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', 'Sem perfil', 'semperfil', now())", ("i", semPerfil), ("o", OrgA));
            await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", OrgA), ("u", semPerfil), ("n", UnitA));
            await tx.CommitAsync();
        }

        var versao = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{semPerfil:D}'", r => r.GetInt32(0))).Single();
        var token = (await api.Services.GetRequiredService<Sessoes>().IniciarAsync(semPerfil, OrgA, versao, false, new ClienteDaSessao("127.0.0.1", "testes"))).AccessToken;

        foreach (var path in new[] { "/cardapio/produtos/favoritos", "/cardapio/produtos/busca?q=a" })
        {
            using var denied = await SendAsync(client, HttpMethod.Get, path, token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(string.Empty, await denied.Content.ReadAsStringAsync());
        }
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
