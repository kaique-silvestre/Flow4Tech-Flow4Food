using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0056: everything changed on a comanda without touching money (item quantity and note, name, waiter, head count and note, cancelling it) and its timeline, through the real authenticated routes.
/// Same fixture as the other comanda tests: real PostgreSQL, application credential (RLS on), real sessions. The owner holds every permission; "Garcom" holds none of the new ones (the denied case);
/// "Mudo" is given an editor profile WITHOUT <c>comanda:ver-comandas-de-outros</c> (the visibility case).
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private static readonly string[] PermissoesDeEdicao =
    [
        "comanda:ver", "comanda:abrir", "comanda:lancar-item", "comanda:alterar-item", "comanda:renomear", "comanda:alterar-dados", "comanda:trocar-garcom", "comanda:cancelar", "comanda:ver-historico",
    ];

    private sealed record ComandaEditada(Guid Id, int Numero, string Identificacao, Guid GarcomId, int? Pessoas, string Estado, int Versao, string? Observacao);

    private sealed record ItemEditado(Guid Id, int Quantidade, string? Observacao, long PrecoCentavos, long TotalCentavos, string Estado, string? MotivoCancelamento);

    private sealed record ItemAlteradoBody(ItemEditado Item, int ComandaVersao);

    private sealed record ComandaCanceladaBody(ComandaEditada Comanda, int ItensCancelados);

    private sealed record EventoDoHistoricoBody(Guid Id, string Acao, DateTimeOffset Quando, Guid? UsuarioId, string? UsuarioNome, Dictionary<string, string> Antes, Dictionary<string, string> Depois);

    private sealed record HistoricoBody(List<EventoDoHistoricoBody> Eventos);

    private static Task<HttpResponseMessage> AlterItemAsync(HttpClient client, string? token, Guid comanda, Guid item, int versao, int quantidade, string? observacao = null) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/itens/{item:D}/alterar", token, new { versao, quantidade, observacao });

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, string? token, Guid comanda, int versao, string? identificacao) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/renomear", token, new { versao, identificacao });

    private static Task<HttpResponseMessage> ChangeWaiterAsync(HttpClient client, string? token, Guid comanda, int versao, Guid garcomId) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/trocar-garcom", token, new { versao, garcomId });

    private static Task<HttpResponseMessage> ChangeDataAsync(HttpClient client, string? token, Guid comanda, int versao, int? pessoas, string? observacao) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/dados", token, new { versao, pessoas, observacao });

    private static Task<HttpResponseMessage> CancelComandaAsync(HttpClient client, string? token, Guid comanda, int versao, string motivo = "cliente-desistiu", string? texto = null) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/cancelar", token, new { versao, motivo, texto });

    private static async Task<List<EventoDoHistoricoBody>> HistoryAsync(HttpClient client, string? token, Guid comanda)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/historico", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HistoricoBody>())!.Eventos;
    }

    private static async Task<T> BodyAsync<T>(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Gives <see cref="Mudo"/> a profile with <see cref="PermissoesDeEdicao"/> (no permission to see other waiters' comandas).</summary>
    private static async Task GiveEditorProfileAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfil = await perfis.CriarAsync(request, "Editor", [.. PermissoesDeEdicao.Select(p => new RegraDeAcesso(RuleEffect.Allow, p))]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Mudo, perfil.Id!.Value));
    }

    /// <summary>A fresh session token for the account: changing a profile or deactivating an account ends the sessions it had.</summary>
    private async Task<string> FreshTokenAsync(Api api, Guid user)
    {
        var versao = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{user:D}'", r => r.GetInt32(0))).Single();
        return (await api.Services.GetRequiredService<Sessoes>().IniciarAsync(user, OrgA, versao, false, new ClienteDaSessao("127.0.0.1", "testes"))).AccessToken;
    }

    [Fact]
    public async Task Altering_an_item_changes_quantity_and_note_keeps_the_frozen_price_and_is_audited_with_before_and_after()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1500, 2, "ALT1");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();
        await RunSqlAsync(OrgA, UnitA, "UPDATE produtos SET preco_centavos = 9900");

        var r = await BodyAsync<ItemAlteradoBody>(await AlterItemAsync(client, t.Dono, comanda, item.Id, versao, 5, "  sem gelo  "), HttpStatusCode.OK);

        Assert.Equal((5, "sem gelo", 1500L, 7500L, "lancado", versao + 1), (r.Item.Quantidade, r.Item.Observacao, r.Item.PrecoCentavos, r.Item.TotalCentavos, r.Item.Estado, r.ComandaVersao));
        var salvo = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();
        Assert.Equal((5, "sem gelo", 1500L), (salvo.Quantidade, salvo.Observacao, salvo.PrecoCentavos));
        Assert.Equal(7500L, (await ItemsAsync(client, t.Garcom, comanda)).ConsumoCentavos);
        Assert.Equal(versao + 1, await VersionOfAsync(comanda));
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_tipo, objeto_id, antes->>'quantidade', depois->>'quantidade', depois->>'observacao' FROM auditoria_registros WHERE acao = 'comanda:alterar-item'", x => (x.GetGuid(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4), x.GetString(5))));
        Assert.Equal((DonoA, "item-comanda", item.Id.ToString("D"), "2", "5", "sem gelo"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'comanda.item-alterado' AND payload->>'ItemId' = '{item.Id:D}'", x => x.GetInt32(0)));

        // A blank note clears it.
        var limpo = await BodyAsync<ItemAlteradoBody>(await AlterItemAsync(client, t.Dono, comanda, item.Id, r.ComandaVersao, 5, "   "), HttpStatusCode.OK);
        Assert.Null(limpo.Item.Observacao);
    }

    [Fact]
    public async Task An_item_edit_is_refused_for_bad_input_a_cancelled_item_a_foreign_item_a_closed_comanda_and_a_stale_version()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, produto) = await ComandaWithConsumoAsync(client, t, 1000, 1, "ALT2");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();
        var outra = await OpenAsync(client, t.Garcom);
        var outraRodada = await LaunchOkAsync(client, t.Garcom, outra.Id, outra.Versao, produto.Id);
        var itemDeOutra = outraRodada.Itens.Single();

        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, item.Id, versao, 0), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, item.Id, versao, 1000), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, item.Id, versao, 2, new string('x', 201)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, item.Id, 0, 2), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, itemDeOutra.Id, versao, 2), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, Guid.NewGuid(), versao, 2), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, item.Id, versao - 1, 2), HttpStatusCode.Conflict, "versao-desatualizada", versao);
        Assert.Equal(versao, await VersionOfAsync(comanda));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'comanda:alterar-item'"));

        using var cancelamento = await CancelAsync(client, t.Dono, comanda, item.Id, versao, "erro-de-lancamento");
        Assert.Equal(HttpStatusCode.OK, cancelamento.StatusCode);
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, item.Id, versao + 1, 2), HttpStatusCode.Conflict, "item-ja-cancelado");
    }

    [Fact]
    public async Task Lowering_an_item_below_what_was_received_is_refused_like_cancelling_but_raising_it_is_allowed()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 4, "ALT3");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();
        var pago = await PayOkAsync(client, t.Dono, comanda, 2200, versao);

        // Bill: 4 x 10,00 + 10% = 44,00; paid 22,00. Down to 2 units: total 22,00, balance 0 (allowed). Down to 1: total 11,00 < 22,00 (refused).
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, item.Id, pago.ComandaVersao, 1), HttpStatusCode.Conflict, "valor-ja-recebido");
        Assert.Equal(pago.ComandaVersao, await VersionOfAsync(comanda));
        var baixou = await BodyAsync<ItemAlteradoBody>(await AlterItemAsync(client, t.Dono, comanda, item.Id, pago.ComandaVersao, 2), HttpStatusCode.OK);
        var subiu = await BodyAsync<ItemAlteradoBody>(await AlterItemAsync(client, t.Dono, comanda, item.Id, baixou.ComandaVersao, 6), HttpStatusCode.OK);
        Assert.Equal(6, subiu.Item.Quantidade);
        Assert.Equal(6600L, (await BillAsync(client, t.Dono, comanda)).TotalCentavos);

        // Paid in full and closed: the item can no longer be edited.
        var resto = await PayOkAsync(client, t.Dono, comanda, 4400, subiu.ComandaVersao);
        using var fechar = await CloseAsync(client, t.Dono, comanda, resto.ComandaVersao);
        Assert.Equal(HttpStatusCode.OK, fechar.StatusCode);
        await AssertErrorAsync(await AlterItemAsync(client, t.Dono, comanda, item.Id, resto.ComandaVersao + 1, 3), HttpStatusCode.Conflict, "comanda-nao-aberta");
    }

    [Fact]
    public async Task Renaming_changing_the_head_count_and_the_note_are_versioned_audited_and_validated()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var comanda = await OpenAsync(client, t.Garcom, new { identificacao = "Mesa da janela", pessoas = 2 });

        var renomeada = await BodyAsync<ComandaEditada>(await RenameAsync(client, t.Dono, comanda.Id, comanda.Versao, "  Aniversario   do Joao "), HttpStatusCode.OK);
        var dados = await BodyAsync<ComandaEditada>(await ChangeDataAsync(client, t.Dono, comanda.Id, renomeada.Versao, 8, " vem bolo as 21h "), HttpStatusCode.OK);
        var limpa = await BodyAsync<ComandaEditada>(await ChangeDataAsync(client, t.Dono, comanda.Id, dados.Versao, null, null), HttpStatusCode.OK);

        Assert.Equal(("Aniversario do Joao", comanda.Versao + 1), (renomeada.Identificacao, renomeada.Versao));
        Assert.Equal((8, "vem bolo as 21h", comanda.Versao + 2), (dados.Pessoas, dados.Observacao, dados.Versao));
        Assert.Equal((null, null, comanda.Versao + 3), (limpa.Pessoas, limpa.Observacao, limpa.Versao));
        var linha = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT identificacao, pessoas, observacao, versao FROM comandas WHERE id = '{comanda.Id:D}'", x => (x.GetString(0), x.IsDBNull(1) ? (int?)null : x.GetInt32(1), x.IsDBNull(2) ? null : x.GetString(2), x.GetInt32(3))));
        Assert.Equal(("Aniversario do Joao", (int?)null, (string?)null, comanda.Versao + 3), linha);
        var renomear = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, antes->>'identificacao', depois->>'identificacao', depois->>'comandaNumero' FROM auditoria_registros WHERE acao = 'comanda:renomear'", x => (x.GetGuid(0), x.GetString(1), x.GetString(2), x.GetString(3))));
        Assert.Equal((DonoA, "Mesa da janela", "Aniversario do Joao", "1"), renomear);
        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'comanda:alterar-dados'"));

        await AssertErrorAsync(await RenameAsync(client, t.Dono, comanda.Id, limpa.Versao, "   "), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await RenameAsync(client, t.Dono, comanda.Id, limpa.Versao, new string('x', 41)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ChangeDataAsync(client, t.Dono, comanda.Id, limpa.Versao, 0, null), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ChangeDataAsync(client, t.Dono, comanda.Id, limpa.Versao, 1000, null), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ChangeDataAsync(client, t.Dono, comanda.Id, limpa.Versao, 2, new string('x', 201)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await RenameAsync(client, t.Dono, comanda.Id, comanda.Versao, "Velha"), HttpStatusCode.Conflict, "versao-desatualizada", limpa.Versao);
        await AssertErrorAsync(await ChangeDataAsync(client, t.Dono, comanda.Id, comanda.Versao, 3, null), HttpStatusCode.Conflict, "versao-desatualizada", limpa.Versao);
        Assert.Equal(limpa.Versao, await VersionOfAsync(comanda.Id));
    }

    [Fact]
    public async Task Changing_the_waiter_needs_an_active_account_of_the_unit_and_moves_the_comanda_to_their_list()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var comanda = await OpenAsync(client, t.Garcom);
        Assert.Contains(await ListAsync(client, t.Garcom), c => c.Id == comanda.Id);
        Assert.DoesNotContain(await ListAsync(client, t.Garcom2), c => c.Id == comanda.Id);

        await AssertErrorAsync(await ChangeWaiterAsync(client, t.Dono, comanda.Id, comanda.Versao, Guid.NewGuid()), HttpStatusCode.BadRequest, "garcom-invalido");
        await AssertErrorAsync(await ChangeWaiterAsync(client, t.Dono, comanda.Id, comanda.Versao, DonoB), HttpStatusCode.BadRequest, "garcom-invalido");
        await AssertErrorAsync(await ChangeWaiterAsync(client, t.Dono, comanda.Id, comanda.Versao, Garcom), HttpStatusCode.BadRequest, "garcom-invalido");
        await AssertErrorAsync(await ChangeWaiterAsync(client, t.Dono, comanda.Id, comanda.Versao, Guid.Empty), HttpStatusCode.BadRequest, "invalido");
        await RunSqlAsync(OrgA, UnitA, $"UPDATE contas SET ativa = false, desativada_em = now() WHERE id = '{Garcom2:D}'");
        await AssertErrorAsync(await ChangeWaiterAsync(client, t.Dono, comanda.Id, comanda.Versao, Garcom2), HttpStatusCode.BadRequest, "garcom-invalido");
        await RunSqlAsync(OrgA, UnitA, $"UPDATE contas SET ativa = true, desativada_em = NULL WHERE id = '{Garcom2:D}'");
        await RunSqlAsync(OrgA, UnitA, $"DELETE FROM usuario_unidades WHERE usuario_id = '{Garcom2:D}'");
        await AssertErrorAsync(await ChangeWaiterAsync(client, t.Dono, comanda.Id, comanda.Versao, Garcom2), HttpStatusCode.BadRequest, "garcom-invalido");
        Assert.Equal(comanda.Versao, await VersionOfAsync(comanda.Id));
        await RunSqlAsync(OrgA, UnitA, $"INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES ('{OrgA:D}', '{Garcom2:D}', '{UnitA:D}')");

        var trocada = await BodyAsync<ComandaEditada>(await ChangeWaiterAsync(client, t.Dono, comanda.Id, comanda.Versao, Garcom2), HttpStatusCode.OK);

        Assert.Equal((Garcom2, comanda.Versao + 1), (trocada.GarcomId, trocada.Versao));
        Assert.DoesNotContain(await ListAsync(client, t.Garcom), c => c.Id == comanda.Id);
        Assert.Contains(await ListAsync(client, await FreshTokenAsync(api, Garcom2)), c => c.Id == comanda.Id);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT antes->>'garcomId', depois->>'garcomId' FROM auditoria_registros WHERE acao = 'comanda:trocar-garcom'", x => (x.GetString(0), x.GetString(1))));
        Assert.Equal((Garcom.ToString("D"), Garcom2.ToString("D")), audit);
    }

    [Fact]
    public async Task Every_new_route_answers_401_without_a_session_and_403_without_its_permission_and_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "PERM");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();
        var antes = await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE resultado = 'sucesso'", x => x.GetInt64(0));

        foreach (var token in new string?[] { null, t.Garcom })
        {
            var esperado = token is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            Assert.Equal(esperado, (await AlterItemAsync(client, token, comanda, item.Id, versao, 3)).StatusCode);
            Assert.Equal(esperado, (await RenameAsync(client, token, comanda, versao, "Nova")).StatusCode);
            Assert.Equal(esperado, (await ChangeWaiterAsync(client, token, comanda, versao, Garcom2)).StatusCode);
            Assert.Equal(esperado, (await ChangeDataAsync(client, token, comanda, versao, 4, "x")).StatusCode);
            Assert.Equal(esperado, (await CancelComandaAsync(client, token, comanda, versao)).StatusCode);
            Assert.Equal(esperado, (await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/historico", token)).StatusCode);
        }

        Assert.Equal(versao, await VersionOfAsync(comanda));
        Assert.Equal("aberta", (await QueryAsync(OrgA, UnitA, $"SELECT estado FROM comandas WHERE id = '{comanda:D}'", x => x.GetString(0))).Single());
        Assert.Equal(1L, await CountAsync("itens_comanda WHERE quantidade = 2"));
        // A denial writes no success record: only the cancel denial (sensitive) is audited, as 'negado'.
        var depois = await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros WHERE resultado = 'sucesso'", x => x.GetInt64(0));
        Assert.Equal(antes, depois);
    }

    [Fact]
    public async Task An_editor_without_permission_to_see_other_waiters_comandas_reaches_only_their_own()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await GiveEditorProfileAsync(api);
        var editor = await FreshTokenAsync(api, Mudo);
        var deOutro = await OpenAsync(client, t.Garcom);
        var propria = await OpenAsync(client, editor);

        await AssertErrorAsync(await RenameAsync(client, editor, deOutro.Id, deOutro.Versao, "Roubada"), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await ChangeDataAsync(client, editor, deOutro.Id, deOutro.Versao, 3, null), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await CancelComandaAsync(client, editor, deOutro.Id, deOutro.Versao), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await ChangeWaiterAsync(client, editor, deOutro.Id, deOutro.Versao, Mudo), HttpStatusCode.NotFound, "nao-encontrada");
        using var historicoDeOutro = await SendAsync(client, HttpMethod.Get, $"/comandas/{deOutro.Id:D}/historico", editor);
        Assert.Equal(HttpStatusCode.NotFound, historicoDeOutro.StatusCode);
        Assert.Equal(deOutro.Versao, await VersionOfAsync(deOutro.Id));

        var renomeada = await BodyAsync<ComandaEditada>(await RenameAsync(client, editor, propria.Id, propria.Versao, "Minha"), HttpStatusCode.OK);
        Assert.Equal("Minha", renomeada.Identificacao);
        Assert.NotEmpty(await HistoryAsync(client, editor, propria.Id));
    }

    [Fact]
    public async Task The_history_lists_who_did_what_and_when_in_order_for_the_comanda_its_rounds_items_and_payments_without_secrets()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var produto = await CreateProductAsync(client, t.Dono, "HIST", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom, new { identificacao = "Joao" });
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, 3);
        var item = rodada.Itens.Single();
        var renomeada = await BodyAsync<ComandaEditada>(await RenameAsync(client, t.Dono, comanda.Id, rodada.ComandaVersao, "Joao e Ana"), HttpStatusCode.OK);
        var alterado = await BodyAsync<ItemAlteradoBody>(await AlterItemAsync(client, t.Dono, comanda.Id, item.Id, renomeada.Versao, 2), HttpStatusCode.OK);
        var pago = await PayOkAsync(client, t.Dono, comanda.Id, 1000, alterado.ComandaVersao);
        // Another comanda's records never show up here.
        var outra = await OpenAsync(client, t.Garcom, new { identificacao = "Outra" });
        await RenameAsync(client, t.Dono, outra.Id, outra.Versao, "Outra renomeada");

        var eventos = await HistoryAsync(client, t.Dono, comanda.Id);

        Assert.Equal(["comanda:abrir", "comanda:lancar-rodada", "comanda:renomear", "comanda:alterar-item", "pagamento:registrar"], eventos.Select(e => e.Acao));
        Assert.Equal([Garcom, Garcom, DonoA, DonoA, DonoA], eventos.Select(e => e.UsuarioId!.Value));
        Assert.Equal(["Garcom", "Garcom", "Dono", "Dono", "Dono"], eventos.Select(e => e.UsuarioNome));
        Assert.Equal(eventos.Select(e => e.Quando).Order(), eventos.Select(e => e.Quando));
        Assert.Equal("Joao e Ana", eventos[2].Depois["identificacao"]);
        Assert.Equal("Joao", eventos[2].Antes["identificacao"]);
        Assert.Equal(("3", "2"), (eventos[3].Antes["quantidade"], eventos[3].Depois["quantidade"]));
        Assert.Equal("1000", eventos[4].Depois["recebidoCentavos"]);
        Assert.All(eventos.SelectMany(e => e.Antes.Keys.Concat(e.Depois.Keys)), chave => Assert.NotEqual("versao", chave));
        Assert.DoesNotContain(eventos, e => e.Depois.ContainsKey("senha") || e.Depois.ContainsKey("token"));
        Assert.DoesNotContain(await HistoryAsync(client, t.Dono, outra.Id), e => e.Acao == "pagamento:registrar");
    }

    [Fact]
    public async Task Cancelling_a_comanda_without_payments_cancels_every_open_item_with_the_same_reason_and_publishes_the_event()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var mesa = await CreateTableAsync(client, t.Dono, "7");
        var produto = await CreateProductAsync(client, t.Dono, "CANC", "Porcao", 2500);
        var comanda = await OpenAsync(client, t.Garcom, new { mesaId = mesa.Id });
        var primeira = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, 2);
        var segunda = await LaunchOkAsync(client, t.Garcom, comanda.Id, primeira.ComandaVersao, produto.Id, 1);
        using var jaCancelado = await CancelAsync(client, t.Dono, comanda.Id, segunda.Itens.Single().Id, segunda.ComandaVersao, "item-indisponivel");
        Assert.Equal(HttpStatusCode.OK, jaCancelado.StatusCode);
        var versao = segunda.ComandaVersao + 1;

        var r = await BodyAsync<ComandaCanceladaBody>(await CancelComandaAsync(client, t.Dono, comanda.Id, versao, "cliente-desistiu"), HttpStatusCode.OK);

        Assert.Equal(("cancelada", versao + 1, 1), (r.Comanda.Estado, r.Comanda.Versao, r.ItensCancelados));
        var itens = await QueryAsync(OrgA, UnitA, $"SELECT estado, motivo_cancelamento, cancelado_por FROM itens_comanda WHERE comanda_id = '{comanda.Id:D}' ORDER BY posicao, cancelado_em", x => (x.GetString(0), x.GetString(1), x.GetGuid(2)));
        Assert.Equal(2, itens.Count);
        Assert.Contains(("cancelado", "cliente desistiu", DonoA), itens);
        Assert.Contains(("cancelado", "item indisponível", DonoA), itens);
        var linha = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT estado, motivo_cancelamento, cancelada_por, fechada_em IS NOT NULL, versao FROM comandas WHERE id = '{comanda.Id:D}'", x => (x.GetString(0), x.GetString(1), x.GetGuid(2), x.GetBoolean(3), x.GetInt32(4))));
        Assert.Equal(("cancelada", "cliente desistiu", DonoA, true, versao + 1), linha);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_tipo, objeto_id, depois->>'motivo', depois->>'itensCancelados' FROM auditoria_registros WHERE acao = 'comanda:cancelar'", x => (x.GetGuid(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4))));
        Assert.Equal((DonoA, "comanda", comanda.Id.ToString("D"), "cliente desistiu", "1"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'comanda.cancelada' AND payload->>'ComandaId' = '{comanda.Id:D}'", x => x.GetInt32(0)));
        Assert.DoesNotContain(await ListAsync(client, t.Dono), c => c.Id == comanda.Id);
        using var mesas = await SendAsync(client, HttpMethod.Get, "/mesas", t.Dono);
        Assert.Equal("livre", (await mesas.Content.ReadFromJsonAsync<List<MesaBody>>())!.Single(m => m.Id == mesa.Id).Status);
        Assert.Contains(await HistoryAsync(client, t.Dono, comanda.Id), e => e.Acao == "comanda:cancelar" && e.Depois["motivo"] == "cliente desistiu");

        // A cancelled comanda takes no launch, no edit, no payment and does not close.
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, versao + 1, produto.Id), HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertErrorAsync(await RenameAsync(client, t.Dono, comanda.Id, versao + 1, "Tarde"), HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertErrorAsync(await CancelComandaAsync(client, t.Dono, comanda.Id, versao + 1), HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertErrorAsync(await CloseAsync(client, t.Dono, comanda.Id, versao + 1), HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda.Id, 100, versao + 1), HttpStatusCode.Conflict, "comanda-nao-aberta");
    }

    [Fact]
    public async Task Cancelling_is_refused_with_a_payment_with_a_bad_reason_and_with_a_stale_version_and_the_free_text_is_stored_for_outro()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1, "CPAG");
        var pago = await PayOkAsync(client, t.Dono, comanda, 300, versao);

        await AssertErrorAsync(await CancelComandaAsync(client, t.Dono, comanda, pago.ComandaVersao), HttpStatusCode.Conflict, "com-pagamentos");
        Assert.Equal(("aberta", pago.ComandaVersao), (await QueryAsync(OrgA, UnitA, $"SELECT estado, versao FROM comandas WHERE id = '{comanda:D}'", x => (x.GetString(0), x.GetInt32(1)))).Single());
        Assert.Equal(1L, await CountAsync($"itens_comanda WHERE comanda_id = '{comanda:D}' AND estado = 'lancado'"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'comanda:cancelar'"));

        var vazia = await OpenAsync(client, t.Garcom);
        await AssertErrorAsync(await CancelComandaAsync(client, t.Dono, vazia.Id, vazia.Versao, "nao-existe"), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await CancelComandaAsync(client, t.Dono, vazia.Id, vazia.Versao, "outro"), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await CancelComandaAsync(client, t.Dono, vazia.Id, 0), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await CancelComandaAsync(client, t.Dono, vazia.Id, vazia.Versao + 5), HttpStatusCode.Conflict, "versao-desatualizada", vazia.Versao);
        var livre = await BodyAsync<ComandaCanceladaBody>(await CancelComandaAsync(client, t.Dono, vazia.Id, vazia.Versao, "outro", "  cliente foi embora  "), HttpStatusCode.OK);
        Assert.Equal(0, livre.ItensCancelados);
        Assert.Equal("cliente foi embora", (await QueryAsync(OrgA, UnitA, $"SELECT motivo_cancelamento FROM comandas WHERE id = '{vazia.Id:D}'", x => x.GetString(0))).Single());
    }

    [Fact]
    public async Task A_failure_after_the_audit_and_the_event_rolls_the_cancel_back_completely()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "ROLL");
        await using var falha = new Api(_appConnection, configure: services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IEventosDeComandas));
            services.Remove(real);
            services.AddSingleton<IEventosDeComandas>(p => new EventsThatThrow(ActivatorUtilities.CreateInstance<Flow4Tech.Comandas.Infrastructure.OutboxEventosDeComandas>(p)));
        });
        var edicao = falha.Services.GetRequiredService<GestaoDaEdicaoDaComanda>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => edicao.CancelarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), comanda, versao, "cliente-desistiu", null));

        Assert.Equal(("aberta", versao), (await QueryAsync(OrgA, UnitA, $"SELECT estado, versao FROM comandas WHERE id = '{comanda:D}'", x => (x.GetString(0), x.GetInt32(1)))).Single());
        Assert.Equal(1L, await CountAsync($"itens_comanda WHERE comanda_id = '{comanda:D}' AND estado = 'lancado'"));
        Assert.Equal((0L, 0L), (await CountAsync("auditoria_registros WHERE acao = 'comanda:cancelar'"), await CountAsync("outbox_eventos WHERE tipo = 'comanda.cancelada'")));
    }

    [Fact]
    public async Task A_launch_racing_the_close_of_the_same_comanda_never_lands_in_a_closed_comanda()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "RACE", "Chopp", 1000);

        for (var volta = 0; volta < 8; volta++)
        {
            var comanda = await OpenAsync(client, t.Garcom);
            var gate = new TaskCompletionSource();

            async Task<HttpResponseMessage> FecharAsync()
            {
                await gate.Task;
                return await CloseAsync(client, t.Dono, comanda.Id, comanda.Versao);
            }

            async Task<HttpResponseMessage> LancarAsync()
            {
                await gate.Task;
                return await LaunchAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);
            }

            var fechar = FecharAsync();
            var lancar = LancarAsync();
            gate.SetResult();
            var (fechou, lancou) = (await fechar, await lancar);

            var estado = (await QueryAsync(OrgA, UnitA, $"SELECT estado FROM comandas WHERE id = '{comanda.Id:D}'", x => x.GetString(0))).Single();
            var itens = await CountAsync($"itens_comanda WHERE comanda_id = '{comanda.Id:D}'");
            if (fechou.StatusCode == HttpStatusCode.OK)
            {
                // The close won: the launch was refused whole, nothing is in the closed comanda.
                Assert.Equal(("fechada", 0L, HttpStatusCode.Conflict), (estado, itens, lancou.StatusCode));
            }
            else
            {
                // The launch won: the comanda still has a balance to pay, so it stays open with the item in it.
                Assert.Equal(("aberta", 1L, HttpStatusCode.Created), (estado, itens, lancou.StatusCode));
            }
        }
    }

    [Fact]
    public async Task Another_organization_cannot_edit_cancel_or_read_the_history_of_a_comanda_and_the_new_columns_are_checked_by_the_database()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1, "ISOL");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();

        await AssertErrorAsync(await AlterItemAsync(client, t.DonoB, comanda, item.Id, versao, 3), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await RenameAsync(client, t.DonoB, comanda, versao, "Invadida"), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await ChangeDataAsync(client, t.DonoB, comanda, versao, 5, null), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await ChangeWaiterAsync(client, t.DonoB, comanda, versao, DonoB), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await CancelComandaAsync(client, t.DonoB, comanda, versao), HttpStatusCode.NotFound, "nao-encontrada");
        using var historico = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/historico", t.DonoB);
        Assert.Equal(HttpStatusCode.NotFound, historico.StatusCode);
        Assert.Equal(versao, await VersionOfAsync(comanda));

        // Cancelled means a reason and an author: the database refuses half of it.
        await Assert.ThrowsAnyAsync<Exception>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'cancelada', fechada_em = now() WHERE id = '{comanda:D}'"));
        await Assert.ThrowsAnyAsync<Exception>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET observacao = '' WHERE id = '{comanda:D}'"));
        await Assert.ThrowsAnyAsync<Exception>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET motivo_cancelamento = 'solto', cancelada_por = '{DonoA:D}' WHERE id = '{comanda:D}'"));
    }
}
