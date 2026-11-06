using Flow4Tech.Cardapio.Application;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flow4Tech.E2eSeed;

/// <summary>
/// Seeds a LOCAL database for the browser tests (docs/agents/project.md, "e2e"): two organizations, an owner in each, a unit, tables, a few products, a waiter and a cashier of the first
/// organization with custom profiles made through <c>GestaoDePerfis</c>, and an account with a temporary credential (forced password change). It goes through the same services the server
/// uses, so every rule (password policy, audit, RLS) applies; only the organizations and units, which have no creation route yet, are inserted with the owner credential.
/// Idempotent: when the first organization already exists nothing is written. Configuration: environment variables only, never values in the repository.
/// </summary>
internal static class SeedMain
{
    private static readonly Guid OrgA = Guid.Parse("0197e2e0-0000-7000-8000-00000000000a");
    private static readonly Guid UnitA = Guid.Parse("0197e2e0-0000-7000-8000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("0197e2e0-0000-7000-8000-00000000000b");
    private static readonly Guid UnitB = Guid.Parse("0197e2e0-0000-7000-8000-0000000000b1");

    public static async Task<int> Main()
    {
        var ownerUrl = Required("E2E_SEED_OWNER_URL");
        var appUrl = Required("E2E_SEED_APP_URL");
        var password = Required("E2E_SEED_PASSWORD");
        var tokenKey = Environment.GetEnvironmentVariable("Flow4Tech__Tokens__AppKey") ?? "e2e-seed-only-token-key-not-used-for-anything-real";

        var ownerConnection = ToConnectionString(ownerUrl);
        await using var ownerSource = NpgsqlDataSource.Create(ownerConnection);
        if (await ExistsAsync(ownerSource))
        {
            Console.WriteLine("e2e seed: already present, nothing written.");
            return 0;
        }

        await InsertOrganizationsAsync(ownerSource);

        await using var baseFactory = new WebApplicationFactory<global::Program>();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(Path.GetDirectoryName(typeof(global::Program).Assembly.Location)!);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = ToConnectionString(appUrl),
                    ["Flow4Tech:Tokens:AppKey"] = tokenKey,
                }));
        });
        var services = factory.Services;
        var contas = services.GetRequiredService<GestaoDeContas>();
        var troca = services.GetRequiredService<TrocaDeSenha>();
        var perfis = services.GetRequiredService<GestaoDePerfis>();
        var cardapio = services.GetRequiredService<GestaoDoCardapio>();
        var mesas = services.GetRequiredService<GestaoDeMesas>();

        var donoA = await CreateOwnerAsync(contas, OrgA, "dono@bar-a.test", "Dono do Bar A", password);
        var donoB = await CreateOwnerAsync(contas, OrgB, "dono@bar-b.test", "Dono do Bar B", password);
        await LinkAsync(ownerSource, OrgA, UnitA, donoA);
        await LinkAsync(ownerSource, OrgB, UnitB, donoB);
        var requestA = new SeedRequest(donoA, new OrganizationContext(OrgA, UnitA));

        var garcom = await CreateOperationalAsync(contas, troca, requestA, "garcom", "Garcom da Casa", password);
        var caixa = await CreateOperationalAsync(contas, troca, requestA, "caixa", "Caixa da Casa", password);
        var novato = await contas.CriarOperacionalAsync(requestA, "novato", "Novato");
        Expect(novato.Status == StatusCriacao.Criada && novato.ContaId is not null && novato.Credencial is not null, "novato");
        foreach (var conta in new[] { garcom, caixa, novato.ContaId!.Value })
        {
            await LinkAsync(ownerSource, OrgA, UnitA, conta);
        }

        var perfilGarcom = await perfis.CriarAsync(requestA, "Garçom", Rules("comanda:abrir", "comanda:ver", "comanda:lancar-item", "comanda:cancelar-item", "comanda:desfazer-rodada", "mesa:ver", "produto:ver"));
        var perfilCaixa = await perfis.CriarAsync(
            requestA,
            "Caixa",
            Rules("comanda:ver", "comanda:ver-comandas-de-outros", "comanda:receber-pagamento", "comanda:fechar", "pagamento:registrar", "pagamento:ver", "caixa:ver", "caixa:abrir", "caixa:fechar", "produto:ver", "mesa:ver"));
        Expect(perfilGarcom.Resultado == ResultadoGestao.Ok && perfilCaixa.Resultado == ResultadoGestao.Ok, "profiles");
        Expect(await perfis.AtribuirAsync(requestA, garcom, perfilGarcom.Id!.Value) == ResultadoGestao.Ok, "assign garcom");
        Expect(await perfis.AtribuirAsync(requestA, caixa, perfilCaixa.Id!.Value) == ResultadoGestao.Ok, "assign caixa");
        Expect(await perfis.AtribuirAsync(requestA, novato.ContaId!.Value, perfilGarcom.Id!.Value) == ResultadoGestao.Ok, "assign novato");

        var bebidas = await cardapio.CriarCategoriaAsync(requestA, "Bebidas");
        var porcoes = await cardapio.CriarCategoriaAsync(requestA, "Porções");
        Expect(bebidas.Categoria is not null && porcoes.Categoria is not null, "categories");
        foreach (var (codigo, nome, preco, categoria) in new[]
        {
            ("CHOPP", "Chopp 300ml", 1250L, bebidas.Categoria!.Id),
            ("REFRI", "Refrigerante lata", 700L, bebidas.Categoria!.Id),
            ("AGUA", "Água sem gás", 500L, bebidas.Categoria!.Id),
            ("BATATA", "Porção de batata", 3490L, porcoes.Categoria!.Id),
        })
        {
            var produto = await cardapio.CriarProdutoAsync(requestA, new NovoProduto(codigo, nome, preco, categoria, true));
            Expect(produto.Produto is not null, $"product {codigo}");
        }

        foreach (var mesa in new[] { "1", "2", "3", "4", "5", "6" })
        {
            Expect((await mesas.CriarAsync(requestA, mesa, null)).Resultado == ResultadoDeMesa.Ok, $"table {mesa}");
        }

        Console.WriteLine("e2e seed: done.");
        Console.WriteLine("  organization A (Bar A, unit Centro): dono@bar-a.test (owner), operational users 'garcom' and 'caixa' (password from E2E_SEED_PASSWORD).");
        Console.WriteLine($"  operational user 'novato' has a TEMPORARY credential (forces a password change): {novato.Credencial!.Valor}");
        Console.WriteLine("  organization B (Bar B, unit Centro): dono@bar-b.test (owner).");
        return 0;
    }

    private static List<RegraDeAcesso> Rules(params string[] permissions) => permissions.Select(p => new RegraDeAcesso(RuleEffect.Allow, p)).ToList();

    private static async Task<Guid> CreateOwnerAsync(GestaoDeContas contas, Guid org, string email, string nome, string password)
    {
        var result = await contas.CriarDonoAsync(org, email, nome, password);
        Expect(result.Status == StatusCriacao.Criada && result.ContaId is not null, $"owner {email}: {result.Status}");
        return result.ContaId!.Value;
    }

    private static async Task<Guid> CreateOperationalAsync(GestaoDeContas contas, TrocaDeSenha troca, IRequestContext request, string usuario, string nome, string password)
    {
        var created = await contas.CriarOperacionalAsync(request, usuario, nome);
        Expect(created.Status == StatusCriacao.Criada && created.ContaId is not null && created.Credencial is not null, $"operational {usuario}: {created.Status}");
        var changed = await troca.TrocarAsync(request.Organization.OrganizationId, created.ContaId!.Value, created.Credencial!.Valor, password);
        Expect(changed.Status == StatusTrocaDeSenha.Trocada, $"password of {usuario}: {changed.Status}");
        return created.ContaId.Value;
    }

    /// <summary>The seed is complete when its LAST step (the tables) exists; a run that stopped halfway is repeated from where the idempotent steps allow.</summary>
    private static async Task<bool> ExistsAsync(NpgsqlDataSource source)
    {
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM mesas)", tx.Connection, tx.Transaction);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertOrganizationsAsync(NpgsqlDataSource source)
    {
        foreach (var (org, unit, nome) in new[] { (OrgA, UnitA, "Bar A"), (OrgB, UnitB, "Bar B") })
        {
            await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
            await Exec(tx, "INSERT INTO organizacoes (id, nome) VALUES (@o, @n) ON CONFLICT (id) DO NOTHING", ("o", org), ("n", nome));
            await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Centro') ON CONFLICT (id) DO NOTHING", ("u", unit), ("o", org));
            await tx.CommitAsync();
        }
    }

    private static async Task LinkAsync(NpgsqlDataSource source, Guid org, Guid unit, Guid user)
    {
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
        await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", user), ("n", unit));
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

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set the environment variable {name} (see docs/agents/project.md, e2e).");

    /// <summary>Accepts the Railway/compose style <c>postgres://user:password@host:port/db</c> or an Npgsql connection string.</summary>
    private static string ToConnectionString(string value)
    {
        if (!value.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var uri = new Uri(value);
        var credentials = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,
        }.ConnectionString;
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"e2e seed failed at: {what}");
        }
    }

    private sealed class SeedRequest(Guid user, OrganizationContext organization) : IRequestContext
    {
        public Guid UserId => user;

        public Guid SessionId => Guid.NewGuid();

        public OrganizationContext Organization => organization;
    }
}
