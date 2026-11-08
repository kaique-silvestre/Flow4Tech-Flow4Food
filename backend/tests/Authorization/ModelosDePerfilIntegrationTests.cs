using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using System.Text.RegularExpressions;
using Flow4Tech.Isolation.Tests;

namespace Flow4Tech.Authorization.Tests;

/// <summary>F4-0046 on a real PostgreSQL: model profiles as data, and the read side (list, a person's profiles) behind <c>perfil:ver</c>.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class ModelosDePerfilIntegrationTests(PostgresFixture postgres)
{
    private static string BackendFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "modules.dependencies.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir?.FullName ?? throw new InvalidOperationException("backend root not found"), .. parts]);
    }

    /// <summary>Names added to <see cref="ModelosDePerfil"/> for a later ticket before the catalog knows them. Empty today: every name must exist, so a typo is not skipped silently.</summary>
    private static readonly string[] AddedEarly = [];

    [Fact]
    public void Every_model_permission_exists_in_the_catalog_except_the_explicit_early_allowlist()
    {
        var catalog = File.ReadAllLines(BackendFile("permissions.catalog.txt")).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);

        var unknown = ModelosDePerfil.Todos.SelectMany(m => m.Permissoes).Distinct().Where(p => !catalog.Contains(p) && !AddedEarly.Contains(p)).ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void Waiter_and_cashier_models_equal_the_e2e_seed_profiles_and_the_manager_is_their_superset()
    {
        var seed = File.ReadAllText(BackendFile("tools", "E2eSeed", "Program.cs"));
        var profileRegex = new Regex("\"(Garçom|Caixa)\",?\\s*Rules\\(([^)]*)\\)", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        var quoted = new Regex("\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(5));
        var found = profileRegex.Matches(seed)
            .ToDictionary(m => m.Groups[1].Value, m => quoted.Matches(m.Groups[2].Value).Select(x => x.Groups[1].Value).ToList());
        Assert.Equal(2, found.Count);

        foreach (var nome in new[] { "Garçom", "Caixa" })
        {
            var model = ModelosDePerfil.Todos.Single(m => m.Nome == nome).Permissoes;
            Assert.Equal(found[nome].Order(StringComparer.Ordinal), model.Order(StringComparer.Ordinal));
        }

        // Deliberate difference: the seed has no manager profile (its "gerente" user holds the waiter profile), so the model is the union of both plus table/product management and cash overview.
        var manager = ModelosDePerfil.Todos.Single(m => m.Chave == "gerente").Permissoes;
        Assert.All(found.Values.SelectMany(v => v), p => Assert.Contains(p, manager));
    }

    [Fact]
    public void The_models_are_plain_data_with_unique_names_and_no_profile_management()
    {
        Assert.Equal(["garcom", "caixa", "gerente"], ModelosDePerfil.Todos.Select(m => m.Chave));
        Assert.Equal(3, ModelosDePerfil.Todos.Select(m => m.Nome).Distinct().Count());
        Assert.All(ModelosDePerfil.Todos, m => Assert.Equal(m.Permissoes.Count, m.Permissoes.Distinct().Count()));
        Assert.All(ModelosDePerfil.Todos, m => Assert.DoesNotContain(m.Permissoes, p => p.StartsWith("perfil:", StringComparison.Ordinal) || p == PerfisPermissions.Atribuir));
    }

    [Fact]
    public async Task Creating_the_models_skips_permissions_the_catalog_does_not_know_and_repeating_creates_nothing()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);

        var first = await env.Gestao.CriarModelosAsync(PerfisTestEnv.As(owner));
        var second = await env.Gestao.CriarModelosAsync(PerfisTestEnv.As(owner));

        Assert.Equal(ResultadoGestao.Ok, first.Resultado);
        Assert.All(first.Modelos, m => Assert.True(m.Criado));
        Assert.All(second.Modelos, m => Assert.False(m.Criado));
        var listed = await env.Gestao.ListarAsync(PerfisTestEnv.As(owner));
        Assert.Equal(3, listed.Perfis.Count);
        // The test catalog lacks most salão permissions: each model keeps only the ones it knows instead of failing as invalid.
        Assert.All(listed.Perfis, p => Assert.All(p.Regras, r => Assert.Contains(r.Permission, PerfisTestEnv.FullCatalog)));
        Assert.All(listed.Perfis, p => Assert.True(p.Regras.Count < ModelosDePerfil.Todos.Single(m => m.Nome == p.Nome).Permissoes.Count));
    }

    [Fact]
    public async Task Reading_profiles_needs_perfil_ver_and_a_person_of_another_organization_does_not_exist()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var plain = await env.AddAccountAsync(owner: false);
        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Leitura", [new RegraDeAcesso(RuleEffect.Allow, PerfisPermissions.Ver)]);
        Assert.Equal(ResultadoGestao.Ok, await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), plain, profile.Id!.Value));
        var nobody = await env.AddAccountAsync(owner: false);

        Assert.Equal(ResultadoGestao.Negado, (await env.Gestao.ListarAsync(PerfisTestEnv.As(nobody))).Resultado);
        Assert.Equal(ResultadoGestao.Negado, (await env.Gestao.PerfisDaPessoaAsync(PerfisTestEnv.As(nobody), plain)).Resultado);
        var seen = await env.Gestao.PerfisDaPessoaAsync(PerfisTestEnv.As(plain), plain);
        Assert.Equal(ResultadoGestao.Ok, seen.Resultado);
        Assert.Equal("Leitura", Assert.Single(seen.Perfis).Nome);
        Assert.Equal(ResultadoGestao.NaoEncontrado, (await env.Gestao.PerfisDaPessoaAsync(PerfisTestEnv.As(owner), Guid.NewGuid())).Resultado);
    }
}
