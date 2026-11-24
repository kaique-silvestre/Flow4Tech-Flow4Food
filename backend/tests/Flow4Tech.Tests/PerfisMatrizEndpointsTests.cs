using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Tests;

/// <summary>F4-0155 (G9 backend): the profile x permission matrix and the edit of a profile's rules, through the real authenticated route (same fixture as the profile routes).</summary>
public sealed partial class PerfisEndpointsTests
{
    private sealed record ColunaBody(string Chave, string Nome, Guid? Id, bool Editavel, List<RegraBody> Regras);

    private sealed record MatrizBody(List<string> Permissoes, List<ColunaBody> Perfis);

    private static object Regra(string efeito, string permissao, decimal? maxPercentual = null, long? tetoDiarioCentavos = null) =>
        new { efeito, permissao, maxPercentual, tetoDiarioCentavos };

    private static async Task<Guid> CreateProfileAsync(Api api, string name, params RegraDeAcesso[] rules)
    {
        var created = await api.Services.GetRequiredService<GestaoDePerfis>().CriarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), name, rules);
        Assert.Equal(ResultadoGestao.Ok, created.Resultado);
        return created.Id!.Value;
    }

    private static async Task<MatrizBody> MatrizAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/perfis/matriz", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MatrizBody>())!;
    }

    [Fact]
    public async Task The_matrix_lists_the_catalog_the_read_only_factory_columns_and_only_the_own_organizations_profiles()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var donoB = await TokenAsync(api, DonoB, OrgB, UnitB);
        await CreateModelsAsync(client, dono);

        var matrix = await MatrizAsync(client, dono);

        var catalog = api.Services.GetRequiredService<PolicySet>().KnownPermissions;
        Assert.Equal(catalog.Order(StringComparer.Ordinal), matrix.Permissoes);
        var factory = matrix.Perfis.Where(c => !c.Editavel).ToList();
        Assert.Equal(2, factory.Count);
        Assert.All(factory, c => Assert.Null(c.Id));
        Assert.Equal(catalog.Order(StringComparer.Ordinal), factory.Single(c => c.Chave == "dono").Regras.Select(r => r.Permissao).Order(StringComparer.Ordinal));
        Assert.Equal(["Caixa", "Garçom", "Gerente"], matrix.Perfis.Where(c => c.Editavel).Select(c => c.Nome).Order(StringComparer.Ordinal));
        Assert.All(matrix.Perfis.Where(c => c.Editavel), c => Assert.NotNull(c.Id));

        var other = await MatrizAsync(client, donoB);
        Assert.DoesNotContain(other.Perfis, c => c.Editavel);
        Assert.Equal(2, other.Perfis.Count);
    }

    [Fact]
    public async Task The_matrix_and_the_rule_edit_are_asked_by_their_permission_viewer_manager_and_no_session()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        await GiveAsync(api, Gerente, "So ver perfis", "perfil:ver");
        await GiveAsync(api, Garcom, "So gerir perfis", "perfil:gerir", "mesa:ver");
        var perfil = await CreateProfileAsync(api, "Alvo do teste", new RegraDeAcesso(RuleEffect.Allow, "mesa:ver"));
        var soVer = await TokenAsync(api, Gerente, OrgA, UnitA);
        var soGerir = await TokenAsync(api, Garcom, OrgA, UnitA);
        var body = new { regras = new[] { Regra("allow", "mesa:ver") } };
        var path = $"/perfis/{perfil:D}/regras";

        using var matrixViewer = await SendAsync(client, HttpMethod.Get, "/perfis/matriz", soVer);
        using var matrixManager = await SendAsync(client, HttpMethod.Get, "/perfis/matriz", soGerir);
        using var matrixAnonymous = await SendAsync(client, HttpMethod.Get, "/perfis/matriz", null);
        using var editViewer = await SendAsync(client, HttpMethod.Put, path, soVer, body);
        using var editAnonymous = await SendAsync(client, HttpMethod.Put, path, null, body);
        using var editManager = await SendAsync(client, HttpMethod.Put, path, soGerir, body);

        Assert.Equal(HttpStatusCode.OK, matrixViewer.StatusCode);
        await AssertForbiddenEmptyAsync(matrixManager);
        Assert.Equal(HttpStatusCode.Unauthorized, matrixAnonymous.StatusCode);
        await AssertForbiddenEmptyAsync(editViewer);
        Assert.Equal(HttpStatusCode.Unauthorized, editAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, editManager.StatusCode);
    }

    [Fact]
    public async Task Editing_a_profile_replaces_its_rules_with_limits_ends_the_holders_session_and_is_audited_with_names_and_numbers_only()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var waiter = (await CreateModelsAsync(client, dono)).Single(m => m.Chave == "garcom").PerfilId!.Value;
        using (var assigned = await SendAsync(client, HttpMethod.Put, $"/contas/{Alvo:D}/perfis/{waiter:D}", dono))
        {
            Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        }

        var alvoToken = await TokenAsync(api, Alvo, OrgA, UnitA);
        using (var before = await SendAsync(client, HttpMethod.Get, "/sessao/permissoes", alvoToken))
        {
            Assert.Contains("comanda:lancar-item", await before.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var v0 = await VersionAsync(Alvo);
        using (var edited = await SendAsync(client, HttpMethod.Put, $"/perfis/{waiter:D}/regras", dono, new { regras = new[] { Regra("allow", "mesa:ver"), Regra("allow", "comanda:desconto", maxPercentual: 5m, tetoDiarioCentavos: 10_000) } }))
        {
            Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        }

        // In force on the NEXT request of the holder: the old session ends, a fresh one carries the new rights only.
        Assert.True(await VersionAsync(Alvo) > v0);
        using (var ended = await SendAsync(client, HttpMethod.Get, "/sessao/permissoes", alvoToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        }

        var fresh = await TokenAsync(api, Alvo, OrgA, UnitA);
        using (var permissions = await SendAsync(client, HttpMethod.Get, "/sessao/permissoes", fresh))
        {
            var text = await permissions.Content.ReadAsStringAsync();
            Assert.Contains("mesa:ver", text, StringComparison.Ordinal);
            Assert.DoesNotContain("comanda:lancar-item", text, StringComparison.Ordinal);
        }

        var column = (await MatrizAsync(client, dono)).Perfis.Single(c => c.Id == waiter);
        Assert.Equal(["comanda:desconto", "mesa:ver"], column.Regras.Select(r => r.Permissao).Order(StringComparer.Ordinal));
        var limited = column.Regras.Single(r => r.Permissao == "comanda:desconto");
        Assert.Equal(5m, limited.MaxPercentual);
        Assert.Equal(10_000L, limited.TetoDiarioCentavos);

        var record = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_id, antes::text, depois::text FROM auditoria_registros WHERE acao = 'perfil:editar-regras'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
        Assert.Equal((DonoA, "sucesso", waiter.ToString("D")), (record.Item1, record.Item2, record.Item3));
        Assert.Contains("comanda:lancar-item", record.Item4, StringComparison.Ordinal);
        Assert.Contains("\"maxPercentual\": 5", record.Item5, StringComparison.Ordinal);
        Assert.DoesNotContain("Garçom", record.Item4 + record.Item5, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_edit_with_invalid_input_is_400_a_missing_profile_404_and_another_organizations_profile_404_with_nothing_changed()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var donoB = await TokenAsync(api, DonoB, OrgB, UnitB);
        var perfil = await CreateProfileAsync(api, "Para validar", new RegraDeAcesso(RuleEffect.Allow, "mesa:ver"));
        var path = $"/perfis/{perfil:D}/regras";

        foreach (var invalid in new object[]
        {
            new { regras = new[] { Regra("allow", "nao:existe") } },
            new { regras = new[] { Regra("talvez", "mesa:ver") } },
            new { regras = new[] { Regra("deny", "mesa:ver", maxPercentual: 5m) } },
            new { regras = new[] { Regra("allow", "comanda:desconto", maxPercentual: 101m) } },
            new { regras = (object?)null },
        })
        {
            using var response = await SendAsync(client, HttpMethod.Put, path, dono, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var missing = await SendAsync(client, HttpMethod.Put, $"/perfis/{Guid.NewGuid():D}/regras", dono, new { regras = Array.Empty<object>() }))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var foreign = await SendAsync(client, HttpMethod.Put, path, donoB, new { regras = Array.Empty<object>() }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        Assert.Equal(1, Assert.Single(await CountAsync(OrgA, UnitA, $"SELECT count(*) FROM perfil_regras WHERE perfil_id = '{perfil:D}'")));
        Assert.Equal(0, Assert.Single(await CountAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'perfil:editar-regras'")));
    }

    [Fact]
    public async Task The_grant_guard_refuses_a_rule_the_editor_lacks_or_holds_with_a_smaller_limit_and_each_refusal_is_audited()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var editorProfile = await CreateProfileAsync(api, "Editor", new RegraDeAcesso(RuleEffect.Allow, "perfil:gerir"), new RegraDeAcesso(RuleEffect.Allow, "mesa:ver"), new RegraDeAcesso(RuleEffect.Allow, "comanda:desconto", MaxPercent: 10m));
        Assert.Equal(ResultadoGestao.Ok, await api.Services.GetRequiredService<GestaoDePerfis>().AtribuirAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), Gerente, editorProfile));
        var target = await CreateProfileAsync(api, "Alvo da edicao", new RegraDeAcesso(RuleEffect.Allow, "mesa:ver"));
        var editor = await TokenAsync(api, Gerente, OrgA, UnitA);
        var path = $"/perfis/{target:D}/regras";

        using (var lacks = await SendAsync(client, HttpMethod.Put, path, editor, new { regras = new[] { Regra("allow", "caixa:abrir") } }))
        {
            await AssertForbiddenEmptyAsync(lacks);
        }

        using (var unlimited = await SendAsync(client, HttpMethod.Put, path, editor, new { regras = new[] { Regra("allow", "comanda:desconto") } }))
        {
            await AssertForbiddenEmptyAsync(unlimited);
        }

        using (var higher = await SendAsync(client, HttpMethod.Put, path, editor, new { regras = new[] { Regra("allow", "comanda:desconto", maxPercentual: 20m) } }))
        {
            await AssertForbiddenEmptyAsync(higher);
        }

        Assert.Equal("mesa:ver", Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT permissao FROM perfil_regras WHERE perfil_id = '{target:D}'", r => r.GetString(0))));
        var refusals = await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado FROM auditoria_registros WHERE acao = 'permissao:negar-escalada'", r => (r.GetGuid(0), r.GetString(1)));
        Assert.Equal(3, refusals.Count);
        Assert.All(refusals, r => Assert.Equal((Gerente, "negado"), r));

        // What the editor does hold, within its limit (and any deny), may be handed on.
        using var ok = await SendAsync(client, HttpMethod.Put, path, editor, new { regras = new[] { Regra("allow", "comanda:desconto", maxPercentual: 10m), Regra("deny", "mesa:ver") } });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }

    [Fact]
    public async Task Nobody_edits_a_profile_they_hold_nor_one_a_profile_they_hold_follows()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var template = await CreateProfileAsync(api, "Modelo base", new RegraDeAcesso(RuleEffect.Allow, "perfil:gerir"), new RegraDeAcesso(RuleEffect.Allow, "mesa:ver"));
        var gestoras = api.Services.GetRequiredService<GestaoDePerfis>();
        var owner = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var follower = await gestoras.DuplicarAsync(owner, template.ToString("D"), "Segue o modelo", acompanhar: true);
        Assert.Equal(ResultadoGestao.Ok, follower.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await gestoras.AtribuirAsync(owner, Gerente, follower.Id!.Value));
        var editor = await TokenAsync(api, Gerente, OrgA, UnitA);

        // The caller holds the follower, which gets the template's rules: editing the template would change the caller's own rights.
        using (var viaFollowed = await SendAsync(client, HttpMethod.Put, $"/perfis/{template:D}/regras", editor, new { regras = new[] { Regra("allow", "perfil:gerir") } }))
        {
            await AssertForbiddenEmptyAsync(viaFollowed);
        }

        using (var own = await SendAsync(client, HttpMethod.Put, $"/perfis/{follower.Id:D}/regras", editor, new { regras = new[] { Regra("allow", "mesa:ver") } }))
        {
            await AssertForbiddenEmptyAsync(own);
        }

        var refusals = await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado FROM auditoria_registros WHERE acao = 'permissao:negar-escalada'", r => (r.GetGuid(0), r.GetString(1)));
        Assert.Equal(2, refusals.Count);
    }

    [Fact]
    public async Task A_factory_profile_is_untouchable_and_no_deny_written_by_another_person_reaches_an_owner()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var shared = await CreateProfileAsync(api, "Compartilhado", new RegraDeAcesso(RuleEffect.Allow, "mesa:ver"));
        var withDeny = await CreateProfileAsync(api, "Com negacao", new RegraDeAcesso(RuleEffect.Deny, "mesa:ver"));
        var assigner = await CreateProfileAsync(api, "Quem atribui", new RegraDeAcesso(RuleEffect.Allow, "perfil:gerir"), new RegraDeAcesso(RuleEffect.Allow, "permissao:atribuir"), new RegraDeAcesso(RuleEffect.Allow, "mesa:ver"));
        var owner = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(owner, Gerente, assigner));
        var gestor = await TokenAsync(api, Gerente, OrgA, UnitA);

        // The factory profiles are shown in the matrix but only duplicable: no edit, not even by the owner.
        foreach (var key in new[] { "dono", "conta" })
        {
            using var factory = await SendAsync(client, HttpMethod.Put, $"/perfis/{key}/regras", dono, new { regras = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.Conflict, factory.StatusCode);
        }

        // A manager gives the shared profile to the owner (allowed: an allow only widens), then tries to put a deny in it or to hand the owner a profile with a deny.
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(new FakeRequest(Gerente, new OrganizationContext(OrgA, UnitA)), DonoA, shared));
        using (var denyInShared = await SendAsync(client, HttpMethod.Put, $"/perfis/{shared:D}/regras", gestor, new { regras = new[] { Regra("deny", "mesa:ver") } }))
        {
            await AssertForbiddenEmptyAsync(denyInShared);
        }

        using (var denyToOwner = await SendAsync(client, HttpMethod.Put, $"/contas/{DonoA:D}/perfis/{withDeny:D}", gestor))
        {
            await AssertForbiddenEmptyAsync(denyToOwner);
        }

        using (var denyOverride = await SendAsync(client, HttpMethod.Put, $"/contas/{DonoA:D}/overrides", gestor, new { regras = new[] { Regra("deny", "mesa:ver") } }))
        {
            await AssertForbiddenEmptyAsync(denyOverride);
        }

        Assert.Equal("allow", Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT efeito FROM perfil_regras WHERE perfil_id = '{shared:D}'", r => r.GetString(0))));
        // Giving the owner a profile ended the owner's old session (credential version); a fresh one still reaches everything.
        using var stillWorks = await SendAsync(client, HttpMethod.Get, "/mesas", await TokenAsync(api, DonoA, OrgA, UnitA));
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task An_explicit_deny_in_an_edited_profile_wins_over_an_allow_for_the_holder_on_the_next_request()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var dono = await TokenAsync(api, DonoA, OrgA, UnitA);
        var perfil = await CreateProfileAsync(api, "Para negar", new RegraDeAcesso(RuleEffect.Allow, "mesa:ver"));
        Assert.Equal(ResultadoGestao.Ok, await api.Services.GetRequiredService<GestaoDePerfis>().AtribuirAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), Alvo, perfil));
        var before = await TokenAsync(api, Alvo, OrgA, UnitA);
        using (var allowed = await SendAsync(client, HttpMethod.Get, "/mesas", before))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using (var edited = await SendAsync(client, HttpMethod.Put, $"/perfis/{perfil:D}/regras", dono, new { regras = new[] { Regra("allow", "mesa:ver"), Regra("deny", "mesa:ver") } }))
        {
            Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        }

        using (var ended = await SendAsync(client, HttpMethod.Get, "/mesas", before))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        }

        var fresh = await TokenAsync(api, Alvo, OrgA, UnitA);
        using var denied = await SendAsync(client, HttpMethod.Get, "/mesas", fresh);
        await AssertForbiddenEmptyAsync(denied);
    }
}
