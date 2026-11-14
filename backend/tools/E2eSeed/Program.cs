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
using System.Text.Json;

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
    private static readonly Guid UnitA2 = Guid.Parse("0197e2e0-0000-7000-8000-0000000000a2");
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
            Console.WriteLine($"  summary of ids: {await WriteSummaryAsync(ownerSource)}");
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
        var areas = services.GetRequiredService<GestaoDeAreas>();

        var donoA = await CreateOwnerAsync(contas, OrgA, "dono@bar-a.test", "Dono do Bar A", password);
        var donoB = await CreateOwnerAsync(contas, OrgB, "dono@bar-b.test", "Dono do Bar B", password);
        await LinkAsync(ownerSource, OrgA, UnitA, donoA);
        await LinkAsync(ownerSource, OrgB, UnitB, donoB);
        var requestA = new SeedRequest(donoA, new OrganizationContext(OrgA, UnitA));

        var garcom = await CreateOperationalAsync(contas, troca, requestA, "garcom", "Garcom da Casa", password);
        var caixa = await CreateOperationalAsync(contas, troca, requestA, "caixa", "Caixa da Casa", password);
        var novato = await contas.CriarOperacionalAsync(requestA, "novato", "Novato");
        Expect(novato.Status == StatusCriacao.Criada && novato.ContaId is not null && novato.Credencial is not null, "novato");
        // A second waiter for the browser tests that open comandas, so the waiter whose empty list other tests assert stays empty.
        var garcom2 = await CreateOperationalAsync(contas, troca, requestA, "garcom2", "Garcom de Teste", password);
        var gerente = await CreateOperationalAsync(contas, troca, requestA, "gerente", "Gerente das duas unidades", password);
        var semUnidade = await CreateOperationalAsync(contas, troca, requestA, "semunidade", "Sem unidade", password);
        foreach (var conta in new[] { garcom, garcom2, caixa, novato.ContaId!.Value, gerente })
        {
            await LinkAsync(ownerSource, OrgA, UnitA, conta);
        }

        await LinkAsync(ownerSource, OrgA, UnitA2, gerente);

        var perfilGarcom = await perfis.CriarAsync(requestA, "Garçom", Rules("comanda:abrir", "comanda:ver", "comanda:lancar-item", "comanda:cancelar-item", "comanda:desfazer-rodada", "comanda:transferir-mesa", "comanda:alterar-item", "comanda:renomear", "comanda:alterar-dados", "comanda:transferir-item", "comanda:dividir", "mesa:ver", "produto:ver"));
        var perfilCaixa = await perfis.CriarAsync(
            requestA,
            "Caixa",
            Rules("comanda:ver", "comanda:ver-comandas-de-outros", "comanda:receber-pagamento", "comanda:fechar", "comanda:remover-taxa-servico", "comanda:dividir", "comanda:reimprimir", "pagamento:registrar", "pagamento:ver", "pagamento:dividir", "pagamento:registrar-gorjeta", "caixa:ver", "caixa:abrir", "caixa:fechar", "produto:ver", "mesa:ver"));
        Expect(perfilGarcom.Resultado == ResultadoGestao.Ok && perfilCaixa.Resultado == ResultadoGestao.Ok, "profiles");
        Expect(await perfis.AtribuirAsync(requestA, garcom, perfilGarcom.Id!.Value) == ResultadoGestao.Ok, "assign garcom");
        Expect(await perfis.AtribuirAsync(requestA, caixa, perfilCaixa.Id!.Value) == ResultadoGestao.Ok, "assign caixa");
        Expect(await perfis.AtribuirAsync(requestA, novato.ContaId!.Value, perfilGarcom.Id!.Value) == ResultadoGestao.Ok, "assign novato");
        Expect(await perfis.AtribuirAsync(requestA, garcom2, perfilGarcom.Id!.Value) == ResultadoGestao.Ok, "assign garcom2");
        Expect(await perfis.AtribuirAsync(requestA, gerente, perfilGarcom.Id!.Value) == ResultadoGestao.Ok, "assign gerente");
        Expect(await perfis.AtribuirAsync(requestA, semUnidade, perfilGarcom.Id!.Value) == ResultadoGestao.Ok, "assign semunidade");

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

        // Two areas for the table map (F4-0055): tables 1 to 3 in the "Salão", 4 to 6 in the "Varanda".
        var salao = await areas.CriarAsync(requestA, "Salão");
        var varanda = await areas.CriarAsync(requestA, "Varanda");
        Expect(salao.Resultado == ResultadoDeMesa.Ok && varanda.Resultado == ResultadoDeMesa.Ok, "areas");
        foreach (var mesa in new[] { "1", "2", "3", "4", "5", "6" })
        {
            var area = int.Parse(mesa, System.Globalization.CultureInfo.InvariantCulture) <= 3 ? salao.Area!.Id : varanda.Area!.Id;
            Expect((await mesas.CriarAsync(requestA, mesa, null, area)).Resultado == ResultadoDeMesa.Ok, $"table {mesa}");
        }

        var summary = await WriteSummaryAsync(ownerSource);

        Console.WriteLine("e2e seed: done.");
        Console.WriteLine("  organization A (Bar A, units Centro and Praia): dono@bar-a.test (owner); operational users garcom, garcom2, caixa, gerente (both units), semunidade (no unit), novato (temporary credential); password from E2E_SEED_PASSWORD.");
        Console.WriteLine($"  'novato' TEMPORARY credential (forces a password change; once): {novato.Credencial!.Valor}");
        Console.WriteLine("  organization B (Bar B, unit Centro): dono@bar-b.test (owner).");
        Console.WriteLine($"  summary of ids: {summary}");
        return 0;
    }

    /// <summary>Ids only (never a password), read back from the database so a repeated run can still tell the browser tests who is who. The tests ask the owner for a fresh temporary credential of 'novato' through the real route (POST /contas/{id}/redefinir-acesso).</summary>
    private static async Task<string> WriteSummaryAsync(NpgsqlDataSource source)
    {
        var accounts = new Dictionary<string, Guid>();
        foreach (var (org, unit) in new[] { (OrgA, UnitA), (OrgB, UnitB) })
        {
            await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
            await using var command = new NpgsqlCommand("SELECT COALESCE(usuario, email), id FROM contas", tx.Connection, tx.Transaction);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                accounts[reader.GetString(0)] = reader.GetGuid(1);
            }
        }

        var summary = JsonSerializer.Serialize(new
        {
            organizacaoA = OrgA,
            unidadeCentroA = UnitA,
            unidadePraiaA = UnitA2,
            organizacaoB = OrgB,
            unidadeCentroB = UnitB,
            contas = accounts,
        });
        if (Environment.GetEnvironmentVariable("E2E_SEED_OUT") is { Length: > 0 } outPath)
        {
            await File.WriteAllTextAsync(outPath, summary);
        }

        return summary;
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
            if (org == OrgA)
            {
                await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Praia') ON CONFLICT (id) DO NOTHING", ("u", UnitA2), ("o", org));
            }
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
