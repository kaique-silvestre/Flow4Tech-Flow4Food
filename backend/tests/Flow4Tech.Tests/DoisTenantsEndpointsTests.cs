using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flow4Tech.Api;
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
/// Ticket 11, spec section 6 ("dois tenants"): the whole authenticated route end to end for two organizations. Real login in each one (HTTP, real tokens, real sessions), the
/// application credential (RLS on, no bypass) and real PostgreSQL. A person of organization A neither reads nor changes anything of B: units, sessions, settings, profiles, invitations,
/// accounts and the audit trail. A table sweep closes what the scenarios do not name.
/// </summary>
public sealed class DoisTenantsEndpointsTests : IClassFixture<DoisTenantsDatabase>
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";
    private const string Csrf = "X-F4T-Csrf";
    private const string DonoSenha = "Senha-do-dono-123";
    private const string NovaSenha = "senha-longa-da-pessoa-2026";
    private const string Inatividade = "sessao.inatividade-horas";

    private readonly DoisTenantsDatabase _db;

    public DoisTenantsEndpointsTests(DoisTenantsDatabase db) => _db = db;

    // ---- scenarios ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_of_one_organization_neither_sees_nor_changes_units_sessions_or_accounts_of_the_other()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var loginA = await LoginAsync(client, a.OwnerEmail, DonoSenha);
        var loginB = await LoginAsync(client, b.OwnerEmail, DonoSenha);
        var opA = await LoginOperationalAsync(client, a, a.Operational);

        // Units: each one lists only its own, and a switch to the other's unit is refused (403, the destination is validated against the person's own links).
        Assert.Equal([a.Unit], await UnitsAsync(client, loginA.AccessToken));
        Assert.Equal([b.Unit], await UnitsAsync(client, loginB.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, HttpMethod.Post, "/sessao/unidade-ativa", loginA.AccessToken, new { unidadeDestinoId = b.Unit }));

        // Sessions: the "all sessions" list of A has A's sessions and none of B's.
        var todasA = await SessionsAsync(client, loginA.AccessToken, "/sessoes/todas");
        Assert.Contains(todasA, s => s.Id == loginA.SessaoId);
        Assert.Contains(todasA, s => s.Id == opA.SessaoId);
        Assert.DoesNotContain(todasA, s => s.Id == loginB.SessaoId);
        Assert.All(todasA, s => Assert.Contains(s.ContaId, new[] { a.Owner, a.Operational.Id }));

        // Ending a session of B with A's token finds nothing (RLS), and B's session keeps working.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/sessoes/{loginB.SessaoId}/revogar", loginA.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "/unidades", loginB.AccessToken));

        // Accounts: B's owner cannot reset the access of A's operational account (404) and the account keeps its password.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/contas/{a.Operational.Id}/redefinir-acesso", loginB.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Post, "/login", null, new { organizacaoDoLogin = a.Id, usuario = a.Operational.Usuario, senha = NovaSenha }, csrf: true));
    }

    [Fact]
    public async Task Settings_are_per_organization_and_one_cannot_read_change_or_restore_the_others()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var tokenA = (await LoginAsync(client, a.OwnerEmail, DonoSenha)).AccessToken;
        var tokenB = (await LoginAsync(client, b.OwnerEmail, DonoSenha)).AccessToken;

        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(client, HttpMethod.Put, $"/configuracoes/{Inatividade}", tokenA, new { camada = "organizacao", valor = "6", motivo = "turno curto" }));

        Assert.Equal("6", (await SettingAsync(client, tokenA)).Valor);
        var seenByB = await SettingAsync(client, tokenB);
        Assert.Equal("12", seenByB.Valor);
        Assert.Equal("sistema", seenByB.Origem);
        Assert.Null(seenByB.ValorDaOrganizacao);

        // History: A has one version, B has none, and B cannot restore A's version 1.
        Assert.Single(await HistoryAsync(client, tokenA));
        Assert.Empty(await HistoryAsync(client, tokenB));
        Assert.NotEqual(HttpStatusCode.NoContent, await StatusAsync(client, HttpMethod.Post, $"/configuracoes/{Inatividade}/restaurar", tokenB, new { camada = "organizacao", versao = 1 }));

        // B writes its own value; A keeps 6 and its history stays a single version.
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(client, HttpMethod.Put, $"/configuracoes/{Inatividade}", tokenB, new { camada = "organizacao", valor = "3" }));
        Assert.Equal("6", (await SettingAsync(client, tokenA)).Valor);
        Assert.Single(await HistoryAsync(client, tokenA));
        Assert.Equal("3", (await SettingAsync(client, tokenB)).Valor);

        // Audit: each organization's trail carries its own change, written by its own person, and nothing of the other.
        var trailA = await QueryAsync(a, "SELECT usuario_id FROM auditoria_registros WHERE acao = 'configuracao:alterar'", r => r.GetGuid(0));
        var trailB = await QueryAsync(b, "SELECT usuario_id FROM auditoria_registros WHERE acao = 'configuracao:alterar'", r => r.GetGuid(0));
        Assert.Equal([a.Owner], trailA);
        Assert.Equal([b.Owner], trailB);
    }

    [Fact]
    public async Task Profiles_and_invitations_of_one_organization_do_not_exist_for_the_other()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var tokenA = (await LoginAsync(client, a.OwnerEmail, DonoSenha)).AccessToken;
        var tokenB = (await LoginAsync(client, b.OwnerEmail, DonoSenha)).AccessToken;
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();

        // No HTTP route manages profiles yet (pending, see the ticket): the service is the path, called with each owner's real request.
        var criado = await perfis.CriarAsync(a.RequestOfOwner(), "Gerente A", [new RegraDeAcesso(RuleEffect.Allow, "organizacao:ver-unidades")]);
        Assert.Equal(ResultadoGestao.Ok, criado.Resultado);
        var perfilA = criado.Id!.Value;
        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM perfis", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(b, "SELECT count(*) FROM perfis", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(b, "SELECT count(*) FROM perfil_regras", r => r.GetInt64(0))).Single());

        // B cannot hand A's profile to its own person, nor can A hand it to B's person; nothing is written.
        Assert.Equal(ResultadoGestao.NaoEncontrado, await perfis.AtribuirAsync(b.RequestOfOwner(), b.Operational.Id, perfilA));
        Assert.NotEqual(ResultadoGestao.Ok, await perfis.AtribuirAsync(a.RequestOfOwner(), b.Operational.Id, perfilA));
        Assert.Equal(0L, (await QueryAsync(a, "SELECT count(*) FROM usuario_perfis", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(b, "SELECT count(*) FROM usuario_perfis", r => r.GetInt64(0))).Single());
        Assert.NotEqual(ResultadoGestao.Ok, await perfis.SubstituirRegrasAsync(b.RequestOfOwner(), perfilA.ToString("D"), [new RegraDeAcesso(RuleEffect.Deny, "organizacao:ver-unidades")]));
        Assert.NotEqual(ResultadoGestao.Ok, await perfis.DefinirOverridesAsync(b.RequestOfOwner(), a.Operational.Id, [new RegraDeAcesso(RuleEffect.Deny, "organizacao:ver-unidades")]));

        // Invitations: A invites; B neither sees nor revokes it, and cannot attach A's profile or unit to its own invitation (400).
        using var invite = await SendAsync(client, HttpMethod.Post, "/convites", tokenA, new { email = $"convidado-{Guid.NewGuid():N}@a.test", nome = "Convidado" });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var inviteId = (await invite.Content.ReadFromJsonAsync<ConviteDto>())!.Id;
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/convites/{inviteId}/revogar", tokenB));
        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM convites WHERE revogado_em IS NULL", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(b, "SELECT count(*) FROM convites", r => r.GetInt64(0))).Single());
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, HttpMethod.Post, "/convites", tokenB, new { email = $"x-{Guid.NewGuid():N}@b.test", nome = "X", perfilId = perfilA }));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, HttpMethod.Post, "/convites", tokenB, new { email = $"y-{Guid.NewGuid():N}@b.test", nome = "Y", unidadeInicialId = a.Unit }));
    }

    [Fact]
    public async Task The_authenticated_route_answers_401_without_a_session_and_403_without_the_permission()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var tokenOwner = (await LoginAsync(client, a.OwnerEmail, DonoSenha)).AccessToken;
        var tokenOp = (await LoginOperationalAsync(client, a, a.Operational)).AccessToken;

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, "/configuracoes", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, "/configuracoes", "token-invalido"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "/configuracoes", tokenOwner));

        // The operational account is a person of the same organization with the factory profile "conta": own units yes, administration no.
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "/unidades", tokenOp));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, HttpMethod.Get, "/configuracoes", tokenOp));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, HttpMethod.Get, "/sessoes/todas", tokenOp));
        using var denied = await SendAsync(client, HttpMethod.Get, "/configuracoes", tokenOp);
        Assert.Empty(await denied.Content.ReadAsByteArrayAsync()); // 403 without a reason
    }

    [Fact]
    public async Task No_table_with_an_organization_returns_a_row_of_another_organization_or_any_row_without_context()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var tokenA = (await LoginAsync(client, a.OwnerEmail, DonoSenha)).AccessToken;
        _ = await LoginAsync(client, b.OwnerEmail, DonoSenha);
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(client, HttpMethod.Put, $"/configuracoes/{Inatividade}", tokenA, new { camada = "organizacao", valor = "5" }));
        // The salão tables hold rows of both organizations before the sweep, so the sweep is not vacuous for them.
        var salaoSweepA = await RunSalaoAsync(client, a);
        var salaoSweepB = await RunSalaoAsync(client, b);
        _ = await RunCaixaCompletoAsync(client, salaoSweepA);
        _ = await RunCaixaCompletoAsync(client, salaoSweepB);
        // A unit price in each organization, so the sweep of the new table is not vacuous either.
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Put, $"/cardapio/produtos/{salaoSweepA.ProdutoId}/preco-unidade", salaoSweepA.Token, new { precoCentavos = 1300 }));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Put, $"/cardapio/produtos/{salaoSweepB.ProdutoId}/preco-unidade", salaoSweepB.Token, new { precoCentavos = 1400 }));
        _ = await api.Services.GetRequiredService<GestaoDePerfis>().CriarAsync(a.RequestOfOwner(), "Qualquer", [new RegraDeAcesso(RuleEffect.Allow, "organizacao:ver-unidades")]);

        // Tables are discovered, not listed: a new tenant table is swept without editing this test.
        var tables = await ListTablesAsync();
        Assert.Contains("sessoes", tables);
        Assert.Contains("configuracoes_versoes", tables);
        Assert.Contains("auditoria_registros", tables);
        Assert.Contains("convites", tables);
        Assert.Contains("perfis", tables);
        Assert.Contains("comandas", tables);
        Assert.Contains("rodadas", tables);
        Assert.Contains("pagamentos", tables);
        Assert.Contains("turnos_caixa", tables);
        Assert.Contains("movimentos_caixa", tables);
        Assert.Contains("contagens_turno", tables);
        Assert.Contains("aprovacoes_diferenca", tables);
        Assert.Contains("produtos_preco_unidade", tables);
        Assert.Contains("metodos_pagamento", tables);
        Assert.Contains("grupos_opcao", tables);
        Assert.Contains("item_opcoes", tables);
        Assert.Contains("opcoes_frequencia", tables);
        Assert.Contains("produtos_frequencia_garcom", tables);
        foreach (var tenant in new[] { a, b })
        {
            foreach (var table in tables)
            {
                var foreign = await QueryAsync(tenant, $"SELECT count(*) FROM {table} WHERE organizacao_id <> @o", r => r.GetInt64(0), ("o", tenant.Id));
                Assert.True(foreign.Single() == 0, $"{table} returned a row of another organization to {tenant.Id}");
            }

            // The sweep is not vacuous: the tenant does see its own rows.
            Assert.Equal(2L, (await QueryAsync(tenant, "SELECT count(*) FROM contas", r => r.GetInt64(0))).Single());
            Assert.True((await QueryAsync(tenant, "SELECT count(*) FROM sessoes", r => r.GetInt64(0))).Single() >= 1);
            Assert.Equal(1L, (await QueryAsync(tenant, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
            Assert.Equal(1L, (await QueryAsync(tenant, "SELECT count(*) FROM pagamentos", r => r.GetInt64(0))).Single());
            Assert.Equal(7L, (await QueryAsync(tenant, "SELECT count(*) FROM metodos_pagamento", r => r.GetInt64(0))).Single());
            Assert.Equal(1L, (await QueryAsync(tenant, "SELECT count(*) FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());
            Assert.Equal((2L, 1L, 1L), (await QueryAsync(tenant, "SELECT (SELECT count(*) FROM movimentos_caixa), (SELECT count(*) FROM contagens_turno), (SELECT count(*) FROM aprovacoes_diferenca)", r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)))).Single());
            foreach (var optionTable in new[] { "grupos_opcao", "opcoes", "produto_grupos_opcao", "item_opcoes", "opcoes_frequencia", "produtos_frequencia_garcom" })
            {
                Assert.Equal(1L, (await QueryAsync(tenant, $"SELECT count(*) FROM {optionTable}", r => r.GetInt64(0))).Single());
            }

            Assert.Equal(0L, (await QueryAsync(tenant, "SELECT count(*) FROM organizacoes WHERE id <> @o", r => r.GetInt64(0), ("o", tenant.Id))).Single());
            Assert.Equal(1L, (await QueryAsync(tenant, "SELECT count(*) FROM organizacoes", r => r.GetInt64(0))).Single());
        }

        // Without any context the application credential reads nothing at all.
        await using var source = NpgsqlDataSource.Create(_db.AppConnection);
        foreach (var table in tables.Append("organizacoes"))
        {
            await using var command = source.CreateCommand($"SELECT count(*) FROM {table}");
            Assert.True((long)(await command.ExecuteScalarAsync())! == 0, $"{table} returned rows without an organization context");
        }
    }

    [Fact]
    public async Task The_salao_flow_runs_in_each_organization_and_one_cannot_see_or_touch_the_others_menu_comandas_or_payments()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);

        // The whole flow in each organization: shift, menu, comanda, round, cash payment with change, close the comanda, close the shift.
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        // The other organization neither lists nor reads nor pays what is not its own: every answer is "not found", never the data.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/comandas/{salaoA.ComandaId}/conta", salaoB.Token));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/comandas/{salaoA.ComandaId}/itens", salaoB.Token));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/comandas/{salaoA.ComandaId}/pagamentos", salaoB.Token));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{salaoA.ComandaId}/pagamentos", salaoB.Token, new { pagamentoId = Guid.CreateVersion7(), recebidoCentavos = 100, versao = 1 }));
        // Nor does it read, change, link or launch with the other's option groups: B's own product has no such group, and an option of A is simply not an option for B.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{salaoA.GrupoId}", salaoB.Token, new { nome = "Invadido", minimo = 0, maximo = 1, ativo = true, versao = 1 }));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, HttpMethod.Put, $"/cardapio/produtos/{salaoB.ProdutoId}/grupos-opcao", salaoB.Token, new { grupoIds = new[] { salaoA.GrupoId } }));
        using (var grupos = await SendAsync(client, HttpMethod.Get, "/cardapio/grupos-opcao", salaoB.Token))
        {
            var lista = (await grupos.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>())!;
            Assert.Equal(salaoB.GrupoId, Assert.Single(lista).GetProperty("id").GetGuid());
        }

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{salaoA.ComandaId}/rodadas", salaoB.Token, new { rodadaId = Guid.CreateVersion7(), versao = 1, itens = new[] { new { produtoId = salaoB.ProdutoId, quantidade = 1 } } }));
        using (var produtos = await SendAsync(client, HttpMethod.Get, "/cardapio/produtos", salaoB.Token))
        {
            var lista = (await produtos.Content.ReadFromJsonAsync<List<ProdutoDto>>())!;
            Assert.Equal([salaoB.ProdutoId], lista.Select(p => p.Id));
        }

        // Each organization's cash is its own: each shift held its float plus its own cash only, and A's shift is closed (no open shift answers 404).
        Assert.Equal(10_000 + 2_970, salaoA.EsperadoCentavos);
        Assert.Equal(10_000 + 2_970, salaoB.EsperadoCentavos);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, "/caixa/turnos/atual", salaoA.Token));
    }

    [Fact]
    public async Task The_complete_cash_register_runs_in_each_organization_and_one_cannot_see_or_decide_the_shifts_of_the_other()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        var turnoA = await RunCaixaCompletoAsync(client, salaoA);
        var turnoB = await RunCaixaCompletoAsync(client, salaoB);

        // Each organization holds its own movements, count and approval, and nothing of the other's.
        foreach (var (tenant, turno) in new[] { (a, turnoA), (b, turnoB) })
        {
            Assert.Equal((2L, 1L, 1L, 1L), (await QueryAsync(tenant, $"SELECT (SELECT count(*) FROM movimentos_caixa WHERE turno_id = '{turno}'), (SELECT count(*) FROM contagens_turno WHERE turno_id = '{turno}'), (SELECT count(*) FROM aprovacoes_diferenca WHERE turno_id = '{turno}'), (SELECT count(*) FROM aprovacoes_diferenca)", r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)))).Single());
        }

        // The other organization neither reads, approves nor recounts a shift that is not its own: always "not found".
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/caixa/turnos/{turnoA}", salaoB.Token));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/caixa/turnos/{turnoA}/diferenca/aprovar", salaoB.Token, new { motivo = "Invasao" }));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/caixa/turnos/{turnoA}/diferenca/recontar", salaoB.Token, new { contadoCentavos = 1, motivo = "Invasao" }));
        using var historico = await SendAsync(client, HttpMethod.Get, "/caixa/historico", salaoB.Token);
        Assert.DoesNotContain(turnoA, (await historico.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>())!.Select(t => t.GetProperty("id").GetGuid()));
        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM aprovacoes_diferenca", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Quick_launch_data_runs_in_each_organization_and_nothing_of_it_crosses_to_the_other()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        // Each organization: a table, a comanda on it, a round, "repeat the last round", the cash payment and the close (a new shift, the first one is closed).
        var rapidoA = await RunRapidoAsync(client, salaoA);
        var rapidoB = await RunRapidoAsync(client, salaoB);

        // Favorites (from the launches above), search (same short code in both) and the table card answer each organization's own rows only.
        foreach (var (salao, rapido) in new[] { (salaoA, rapidoA), (salaoB, rapidoB) })
        {
            using var favoritos = await SendAsync(client, HttpMethod.Get, "/cardapio/produtos/favoritos", salao.Token);
            Assert.Equal([salao.ProdutoId], (await favoritos.Content.ReadFromJsonAsync<List<ProdutoDto>>())!.Select(p => p.Id));
            using var busca = await SendAsync(client, HttpMethod.Get, "/cardapio/produtos/busca?q=chopp", salao.Token);
            Assert.Equal([salao.ProdutoId], (await busca.Content.ReadFromJsonAsync<List<ProdutoDto>>())!.Select(p => p.Id));
            using var codigo = await SendAsync(client, HttpMethod.Get, "/cardapio/produtos/busca?q=CHOPP", salao.Token);
            Assert.Equal([salao.ProdutoId], (await codigo.Content.ReadFromJsonAsync<List<ProdutoDto>>())!.Select(p => p.Id));
            using var ultima = await SendAsync(client, HttpMethod.Get, $"/mesas/{rapido.MesaId}/ultima-vez", salao.Token);
            var card = await ultima.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(rapido.ComandaId, card.GetProperty("comandaId").GetGuid());
            Assert.Equal(2, card.GetProperty("itens").GetArrayLength());
        }

        // The other organization cannot read the card of this table, repeat this comanda, or find its product.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/mesas/{rapidoA.MesaId}/ultima-vez", salaoB.Token));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/mesas/{rapidoB.MesaId}/ultima-vez", salaoA.Token));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{rapidoA.ComandaId}/rodadas/repetir", salaoB.Token, new { rodadaId = Guid.CreateVersion7(), versao = 1 }));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{rapidoB.ComandaId}/rodadas/repetir", salaoA.Token, new { rodadaId = Guid.CreateVersion7(), versao = 1 }));
        Assert.NotEqual(salaoA.ProdutoId, salaoB.ProdutoId);
        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM produtos_frequencia_garcom", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(a, "SELECT count(*) FROM produtos_frequencia_garcom WHERE produto_id = @p", r => r.GetInt64(0), ("p", salaoB.ProdutoId))).Single());
        Assert.Equal(3L, (await QueryAsync(a, "SELECT lancamentos FROM produtos_frequencia_garcom", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Areas_the_table_map_and_moving_a_comanda_run_in_each_organization_and_nothing_of_it_crosses_to_the_other()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        // The same names in both organizations: uniqueness is per unit, never global.
        var mapaA = await RunMapaAsync(client, salaoA);
        var mapaB = await RunMapaAsync(client, salaoB);

        foreach (var (salao, mapa) in new[] { (salaoA, mapaA), (salaoB, mapaB) })
        {
            using var response = await SendAsync(client, HttpMethod.Get, "/mesas/mapa", salao.Token);
            var corpo = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var area = Assert.Single(corpo.GetProperty("areas").EnumerateArray());
            Assert.Equal((mapa.AreaId, "Varanda"), (area.GetProperty("id").GetGuid(), area.GetProperty("nome").GetString()));
            var mesas = area.GetProperty("mesas").EnumerateArray().ToList();
            Assert.Equal(mapa.MesaDestino, Assert.Single(mesas).GetProperty("id").GetGuid());
            Assert.Equal(("ocupada", 1), (mesas[0].GetProperty("status").GetString(), mesas[0].GetProperty("comandasAbertas").GetInt32()));
            var solta = corpo.GetProperty("semArea").EnumerateArray().Single();
            Assert.Equal((mapa.MesaOrigem, "livre"), (solta.GetProperty("id").GetGuid(), solta.GetProperty("status").GetString()));
        }

        // The other organization neither reaches this area or table nor can move this comanda, and its own rows are untouched.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Put, $"/mesas/areas/{mapaA.AreaId}", salaoB.Token, new { nome = "Roubada", ativa = false }));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Put, $"/mesas/{mapaA.MesaOrigem}/area", salaoB.Token, new { areaId = mapaB.AreaId }));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Put, $"/mesas/{mapaB.MesaOrigem}/area", salaoB.Token, new { areaId = mapaA.AreaId }));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{mapaA.ComandaId}/trocar-mesa", salaoB.Token, new { mesaId = mapaB.MesaOrigem, versao = 2 }));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{mapaB.ComandaId}/trocar-mesa", salaoB.Token, new { mesaId = mapaA.MesaOrigem, versao = 2 }));
        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM areas", r => r.GetInt64(0))).Single());
        Assert.Equal(mapaA.MesaDestino, (await QueryAsync(a, "SELECT mesa_id FROM comandas WHERE id = @c", r => r.GetGuid(0), ("c", mapaA.ComandaId))).Single());
    }

    [Fact]
    public async Task Editing_the_history_and_cancelling_a_comanda_run_in_each_organization_and_one_cannot_reach_the_others()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        var edicaoA = await RunEdicaoAsync(client, salaoA);
        var edicaoB = await RunEdicaoAsync(client, salaoB);

        // Each organization sees only its own timeline: open, launch, rename, alter item, data, cancel.
        foreach (var (salao, edicao) in new[] { (salaoA, edicaoA), (salaoB, edicaoB) })
        {
            using var historico = await SendAsync(client, HttpMethod.Get, $"/comandas/{edicao.ComandaId}/historico", salao.Token);
            var eventos = (await historico.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("eventos").EnumerateArray().Select(e => e.GetProperty("acao").GetString()).ToList();
            Assert.Equal(["comanda:abrir", "comanda:lancar-rodada", "comanda:renomear", "comanda:alterar-item", "comanda:alterar-dados", "comanda:cancelar"], eventos);
        }

        // The other organization neither reads nor changes it, and the rows stay as the owner left them.
        foreach (var (intruso, alvo) in new[] { (salaoB.Token, edicaoA), (salaoA.Token, edicaoB) })
        {
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/comandas/{alvo.ComandaId}/historico", intruso));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/itens/{alvo.ItemId}/alterar", intruso, new { versao = alvo.Versao, quantidade = 9 }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/renomear", intruso, new { versao = alvo.Versao, identificacao = "Invadida" }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/dados", intruso, new { versao = alvo.Versao, pessoas = 9, observacao = "x" }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/trocar-garcom", intruso, new { versao = alvo.Versao, garcomId = Guid.NewGuid() }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/cancelar", intruso, new { versao = alvo.Versao, motivo = "cliente-desistiu" }));
        }

        Assert.Equal(("Ana e Beto 2", 4, "cancelada"), (await QueryAsync(a, "SELECT identificacao, pessoas, estado FROM comandas WHERE id = @c", r => (r.GetString(0), r.GetInt32(1), r.GetString(2)), ("c", edicaoA.ComandaId))).Single());
        Assert.Equal(2, (await QueryAsync(a, "SELECT quantidade FROM itens_comanda WHERE id = @i", r => r.GetInt32(0), ("i", edicaoA.ItemId))).Single());
    }

    [Fact]
    public async Task Discounts_courtesies_and_gifts_run_in_each_organization_and_one_cannot_reach_the_others()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        var descontoA = await RunDescontoAsync(client, salaoA);
        var descontoB = await RunDescontoAsync(client, salaoB);

        // Each organization sees only its own discount; the bill is the same arithmetic in both (see RunDescontoAsync).
        foreach (var (salao, alvo) in new[] { (salaoA, descontoA), (salaoB, descontoB) })
        {
            using var lista = await SendAsync(client, HttpMethod.Get, $"/comandas/{alvo.ComandaId}/descontos", salao.Token);
            Assert.Equal(alvo.DescontoId, (await lista.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).EnumerateArray().Single().GetProperty("id").GetGuid());
        }

        foreach (var (intruso, alvo) in new[] { (salaoB.Token, descontoA), (salaoA.Token, descontoB) })
        {
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/comandas/{alvo.ComandaId}/descontos", intruso));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/descontos", intruso, new { versao = alvo.Versao, percentual = 5m, motivo = "x" }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/itens/{alvo.ItemId}/cortesia", intruso, new { versao = alvo.Versao, motivo = "x" }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/itens/{alvo.ItemId}/brinde", intruso, new { versao = alvo.Versao, motivo = "x" }));
        }

        // The visibility question is answered per session: each organization's owner holds every rule in his own organization, nothing of the other's reaches him.
        foreach (var salao in new[] { salaoA, salaoB })
        {
            using var regras = await SendAsync(client, HttpMethod.Get, "/sessao/regras-de-desconto", salao.Token);
            Assert.Equal("""{"desconto":true,"cortesia":true,"brinde":true}""", await regras.Content.ReadAsStringAsync());
        }

        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM descontos_comanda", r => r.GetInt64(0))).Single());
        Assert.Equal(("cortesia", 0L), (await QueryAsync(a, "SELECT tipo, 0::bigint FROM itens_comanda WHERE id = @i", r => (r.GetString(0), r.GetInt64(1)), ("i", descontoA.ItemId))).Single());
    }

    [Fact]
    public async Task Transfers_merges_and_separations_run_in_each_organization_and_one_cannot_reach_the_others()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        var movidoA = await RunMovimentoAsync(client, salaoA);
        var movidoB = await RunMovimentoAsync(client, salaoB);

        // Each organization ends with the same shape: the comanda of the salao flow, X (open, one item), Y (merged into X, no live item), Z (new, one item).
        foreach (var (tenant, movido) in new[] { (a, movidoA), (b, movidoB) })
        {
            Assert.Equal(("aberta", 1L), (await QueryAsync(tenant, "SELECT estado, (SELECT count(*) FROM itens_comanda WHERE comanda_id = @x) FROM comandas WHERE id = @x", r => (r.GetString(0), r.GetInt64(1)), ("x", movido.X))).Single());
            Assert.Equal(("juntada", movido.X), (await QueryAsync(tenant, "SELECT estado, juntada_na_comanda_id FROM comandas WHERE id = @y", r => (r.GetString(0), r.GetGuid(1)), ("y", movido.Y))).Single());
            Assert.Equal(1L, (await QueryAsync(tenant, "SELECT count(*) FROM itens_comanda WHERE comanda_id = @z", r => r.GetInt64(0), ("z", movido.Z))).Single());
            Assert.Equal(4L, (await QueryAsync(tenant, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
            Assert.Equal(1L, (await QueryAsync(tenant, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'comanda.juntada'", r => r.GetInt64(0))).Single());
        }

        // The other organization neither moves, merges nor separates them, and the rows stay as the owner left them.
        foreach (var (intruso, alvo) in new[] { (salaoB.Token, movidoA), (salaoA.Token, movidoB) })
        {
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.X}/itens/transferir", intruso, new { versao = alvo.VersaoX, destinoId = alvo.Z, destinoVersao = alvo.VersaoZ, itemIds = new[] { alvo.ItemId } }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.X}/juntar", intruso, new { versao = alvo.VersaoX, origemId = alvo.Z, origemVersao = alvo.VersaoZ }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.X}/itens/separar", intruso, new { versao = alvo.VersaoX, itemIds = new[] { alvo.ItemId }, identificacao = (string?)null }));
        }

        Assert.Equal(4L, (await QueryAsync(a, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM itens_comanda WHERE comanda_id = @x", r => r.GetInt64(0), ("x", movidoA.X))).Single());
    }

    private sealed record Movido(Guid X, Guid Y, Guid Z, Guid ItemId, int VersaoX, int VersaoZ);

    /// <summary>One organization as its founder: X launches two items, one goes to Y, Y is merged back into X, the other is separated into a new comanda Z.</summary>
    private static async Task<Movido> RunMovimentoAsync(HttpClient client, Salao salao)
    {
        async Task<System.Text.Json.JsonElement> PostAsync(string caminho, object corpo, HttpStatusCode esperado)
        {
            using var response = await SendAsync(client, HttpMethod.Post, caminho, salao.Token, corpo);
            Assert.Equal(esperado, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        }

        async Task<ComandaCriadaDto> AbrirAsync(string nome)
        {
            using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", salao.Token, new { identificacao = nome, mesaId = (Guid?)null, pessoas = (int?)null });
            Assert.Equal(HttpStatusCode.Created, aberta.StatusCode);
            return (await aberta.Content.ReadFromJsonAsync<ComandaCriadaDto>())!;
        }

        var x = await AbrirAsync("Movimento X");
        var y = await AbrirAsync("Movimento Y");
        var primeira = await PostAsync($"/comandas/{x.Id}/rodadas", new { rodadaId = Guid.CreateVersion7(), versao = x.Versao, itens = new[] { new { produtoId = salao.ProdutoId, quantidade = 1, opcaoIds = new[] { salao.OpcaoId } } } }, HttpStatusCode.Created);
        var segunda = await PostAsync($"/comandas/{x.Id}/rodadas", new { rodadaId = Guid.CreateVersion7(), versao = primeira.GetProperty("comandaVersao").GetInt32(), itens = new[] { new { produtoId = salao.ProdutoId, quantidade = 2, opcaoIds = new[] { salao.OpcaoId } } } }, HttpStatusCode.Created);
        var (item1, item2) = (primeira.GetProperty("itens")[0].GetProperty("id").GetGuid(), segunda.GetProperty("itens")[0].GetProperty("id").GetGuid());
        var versaoX = segunda.GetProperty("comandaVersao").GetInt32();

        var transferido = await PostAsync($"/comandas/{x.Id}/itens/transferir", new { versao = versaoX, destinoId = y.Id, destinoVersao = y.Versao, itemIds = new[] { item1 } }, HttpStatusCode.OK);
        Assert.Equal(1, transferido.GetProperty("itensMovidos").GetInt32());
        var (vx, vy) = (transferido.GetProperty("origem").GetProperty("versao").GetInt32(), transferido.GetProperty("destino").GetProperty("versao").GetInt32());

        var juntado = await PostAsync($"/comandas/{x.Id}/juntar", new { versao = vx, origemId = y.Id, origemVersao = vy }, HttpStatusCode.OK);
        Assert.Equal(("juntada", 1), (juntado.GetProperty("origem").GetProperty("estado").GetString(), juntado.GetProperty("itensMovidos").GetInt32()));
        vx = juntado.GetProperty("destino").GetProperty("versao").GetInt32();

        var separado = await PostAsync($"/comandas/{x.Id}/itens/separar", new { versao = vx, itemIds = new[] { item2 }, identificacao = "Separada" }, HttpStatusCode.Created);
        var z = separado.GetProperty("destino");
        Assert.Equal(("Separada", 2), (z.GetProperty("identificacao").GetString(), z.GetProperty("versao").GetInt32()));
        return new Movido(x.Id, y.Id, z.GetProperty("id").GetGuid(), item1, separado.GetProperty("origem").GetProperty("versao").GetInt32(), 2);
    }

    [Fact]
    public async Task Splitting_the_bill_runs_in_each_organization_and_one_cannot_preview_record_or_read_the_others()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        var divididoA = await RunDivisaoAsync(client, salaoA);
        var divididoB = await RunDivisaoAsync(client, salaoB);

        // Each organization ends with its own recorded split (two parts that add up to the bill) and one event; the other one sees none of it.
        foreach (var (tenant, dividido) in new[] { (a, divididoA), (b, divididoB) })
        {
            Assert.Equal((1L, 2L, dividido.Total), (await QueryAsync(tenant, "SELECT (SELECT count(*) FROM divisoes), (SELECT count(*) FROM divisao_partes), (SELECT sum(total_centavos) FROM divisao_partes)", r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)))).Single());
            Assert.Equal(1L, (await QueryAsync(tenant, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'comanda.dividida'", r => r.GetInt64(0))).Single());
            Assert.Equal(1L, (await QueryAsync(tenant, "SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:dividir'", r => r.GetInt64(0))).Single());
        }

        foreach (var (intruso, alvo) in new[] { (salaoB.Token, divididoA), (salaoA.Token, divididoB) })
        {
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/divisao/previa", intruso, new { modo = "iguais", partes = 2 }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/divisao", intruso, new { divisaoId = Guid.CreateVersion7(), versao = alvo.Versao, pedido = new { modo = "iguais", partes = 2 } }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/comandas/{alvo.ComandaId}/divisao", intruso));
        }

        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM divisoes", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(b, "SELECT count(*) FROM divisoes", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Receiving_with_several_methods_runs_in_each_organization_with_its_own_methods_and_one_cannot_read_pay_or_rename_the_others()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var salaoA = await RunSalaoAsync(client, a);
        var salaoB = await RunSalaoAsync(client, b);

        var recebidoA = await RunRecebimentoMistoAsync(client, salaoA);
        var recebidoB = await RunRecebimentoMistoAsync(client, salaoB);

        // Each organization ends with its own methods (the 7 of the factory plus its own "Cheque"), its own receipt of three lines and its own expected cash (only the cash applied).
        foreach (var (tenant, recebido) in new[] { (a, recebidoA), (b, recebidoB) })
        {
            Assert.Equal((8L, 1L, 1L), (await QueryAsync(tenant, "SELECT (SELECT count(*) FROM metodos_pagamento), (SELECT count(*) FROM metodos_pagamento WHERE nome = 'Pix QR'), (SELECT count(*) FROM metodos_pagamento WHERE nome = 'Cheque')", r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)))).Single());
            Assert.Equal((4L, 3L, 200L), (await QueryAsync(tenant, "SELECT count(*), count(*) FILTER (WHERE comanda_id = @c), coalesce(sum(gorjeta_centavos), 0)::bigint FROM pagamentos", r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), ("c", recebido.ComandaId))).Single());
            Assert.Equal(3L, (await QueryAsync(tenant, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'pagamento.registrado' AND payload->>'ComandaId' = @c", r => r.GetInt64(0), ("c", recebido.ComandaId.ToString()))).Single());
            Assert.Equal(recebido.CashApplied, (await QueryAsync(tenant, "SELECT coalesce(sum(recebido_dinheiro_centavos), 0)::bigint FROM turnos_caixa WHERE fechado_em IS NULL", r => r.GetInt64(0))).Single());
        }

        // Across the line: no reading the other's payments, no paying its comanda, no renaming its methods.
        foreach (var (intruso, alvo) in new[] { (salaoB.Token, recebidoA), (salaoA.Token, recebidoB) })
        {
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/comandas/{alvo.ComandaId}/pagamentos", intruso));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/comandas/{alvo.ComandaId}/recebimentos", intruso, new { versao = alvo.Versao, linhas = new[] { new { pagamentoId = Guid.CreateVersion7(), metodoId = alvo.Dinheiro, recebidoCentavos = 100, gorjetaCentavos = (long?)null, divisaoId = (Guid?)null, divisaoParte = (int?)null } } }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{alvo.Dinheiro}", intruso, new { nome = "Roubado", ativo = false }));
        }

        Assert.Equal(4L, (await QueryAsync(a, "SELECT count(*) FROM pagamentos", r => r.GetInt64(0))).Single());
        Assert.Equal(4L, (await QueryAsync(b, "SELECT count(*) FROM pagamentos", r => r.GetInt64(0))).Single());
    }

    private sealed record Recebido(Guid ComandaId, int Versao, Guid Dinheiro, long CashApplied);

    /// <summary>One organization as its founder: a new shift, a comanda of R$ 29,70, the Pix renamed and an own method "Cheque" added, then ONE receipt of card (R$ 10,00 and R$ 2,00 tip), Cheque (R$ 5,00) and cash (R$ 20,00).</summary>
    private static async Task<Recebido> RunRecebimentoMistoAsync(HttpClient client, Salao salao)
    {
        async Task<System.Text.Json.JsonElement> CallAsync(HttpMethod metodo, string caminho, object? corpo, HttpStatusCode esperado)
        {
            using var response = await SendAsync(client, metodo, caminho, salao.Token, corpo);
            Assert.Equal(esperado, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        }

        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, "/caixa/turnos", salao.Token, new { fundoCentavos = 1_000 }));
        var metodos = (await CallAsync(HttpMethod.Get, "/pagamentos/metodos", null, HttpStatusCode.OK)).EnumerateArray().ToList();
        Guid Id(string chave) => metodos.Single(m => m.GetProperty("chave").GetString() == chave).GetProperty("id").GetGuid();
        await CallAsync(HttpMethod.Put, $"/pagamentos/metodos/{Id("pix")}", new { nome = "Pix QR", ativo = true }, HttpStatusCode.OK);
        var cheque = (await CallAsync(HttpMethod.Post, "/pagamentos/metodos", new { nome = "Cheque" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", salao.Token, new { identificacao = "Mista", mesaId = (Guid?)null, pessoas = (int?)null });
        Assert.Equal(HttpStatusCode.Created, aberta.StatusCode);
        var comanda = (await aberta.Content.ReadFromJsonAsync<ComandaCriadaDto>())!;
        var rodada = await CallAsync(HttpMethod.Post, $"/comandas/{comanda.Id}/rodadas", new { rodadaId = Guid.CreateVersion7(), versao = comanda.Versao, itens = new[] { new { produtoId = salao.ProdutoId, quantidade = 2, opcaoIds = new[] { salao.OpcaoId } } } }, HttpStatusCode.Created);
        var versao = rodada.GetProperty("comandaVersao").GetInt32();

        // R$ 29,70: card 10,00 (+ 2,00 tip), cheque 5,00, cash 20,00 -> the cash applies 14,70 and gives 5,30 back.
        var corpo = new
        {
            versao,
            linhas = new object[]
            {
                new { pagamentoId = Guid.CreateVersion7(), metodoId = Id("credito"), recebidoCentavos = 1_000, gorjetaCentavos = 200, divisaoId = (Guid?)null, divisaoParte = (int?)null },
                new { pagamentoId = Guid.CreateVersion7(), metodoId = cheque, recebidoCentavos = 500, gorjetaCentavos = (long?)null, divisaoId = (Guid?)null, divisaoParte = (int?)null },
                new { pagamentoId = Guid.CreateVersion7(), metodoId = Id("dinheiro"), recebidoCentavos = 2_000, gorjetaCentavos = (long?)null, divisaoId = (Guid?)null, divisaoParte = (int?)null },
            },
        };
        var recebimento = await CallAsync(HttpMethod.Post, $"/comandas/{comanda.Id}/recebimentos", corpo, HttpStatusCode.Created);
        var linhas = recebimento.GetProperty("pagamentos").EnumerateArray().ToList();
        Assert.Equal(["credito", "personalizado", "dinheiro"], linhas.Select(l => l.GetProperty("metodo").GetString()));
        Assert.Equal(["Crédito", "Cheque", "Dinheiro"], linhas.Select(l => l.GetProperty("metodoNome").GetString()));
        Assert.Equal([1_000L, 500L, 1_470L], linhas.Select(l => l.GetProperty("aplicadoCentavos").GetInt64()));
        Assert.Equal([0L, 0L, 530L], linhas.Select(l => l.GetProperty("trocoCentavos").GetInt64()));
        Assert.Equal((0L, versao + 1), (recebimento.GetProperty("conta").GetProperty("saldoCentavos").GetInt64(), recebimento.GetProperty("conta").GetProperty("versao").GetInt32()));
        await CallAsync(HttpMethod.Post, $"/comandas/{comanda.Id}/recebimentos", corpo, HttpStatusCode.OK);
        await CallAsync(HttpMethod.Post, $"/comandas/{comanda.Id}/fechar", new { versao = versao + 1 }, HttpStatusCode.OK);
        return new Recebido(comanda.Id, versao + 1, Id("dinheiro"), 1_470);
    }

    private sealed record Dividido(Guid ComandaId, int Versao, long Total);

    /// <summary>One organization as its founder: a comanda with two items on two seats, previewed, recorded by seat, read back, and the recorded split repeated by its client id.</summary>
    private static async Task<Dividido> RunDivisaoAsync(HttpClient client, Salao salao)
    {
        async Task<System.Text.Json.JsonElement> CallAsync(HttpMethod metodo, string caminho, object? corpo, HttpStatusCode esperado)
        {
            using var response = await SendAsync(client, metodo, caminho, salao.Token, corpo);
            Assert.Equal(esperado, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        }

        using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", salao.Token, new { identificacao = "Divisao", mesaId = (Guid?)null, pessoas = (int?)null });
        Assert.Equal(HttpStatusCode.Created, aberta.StatusCode);
        var comanda = (await aberta.Content.ReadFromJsonAsync<ComandaCriadaDto>())!;
        var rodada = await CallAsync(HttpMethod.Post, $"/comandas/{comanda.Id}/rodadas", new { rodadaId = Guid.CreateVersion7(), versao = comanda.Versao, itens = new[] { new { produtoId = salao.ProdutoId, quantidade = 1, assento = 1, opcaoIds = new[] { salao.OpcaoId } }, new { produtoId = salao.ProdutoId, quantidade = 1, assento = 2, opcaoIds = new[] { salao.OpcaoId } } } }, HttpStatusCode.Created);
        var versao = rodada.GetProperty("comandaVersao").GetInt32();

        var previa = await CallAsync(HttpMethod.Post, $"/comandas/{comanda.Id}/divisao/previa", new { modo = "assento", gorjetaCentavos = 100 }, HttpStatusCode.OK);
        var total = previa.GetProperty("totalCentavos").GetInt64();
        Assert.Equal(total, previa.GetProperty("partes").EnumerateArray().Sum(p => p.GetProperty("totalCentavos").GetInt64()));
        Assert.Equal(100L, previa.GetProperty("partes").EnumerateArray().Sum(p => p.GetProperty("gorjetaCentavos").GetInt64()));
        Assert.Equal(versao, previa.GetProperty("comandaVersao").GetInt32());

        var id = Guid.CreateVersion7();
        var pedido = new { modo = "assento", gorjetaCentavos = 100 };
        var gravada = await CallAsync(HttpMethod.Post, $"/comandas/{comanda.Id}/divisao", new { divisaoId = id, versao, pedido }, HttpStatusCode.Created);
        Assert.Equal((id, true, versao + 1), (gravada.GetProperty("id").GetGuid(), gravada.GetProperty("vigente").GetBoolean(), gravada.GetProperty("comandaVersao").GetInt32()));
        var repetida = await CallAsync(HttpMethod.Post, $"/comandas/{comanda.Id}/divisao", new { divisaoId = id, versao, pedido }, HttpStatusCode.OK);
        Assert.Equal(gravada.GetProperty("criadoEm").GetString(), repetida.GetProperty("criadoEm").GetString());
        var lida = await CallAsync(HttpMethod.Get, $"/comandas/{comanda.Id}/divisao", null, HttpStatusCode.OK);
        Assert.Equal(id, lida.GetProperty("id").GetGuid());
        return new Dividido(comanda.Id, versao + 1, total);
    }

    private sealed record Descontado(Guid ComandaId, Guid ItemId, Guid DescontoId, int Versao);

    /// <summary>One organization as its founder: a comanda with 2 chopps and 1 more of another launch, 10 percent off the comanda, then the first line as a courtesy.</summary>
    private static async Task<Descontado> RunDescontoAsync(HttpClient client, Salao salao)
    {
        using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", salao.Token, new { identificacao = "Desconto", mesaId = (Guid?)null, pessoas = (int?)null });
        Assert.Equal(HttpStatusCode.Created, aberta.StatusCode);
        var comanda = (await aberta.Content.ReadFromJsonAsync<ComandaCriadaDto>())!;
        using var rodada = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/rodadas", salao.Token, new { rodadaId = Guid.CreateVersion7(), versao = comanda.Versao, itens = new[] { new { produtoId = salao.ProdutoId, quantidade = 2, opcaoIds = new[] { salao.OpcaoId } } } });
        Assert.Equal(HttpStatusCode.Created, rodada.StatusCode);
        var rodadaJson = await rodada.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var versao = rodadaJson.GetProperty("comandaVersao").GetInt32();
        var itemId = rodadaJson.GetProperty("itens")[0].GetProperty("id").GetGuid();

        // 2 x (12,50 + 1,00) = 27,00; 10% off = 2,70 -> 24,30; fee 10% = 2,43 -> total 26,73.
        using var desconto = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/descontos", salao.Token, new { versao, percentual = 10m, motivo = "cliente antigo" });
        Assert.Equal(HttpStatusCode.Created, desconto.StatusCode);
        var descontoJson = await desconto.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal((270, 2_673), (descontoJson.GetProperty("desconto").GetProperty("aplicadoCentavos").GetInt32(), descontoJson.GetProperty("conta").GetProperty("totalCentavos").GetInt32()));
        versao = descontoJson.GetProperty("conta").GetProperty("versao").GetInt32();

        using var cortesia = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/itens/{itemId}/cortesia", salao.Token, new { versao, motivo = "aniversario" });
        Assert.Equal(HttpStatusCode.OK, cortesia.StatusCode);
        var cortesiaJson = await cortesia.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(("cortesia", 0, 0), (cortesiaJson.GetProperty("item").GetProperty("tipo").GetString(), cortesiaJson.GetProperty("item").GetProperty("totalCentavos").GetInt32(), cortesiaJson.GetProperty("conta").GetProperty("totalCentavos").GetInt32()));
        return new Descontado(comanda.Id, itemId, descontoJson.GetProperty("desconto").GetProperty("id").GetGuid(), cortesiaJson.GetProperty("conta").GetProperty("versao").GetInt32());
    }

    private sealed record Editado(Guid ComandaId, Guid ItemId, int Versao);

    /// <summary>One organization as its founder: open a comanda, launch 3 chopps, rename it, lower the item to 2, set people and note, then cancel it (no payment). Returns the version the comanda ended with.</summary>
    private static async Task<Editado> RunEdicaoAsync(HttpClient client, Salao salao)
    {
        using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", salao.Token, new { identificacao = "Ana e Beto", mesaId = (Guid?)null, pessoas = (int?)null });
        Assert.Equal(HttpStatusCode.Created, aberta.StatusCode);
        var comanda = (await aberta.Content.ReadFromJsonAsync<ComandaCriadaDto>())!;
        using var rodada = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/rodadas", salao.Token, new { rodadaId = Guid.CreateVersion7(), versao = comanda.Versao, itens = new[] { new { produtoId = salao.ProdutoId, quantidade = 3, opcaoIds = new[] { salao.OpcaoId } } } });
        Assert.Equal(HttpStatusCode.Created, rodada.StatusCode);
        var rodadaJson = await rodada.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var versao = rodadaJson.GetProperty("comandaVersao").GetInt32();
        var itemId = rodadaJson.GetProperty("itens")[0].GetProperty("id").GetGuid();

        using var renomeada = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/renomear", salao.Token, new { versao, identificacao = "Ana e Beto 2" });
        Assert.Equal(HttpStatusCode.OK, renomeada.StatusCode);
        versao = (await renomeada.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("versao").GetInt32();
        using var alterado = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/itens/{itemId}/alterar", salao.Token, new { versao, quantidade = 2, observacao = "sem espuma" });
        Assert.Equal(HttpStatusCode.OK, alterado.StatusCode);
        versao = (await alterado.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("comandaVersao").GetInt32();
        using var dados = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/dados", salao.Token, new { versao, pessoas = 4, observacao = "aniversario" });
        Assert.Equal(HttpStatusCode.OK, dados.StatusCode);
        var dadosJson = await dados.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(("aniversario", 4), (dadosJson.GetProperty("observacao").GetString(), dadosJson.GetProperty("pessoas").GetInt32()));
        versao = dadosJson.GetProperty("versao").GetInt32();

        using var cancelada = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/cancelar", salao.Token, new { versao, motivo = "erro-de-lancamento" });
        Assert.Equal(HttpStatusCode.OK, cancelada.StatusCode);
        var canceladaJson = await cancelada.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(("cancelada", 1), (canceladaJson.GetProperty("comanda").GetProperty("estado").GetString(), canceladaJson.GetProperty("itensCancelados").GetInt32()));
        return new Editado(comanda.Id, itemId, versao + 1);
    }

    private sealed record Mapeado(Guid AreaId, Guid MesaOrigem, Guid MesaDestino, Guid ComandaId);

    /// <summary>One organization as its founder: an area "Varanda", a loose table and one in the area, a comanda opened on the loose one and moved into the area.</summary>
    private static async Task<Mapeado> RunMapaAsync(HttpClient client, Salao salao)
    {
        using var area = await SendAsync(client, HttpMethod.Post, "/mesas/areas", salao.Token, new { nome = "Varanda" });
        Assert.Equal(HttpStatusCode.Created, area.StatusCode);
        var areaId = (await area.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        using var origem = await SendAsync(client, HttpMethod.Post, "/mesas", salao.Token, new { identificacao = "Balcao" });
        using var destino = await SendAsync(client, HttpMethod.Post, "/mesas", salao.Token, new { identificacao = "V1", areaId });
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (origem.StatusCode, destino.StatusCode));
        var origemId = (await origem.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        var destinoId = (await destino.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", salao.Token, new { mesaId = origemId });
        Assert.Equal(HttpStatusCode.Created, aberta.StatusCode);
        var comanda = (await aberta.Content.ReadFromJsonAsync<ComandaCriadaDto>())!;
        using var movida = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/trocar-mesa", salao.Token, new { mesaId = destinoId, versao = comanda.Versao });
        Assert.Equal(HttpStatusCode.OK, movida.StatusCode);
        Assert.Equal(comanda.Versao + 1, (await movida.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("versao").GetInt32());
        return new Mapeado(areaId, origemId, destinoId, comanda.Id);
    }

    private sealed record Rapido(Guid MesaId, Guid ComandaId);

    /// <summary>Quick launch in one organization as its founder: table, comanda on it, one round, repeat the last round (two chopps with the add-on, R$ 29,70 with the fee), pay in cash, close.</summary>
    private static async Task<Rapido> RunRapidoAsync(HttpClient client, Salao salao)
    {
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, "/caixa/turnos", salao.Token, new { fundoCentavos = 0 }));
        using var mesa = await SendAsync(client, HttpMethod.Post, "/mesas", salao.Token, new { identificacao = "M1" });
        Assert.Equal(HttpStatusCode.Created, mesa.StatusCode);
        var mesaId = (await mesa.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", salao.Token, new { mesaId });
        Assert.Equal(HttpStatusCode.Created, aberta.StatusCode);
        var comanda = (await aberta.Content.ReadFromJsonAsync<ComandaCriadaDto>())!;
        // The product from RunSalaoAsync has a required add-on group; the option is the one it created.
        using var primeira = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/rodadas", salao.Token, new { rodadaId = Guid.CreateVersion7(), versao = comanda.Versao, itens = new[] { new { produtoId = salao.ProdutoId, quantidade = 1, opcaoIds = new[] { salao.OpcaoId } } } });
        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        var versao = (await primeira.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("comandaVersao").GetInt32();

        var rodadaId = Guid.CreateVersion7();
        using var repetida = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/rodadas/repetir", salao.Token, new { rodadaId, versao });
        Assert.Equal(HttpStatusCode.Created, repetida.StatusCode);
        var corpo = await repetida.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, corpo.GetProperty("ignorados").GetArrayLength());
        versao = corpo.GetProperty("rodada").GetProperty("comandaVersao").GetInt32();
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/rodadas/repetir", salao.Token, new { rodadaId, versao = 1 }));

        // Two chopps with the add-on: 2 x (12,50 + 1,00) = 27,00 plus 10% = 29,70.
        using var pago = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/pagamentos", salao.Token, new { pagamentoId = Guid.CreateVersion7(), recebidoCentavos = 2_970, versao });
        Assert.Equal(HttpStatusCode.Created, pago.StatusCode);
        var fechada = (await pago.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("comandaVersao").GetInt32();
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/fechar", salao.Token, new { versao = fechada }));
        return new Rapido(mesaId, comanda.Id);
    }

    [Fact]
    public async Task A_unit_price_of_one_organization_is_not_seen_charged_or_changed_by_the_other()
    {
        await using var api = Api();
        using var client = api.CreateClient();
        var a = await NewTenantAsync(api);
        var b = await NewTenantAsync(api);
        var tokenA = (await LoginAsync(client, a.OwnerEmail, DonoSenha)).AccessToken;
        var tokenB = (await LoginAsync(client, b.OwnerEmail, DonoSenha)).AccessToken;
        var produtoA = await NewProductAsync(client, tokenA, "CHOPP", 1250);
        var produtoB = await NewProductAsync(client, tokenB, "CHOPP", 1250);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produtoA}/preco-unidade", tokenA, new { precoCentavos = 2000 }));

        // B still pays the base price, cannot read A's product, and its attempts to write or remove a unit price for it find nothing.
        Assert.Equal([(produtoB, 1250L, 1250L)], await EffectivePricesAsync(client, tokenB));
        Assert.Equal([(produtoA, 1250L, 2000L)], await EffectivePricesAsync(client, tokenA));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produtoA}/preco-unidade", tokenB, new { precoCentavos = 1 }));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Delete, $"/cardapio/produtos/{produtoA}/preco-unidade", tokenB));
        Assert.Equal(0L, (await QueryAsync(b, "SELECT count(*) FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());
        Assert.Equal(1L, (await QueryAsync(a, "SELECT count(*) FROM produtos_preco_unidade", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, (await QueryAsync(b, "SELECT count(*) FROM produtos_preco_unidade WHERE organizacao_id <> @o", r => r.GetInt64(0), ("o", b.Id))).Single());

        // Each organization writes its own price for the same short code; neither changes the other.
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produtoB}/preco-unidade", tokenB, new { precoCentavos = 900 }));
        Assert.Equal([(produtoA, 1250L, 2000L)], await EffectivePricesAsync(client, tokenA));
        Assert.Equal([(produtoB, 1250L, 900L)], await EffectivePricesAsync(client, tokenB));
    }

    private static async Task<Guid> NewProductAsync(HttpClient client, string token, string codigo, long preco)
    {
        using var categoria = await SendAsync(client, HttpMethod.Post, "/cardapio/categorias", token, new { nome = "Bebidas" });
        var categoriaId = (await categoria.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        using var produto = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", token, new { codigo, nome = "Chopp", precoCentavos = preco, categoriaId });
        Assert.Equal(HttpStatusCode.Created, produto.StatusCode);
        return (await produto.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<List<(Guid Id, long Base, long Efetivo)>> EffectivePricesAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/cardapio/produtos", token);
        var lista = await response.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        return [.. lista!.Select(p => (p.GetProperty("id").GetGuid(), p.GetProperty("precoCentavos").GetInt64(), p.GetProperty("precoEfetivoCentavos").GetInt64()))];
    }

    // ---- fixtures ---------------------------------------------------------------------------------------------------------------------------------

    internal sealed record Tenant(Guid Id, Guid Unit, Guid Owner, string OwnerEmail, Conta Operational)
    {
        public FakeRequest RequestOfOwner() => new FakeRequest(Owner, Guid.NewGuid(), new OrganizationContext(Id, Unit));
    }

    internal sealed record Conta(Guid Id, string Usuario);

    private sealed record SessaoDto(string AccessToken, Guid SessaoId, Guid? UnidadeAtivaId);

    private sealed record UnidadeDto(Guid Id, string Nome);

    private sealed record SessaoItemDto(Guid Id, Guid ContaId);

    private sealed record ConviteDto(Guid Id);

    private sealed record ConfiguracaoDto(string Chave, string Valor, string Origem, string? ValorDaOrganizacao);

    private sealed record VersaoDto(int Versao);

    internal sealed class FakeRequest(Guid user, Guid session, OrganizationContext organization) : IRequestContext
    {
        public Guid UserId => user;

        public Guid SessionId => session;

        public OrganizationContext Organization => organization;
    }

    private sealed record ProdutoDto(Guid Id, string Nome, long PrecoCentavos);

    private sealed record ComandaCriadaDto(Guid Id, int Versao);

    private sealed record Salao(string Token, Guid ComandaId, Guid ProdutoId, long EsperadoCentavos, Guid GrupoId, Guid OpcaoId);

    /// <summary>The salão flow of one organization through real HTTP as its founder: R$ 100,00 float, 2 x R$ 12,50 (R$ 27,50 with the 10% fee), R$ 30,00 in cash (R$ 2,50 of change), comanda closed, shift counted exactly.</summary>
    private static async Task<Salao> RunSalaoAsync(HttpClient client, Tenant tenant)
    {
        var token = (await LoginAsync(client, tenant.OwnerEmail, DonoSenha)).AccessToken;
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, "/caixa/turnos", token, new { fundoCentavos = 10_000 }));
        using var categoria = await SendAsync(client, HttpMethod.Post, "/cardapio/categorias", token, new { nome = "Bebidas" });
        Assert.Equal(HttpStatusCode.Created, categoria.StatusCode);
        var categoriaId = (await categoria.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        using var produto = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", token, new { codigo = "CHOPP", nome = "Chopp 300ml", precoCentavos = 1250, categoriaId });
        Assert.Equal(HttpStatusCode.Created, produto.StatusCode);
        var produtoId = (await produto.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        // An add-on group with one priced option (R$ 1,00 per unit), linked to the product: the round below launches it.
        using var grupo = await SendAsync(client, HttpMethod.Post, "/cardapio/grupos-opcao", token, new { nome = "Adicionais", minimo = 1, maximo = 1, opcoes = new[] { new { nome = "Gelo extra", precoCentavos = 100 } } });
        Assert.Equal(HttpStatusCode.Created, grupo.StatusCode);
        var grupoJson = await grupo.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var grupoId = grupoJson.GetProperty("id").GetGuid();
        var opcaoId = grupoJson.GetProperty("opcoes")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produtoId}/grupos-opcao", token, new { grupoIds = new[] { grupoId } }));

        using var aberta = await SendAsync(client, HttpMethod.Post, "/comandas", token, new { identificacao = "Ana e Beto", mesaId = (Guid?)null, pessoas = (int?)null });
        Assert.Equal(HttpStatusCode.Created, aberta.StatusCode);
        var comanda = (await aberta.Content.ReadFromJsonAsync<ComandaCriadaDto>())!;
        using var rodada = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/rodadas", token, new { rodadaId = Guid.CreateVersion7(), versao = comanda.Versao, itens = new[] { new { produtoId, quantidade = 2, opcaoIds = new[] { opcaoId } } } });
        Assert.Equal(HttpStatusCode.Created, rodada.StatusCode);
        var versao = (await rodada.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("comandaVersao").GetInt32();

        using var conta = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda.Id}/conta", token);
        var contaJson = await conta.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2_970, contaJson.GetProperty("totalCentavos").GetInt64());

        var pagamentoId = Guid.CreateVersion7();
        using var pago = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/pagamentos", token, new { pagamentoId, recebidoCentavos = 3_000, versao });
        Assert.Equal(HttpStatusCode.Created, pago.StatusCode);
        var pagoJson = await pago.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(30, pagoJson.GetProperty("trocoCentavos").GetInt64());
        Assert.Equal(0, pagoJson.GetProperty("saldoRestanteCentavos").GetInt64());

        // The same payment id again is the same payment (200, no second charge).
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/pagamentos", token, new { pagamentoId, recebidoCentavos = 3_000, versao }));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id}/fechar", token, new { versao = pagoJson.GetProperty("comandaVersao").GetInt32() }));

        using var turno = await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual", token);
        var esperado = (await turno.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("recebidoDinheiroCentavos").GetInt64() + 10_000;
        using var fechado = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", token, new { contadoCentavos = esperado });
        Assert.Equal(HttpStatusCode.OK, fechado.StatusCode);
        Assert.Equal(0, (await fechado.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("diferencaCentavos").GetInt64());
        return new Salao(token, comanda.Id, produtoId, esperado, grupoId, opcaoId);
    }

    /// <summary>
    /// The complete cash register of one organization as its founder: a new shift of R$ 50,00, a supply of R$ 20,00, a withdrawal of R$ 15,00, closed counting R$ 65,00 against the R$ 55,00 expected
    /// (R$ 10,00 over the tolerance, so it is pending) with a zero Pix count, then the difference approved (self-approval switched on for the organization). Returns the shift id.
    /// </summary>
    private static async Task<Guid> RunCaixaCompletoAsync(HttpClient client, Salao salao)
    {
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(client, HttpMethod.Put, "/configuracoes/caixa.permitir-autoaprovacao", salao.Token, new { camada = "organizacao", valor = "true" }));
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, "/caixa/turnos", salao.Token, new { fundoCentavos = 5_000 }));
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, "/caixa/turnos/atual/suprimentos", salao.Token, new { valorCentavos = 2_000, motivo = "Troco do banco" }));
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, "/caixa/turnos/atual/sangrias", salao.Token, new { valorCentavos = 1_500, motivo = "Deposito", destino = "Cofre" }));
        using var metodos = await SendAsync(client, HttpMethod.Get, "/pagamentos/metodos", salao.Token);
        var pix = (await metodos.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>())!.Single(m => m.GetProperty("chave").GetString() == "pix").GetProperty("id").GetGuid();

        using var fechado = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", salao.Token, new { contadoCentavos = 6_500, contagens = new[] { new { metodoId = pix, contadoCentavos = 0 } } });
        Assert.Equal(HttpStatusCode.OK, fechado.StatusCode);
        var json = await fechado.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal((5_500L, 1_000L, "pendente"), (json.GetProperty("esperadoCentavos").GetInt64(), json.GetProperty("diferencaCentavos").GetInt64(), json.GetProperty("situacao").GetString()));
        var turno = json.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Post, $"/caixa/turnos/{turno}/diferenca/aprovar", salao.Token, new { motivo = "Gorjeta no caixa" }));
        return turno;
    }

    private ApiFactory Api() => new(_db.AppConnection);

    /// <summary>A new organization with one unit, its founder (e-mail account, own password) and one operational account (temporary credential already replaced), both linked to the unit.</summary>
    private async Task<Tenant> NewTenantAsync(ApiFactory api)
    {
        var org = Guid.CreateVersion7();
        var unit = Guid.CreateVersion7();
        await using (var ownerSource = NpgsqlDataSource.Create(_db.OwnerConnection))
        {
            await using var tx = await new TenantDatabase(ownerSource).BeginAsync(new OrganizationContext(org, unit));
            await ExecAsync(tx, "INSERT INTO organizacoes (id, nome) VALUES (@o, @n)", ("o", org), ("n", "Bar " + org.ToString("N")[..8]));
            await ExecAsync(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Centro')", ("u", unit), ("o", org));
            await tx.CommitAsync();
        }

        var email = $"dono-{org:N}@exemplo.test";
        var dono = await api.Services.GetRequiredService<GestaoDeContas>().CriarDonoAsync(org, email, "Dono", DonoSenha);
        Assert.Equal(StatusCriacao.Criada, dono.Status);
        var owner = dono.ContaId!.Value;
        var request = new FakeRequest(owner, Guid.NewGuid(), new OrganizationContext(org, unit));
        var usuario = "u" + org.ToString("N")[..10];
        var criada = await api.Services.GetRequiredService<GestaoDeContas>().CriarOperacionalAsync(request, usuario, "Pessoa " + usuario);
        Assert.Equal(StatusCriacao.Criada, criada.Status);
        var troca = await api.Services.GetRequiredService<TrocaDeSenha>().TrocarAsync(org, criada.ContaId!.Value, criada.Credencial!.Valor, NovaSenha);
        Assert.Equal(StatusTrocaDeSenha.Trocada, troca.Status);

        await using var source = NpgsqlDataSource.Create(_db.AppConnection);
        await using var link = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
        foreach (var id in new[] { owner, criada.ContaId.Value })
        {
            await ExecAsync(link, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @c, @u)", ("o", org), ("c", id), ("u", unit));
        }

        await link.CommitAsync();
        return new Tenant(org, unit, owner, email, new Conta(criada.ContaId.Value, usuario));
    }

    private static async Task<int> ExecAsync(TenantTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads as the application credential inside the tenant's own context (what the app does), never as the owner.</summary>
    private async Task<List<T>> QueryAsync<T>(Tenant tenant, string sql, Func<NpgsqlDataReader, T> map, params (string Name, object Value)[] args)
    {
        await using var source = NpgsqlDataSource.Create(_db.AppConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(tenant.Id, tenant.Unit));
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private async Task<List<string>> ListTablesAsync()
    {
        await using var source = NpgsqlDataSource.Create(_db.AppConnection);
        await using var command = source.CreateCommand(
            "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'organizacao_id' AND table_name <> 'schema_migrations' ORDER BY table_name");
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // ---- HTTP helpers -----------------------------------------------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token, object? body = null, bool csrf = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (csrf)
        {
            request.Headers.Add(Csrf, "1");
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string path, string? token, object? body = null, bool csrf = false)
    {
        using var response = await SendAsync(client, method, path, token, body, csrf);
        return response.StatusCode;
    }

    private static async Task<SessaoDto> LoginAsync(HttpClient client, string email, string senha)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/login", null, new { email, senha }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessaoDto>())!;
    }

    private static async Task<SessaoDto> LoginOperationalAsync(HttpClient client, Tenant tenant, Conta conta)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/login", null, new { organizacaoDoLogin = tenant.Id, usuario = conta.Usuario, senha = NovaSenha }, csrf: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessaoDto>())!;
    }

    private static async Task<List<Guid>> UnitsAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/unidades", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<List<UnidadeDto>>())!.Select(u => u.Id)];
    }

    private static async Task<List<SessaoItemDto>> SessionsAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, HttpMethod.Get, path, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<SessaoItemDto>>())!;
    }

    private static async Task<ConfiguracaoDto> SettingAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/configuracoes", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ConfiguracaoDto>>())!.Single(c => c.Chave == Inatividade);
    }

    private static async Task<List<VersaoDto>> HistoryAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/configuracoes/{Inatividade}/historico?camada=organizacao", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<VersaoDto>>())!;
    }

    private sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Flow4Tech:Tokens:AppKey"] = Key,
                ["Flow4Tech:Identidade:TempoMinimoDeLoginMs"] = "0",
            }));
        }
    }
}

/// <summary>One migrated PostgreSQL for the class; every test creates its own two organizations, so none depends on another.</summary>
public sealed class DoisTenantsDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string AppConnection { get; private set; } = "";

    public string OwnerConnection { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var owner = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        OwnerConnection = owner.ConnectionString;
        var app = new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Username = "flow4tech_app", Password = "app-secret-for-test" };
        AppConnection = app.ConnectionString;
        using var output = new StringWriter();
        var env = new Dictionary<string, string?>
        {
            ["MIGRATION_DATABASE_URL"] = $"postgres://{owner.Username}:{Uri.EscapeDataString(owner.Password!)}@{owner.Host}:{owner.Port}/{owner.Database}",
            ["APP_DB_PASSWORD"] = app.Password,
            ["ConnectionStrings__Default"] = app.ConnectionString,
            ["MIGRATIONS_DIR"] = SessoesEndpointsTests.MigrationsDirectory(),
        };
        Assert.True(await MigratorHost.RunAsync(name => env.GetValueOrDefault(name), output, CancellationToken.None) == 0, output.ToString());
    }

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();
}
