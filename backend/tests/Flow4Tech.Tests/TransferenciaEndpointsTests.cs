using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0060: transferring items between comandas, merging and separating, through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions). The owner holds every
/// permission; "Garcom" holds neither <c>comanda:transferir-item</c> nor <c>comanda:juntar</c> (the denied case); "Mudo" holds both but NOT <c>comanda:ver-comandas-de-outros</c> (the visibility case).
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private static readonly string[] PermissoesDeMovimento = ["comanda:ver", "comanda:abrir", "comanda:lancar-item", "comanda:transferir-item", "comanda:juntar"];

    private sealed record MovimentoBody(ComandaEditada Origem, ComandaEditada Destino, int ItensMovidos);

    private sealed record MovimentoErroBody(string Codigo, Guid? ComandaId, int? VersaoAtual);

    private static Task<HttpResponseMessage> TransferAsync(HttpClient client, string? token, Guid origem, int versao, Guid destino, int destinoVersao, params Guid[] itens) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{origem:D}/itens/transferir", token, new { versao, destinoId = destino, destinoVersao, itemIds = itens });

    private static Task<HttpResponseMessage> MergeAsync(HttpClient client, string? token, Guid destino, int versao, Guid origem, int origemVersao) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{destino:D}/juntar", token, new { versao, origemId = origem, origemVersao });

    private static Task<HttpResponseMessage> SeparateAsync(HttpClient client, string? token, Guid origem, int versao, string? identificacao, params Guid[] itens) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{origem:D}/itens/separar", token, new { versao, itemIds = itens, identificacao });

    private static async Task AssertMovimentoErroAsync(HttpResponseMessage response, HttpStatusCode status, string codigo, Guid? comanda = null, int? versaoAtual = null)
    {
        Assert.Equal(status, response.StatusCode);
        var erro = (await response.Content.ReadFromJsonAsync<MovimentoErroBody>())!;
        Assert.Equal((codigo, comanda, versaoAtual), (erro.Codigo, erro.ComandaId, erro.VersaoAtual));
    }

    /// <summary>Gives <see cref="Mudo"/> a profile that moves and merges but sees only its own comandas.</summary>
    private async Task<string> GiveMoverProfileAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfil = await perfis.CriarAsync(request, "Movimenta", [.. PermissoesDeMovimento.Select(p => new RegraDeAcesso(RuleEffect.Allow, p))]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Mudo, perfil.Id!.Value));
        return await FreshTokenAsync(api, Mudo);
    }

    private sealed record Par(Guid X, int VersaoX, Guid Y, int VersaoY, Guid ProdutoId, List<ItemBody> ItensDeX);

    /// <summary>Comanda X of the waiter with two items in two different rounds (R$ 10,00 each) and an empty comanda Y of the same waiter.</summary>
    private static async Task<Par> PairAsync(HttpClient client, Tokens t, string codigo)
    {
        var produto = await CreateProductAsync(client, t.Dono, codigo, "Produto " + codigo, 1000);
        var x = await OpenAsync(client, t.Garcom);
        var r1 = await LaunchOkAsync(client, t.Garcom, x.Id, x.Versao, produto.Id);
        var r2 = await LaunchOkAsync(client, t.Garcom, x.Id, r1.ComandaVersao, produto.Id);
        var y = await OpenAsync(client, t.Garcom);
        return new Par(x.Id, r2.ComandaVersao, y.Id, y.Versao, produto.Id, [.. r1.Itens, .. r2.Itens]);
    }

    [Fact]
    public async Task Transferring_items_moves_them_with_their_price_bumps_both_versions_and_audits_and_publishes_on_both_comandas()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var par = await PairAsync(client, t, "TRF1");
        var (primeiro, segundo) = (par.ItensDeX[0], par.ItensDeX[1]);
        await RunSqlAsync(OrgA, UnitA, "UPDATE produtos SET preco_centavos = 9900");

        var r = await BodyAsync<MovimentoBody>(await TransferAsync(client, t.Dono, par.X, par.VersaoX, par.Y, par.VersaoY, primeiro.Id), HttpStatusCode.OK);

        Assert.Equal((1, par.VersaoX + 1, par.VersaoY + 1, "aberta", "aberta"), (r.ItensMovidos, r.Origem.Versao, r.Destino.Versao, r.Origem.Estado, r.Destino.Estado));
        Assert.Equal((par.VersaoX + 1, par.VersaoY + 1), (await VersionOfAsync(par.X), await VersionOfAsync(par.Y)));
        var emX = await ItemsAsync(client, t.Garcom, par.X);
        var emY = await ItemsAsync(client, t.Garcom, par.Y);
        Assert.Equal([segundo.Id], emX.Itens.Select(i => i.Id));
        Assert.Equal((primeiro.Id, 1000L, "lancado"), (emY.Itens.Single().Id, emY.Itens.Single().PrecoCentavos, emY.Itens.Single().Estado));
        Assert.Equal((1000L, 1000L), (emX.ConsumoCentavos, emY.ConsumoCentavos));

        // The moved item sits in a copy of its round that belongs to the destination, with the original send time and author.
        var rodada = (await QueryAsync(OrgA, UnitA, $"SELECT r.comanda_id, r.comanda_versao, r.enviada_em = o.enviada_em, r.enviada_por = o.enviada_por FROM itens_comanda i JOIN rodadas r ON r.id = i.rodada_id JOIN rodadas o ON o.id = '{primeiro.RodadaId:D}' WHERE i.id = '{primeiro.Id:D}'", x => (x.GetGuid(0), x.GetInt32(1), x.GetBoolean(2), x.GetBoolean(3)))).Single();
        Assert.Equal((par.Y, par.VersaoY + 1, true, true), rodada);

        // One audit record on EACH comanda, same unit of work, and one event naming both.
        var auditoria = await QueryAsync(OrgA, UnitA, "SELECT objeto_id, usuario_id, depois->>'papel', depois->>'itens', depois->>'outraComandaId' FROM auditoria_registros WHERE acao = 'comanda:transferir-item' ORDER BY depois->>'papel'", x => (x.GetString(0), x.GetGuid(1), x.GetString(2), x.GetString(3), x.GetString(4)));
        Assert.Equal([(par.Y.ToString("D"), DonoA, "destino", "1", par.X.ToString("D")), (par.X.ToString("D"), DonoA, "origem", "1", par.Y.ToString("D"))], auditoria);
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'comanda.transferida' AND payload->>'OrigemId' = '{par.X:D}' AND payload->>'DestinoId' = '{par.Y:D}' AND payload->>'DestinoCriada' = 'false'"));
        var historico = await HistoryAsync(client, t.Dono, par.Y);
        Assert.Contains(historico, e => e.Acao == "comanda:transferir-item");

        // Undoing the OLD round can no longer reach the item that left it (the round of X now only has the other item).
        var itemDoOutro = (await ItemsAsync(client, t.Garcom, par.Y)).Itens.Single();
        Assert.Equal(par.Y, (await QueryAsync(OrgA, UnitA, $"SELECT comanda_id FROM itens_comanda WHERE id = '{itemDoOutro.Id:D}'", x => x.GetGuid(0))).Single());
    }

    [Fact]
    public async Task A_transfer_is_refused_for_bad_input_missing_or_not_open_comandas_cancelled_items_and_stale_versions_and_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var par = await PairAsync(client, t, "TRF2");
        var item = par.ItensDeX[0];
        var outra = await OpenAsync(client, t.Garcom);
        var itemDeOutra = (await LaunchOkAsync(client, t.Garcom, outra.Id, outra.Versao, par.ProdutoId)).Itens.Single();

        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, par.VersaoX, par.Y, par.VersaoY), HttpStatusCode.BadRequest, "invalido");
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, par.VersaoX, par.Y, par.VersaoY, item.Id, item.Id), HttpStatusCode.BadRequest, "invalido");
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, par.VersaoX, par.X, par.VersaoX, item.Id), HttpStatusCode.BadRequest, "invalido");
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, 0, par.Y, par.VersaoY, item.Id), HttpStatusCode.BadRequest, "invalido");
        var inexistente = Guid.NewGuid();
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, par.VersaoX, inexistente, 1, item.Id), HttpStatusCode.NotFound, "nao-encontrada", inexistente);
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, par.VersaoX, par.Y, par.VersaoY, itemDeOutra.Id), HttpStatusCode.NotFound, "nao-encontrada");

        // A stale version on either one answers 409 with the one that is stale and its current version, and the other one is NOT bumped.
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, par.VersaoX - 1, par.Y, par.VersaoY, item.Id), HttpStatusCode.Conflict, "versao-desatualizada", par.X, par.VersaoX);
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, par.VersaoX, par.Y, par.VersaoY + 5, item.Id), HttpStatusCode.Conflict, "versao-desatualizada", par.Y, par.VersaoY);
        Assert.Equal((par.VersaoX, par.VersaoY), (await VersionOfAsync(par.X), await VersionOfAsync(par.Y)));
        Assert.Equal(2L, await CountAsync($"itens_comanda WHERE comanda_id = '{par.X:D}'"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'comanda:transferir-item'"));
        Assert.Equal(0L, await CountAsync("outbox_eventos WHERE tipo = 'comanda.transferida'"));

        // A cancelled item and a closed or cancelled destination.
        using var cancelamento = await CancelAsync(client, t.Dono, par.X, item.Id, par.VersaoX, "erro-de-lancamento");
        Assert.Equal(HttpStatusCode.OK, cancelamento.StatusCode);
        var versaoX = par.VersaoX + 1;
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, versaoX, par.Y, par.VersaoY, item.Id), HttpStatusCode.Conflict, "item-ja-cancelado");
        using var cancelada = await CancelComandaAsync(client, t.Dono, par.Y, par.VersaoY);
        Assert.Equal(HttpStatusCode.OK, cancelada.StatusCode);
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, versaoX, par.Y, par.VersaoY + 1, par.ItensDeX[1].Id), HttpStatusCode.Conflict, "comanda-nao-aberta", par.Y);
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.Y, par.VersaoY + 1, par.X, versaoX, par.ItensDeX[1].Id), HttpStatusCode.Conflict, "comanda-nao-aberta", par.Y);
        Assert.Equal(versaoX, await VersionOfAsync(par.X));
    }

    [Fact]
    public async Task A_transfer_is_refused_for_an_item_with_a_discount_and_when_what_was_received_would_exceed_what_is_left_and_otherwise_still_works()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 1000);
        var par = await PairAsync(client, t, "TRF3");
        var (a, b) = (par.ItensDeX[0], par.ItensDeX[1]);

        // Item discount: the discount row is tied to the item and written once, so the item stays.
        var desconto = await BodyAsync<System.Text.Json.JsonElement>(await ValueAsync(client, t.Dono, par.X, par.VersaoX, 100, a.Id), HttpStatusCode.Created);
        var versao = desconto.GetProperty("conta").GetProperty("versao").GetInt32();
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, versao, par.Y, par.VersaoY, a.Id), HttpStatusCode.Conflict, "item-com-desconto");

        // Bill: 2000 - 100 = 1900 + 10% = 2090. Paid 1500; moving b leaves (1000 - 100) + 10% = 990 < 1500.
        var pago = await PayOkAsync(client, t.Dono, par.X, 1500, versao);
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, pago.ComandaVersao, par.Y, par.VersaoY, b.Id), HttpStatusCode.Conflict, "valor-ja-recebido");
        Assert.Equal((pago.ComandaVersao, par.VersaoY), (await VersionOfAsync(par.X), await VersionOfAsync(par.Y)));
        Assert.Equal(2L, await CountAsync($"itens_comanda WHERE comanda_id = '{par.X:D}'"));

        // Moving INTO a comanda that already received money is fine: its bill only grows.
        var lancada = await LaunchOkAsync(client, t.Dono, par.Y, par.VersaoY, par.ProdutoId);
        var ok = await BodyAsync<MovimentoBody>(await TransferAsync(client, t.Dono, par.Y, lancada.ComandaVersao, par.X, pago.ComandaVersao, lancada.Itens.Single().Id), HttpStatusCode.OK);
        Assert.Equal(1, ok.ItensMovidos);
    }

    [Fact]
    public async Task Merging_moves_the_launched_items_closes_the_other_comanda_as_merged_and_refuses_what_cannot_be_merged()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 1000);
        var par = await PairAsync(client, t, "JUN1");
        var da = (await LaunchOkAsync(client, t.Garcom, par.Y, par.VersaoY, par.ProdutoId, 3)).Itens.Single();
        var versaoY = par.VersaoY + 1;
        using var cancel = await CancelAsync(client, t.Dono, par.X, par.ItensDeX[0].Id, par.VersaoX, "erro-de-lancamento");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var versaoX = par.VersaoX + 1;

        // Refusals first: same comanda, stale version of either, a payment on the merged one, a discount on it.
        await AssertMovimentoErroAsync(await MergeAsync(client, t.Dono, par.Y, versaoY, par.Y, versaoY), HttpStatusCode.BadRequest, "invalido");
        await AssertMovimentoErroAsync(await MergeAsync(client, t.Dono, par.Y, versaoY - 1, par.X, versaoX), HttpStatusCode.Conflict, "versao-desatualizada", par.Y, versaoY);
        await AssertMovimentoErroAsync(await MergeAsync(client, t.Dono, par.Y, versaoY, par.X, versaoX - 1), HttpStatusCode.Conflict, "versao-desatualizada", par.X, versaoX);
        Assert.Equal((versaoX, versaoY), (await VersionOfAsync(par.X), await VersionOfAsync(par.Y)));
        var pagou = await PayOkAsync(client, t.Dono, par.X, 100, versaoX);
        await AssertMovimentoErroAsync(await MergeAsync(client, t.Dono, par.Y, versaoY, par.X, pagou.ComandaVersao), HttpStatusCode.Conflict, "valor-ja-recebido");

        var outra = await OpenAsync(client, t.Garcom);
        var itemDescontado = (await LaunchOkAsync(client, t.Garcom, outra.Id, outra.Versao, par.ProdutoId)).Itens.Single();
        var comDesconto = await BodyAsync<System.Text.Json.JsonElement>(await PercentAsync(client, t.Dono, outra.Id, outra.Versao + 1, 10m), HttpStatusCode.Created);
        await AssertMovimentoErroAsync(await MergeAsync(client, t.Dono, par.Y, versaoY, outra.Id, comDesconto.GetProperty("conta").GetProperty("versao").GetInt32()), HttpStatusCode.Conflict, "comanda-com-desconto");
        Assert.Equal(1L, await CountAsync($"itens_comanda WHERE comanda_id = '{outra.Id:D}' AND id = '{itemDescontado.Id:D}'"));

        // A merge that is allowed: a comanda without payment and without discount (the third one, with one live item and one cancelled).
        var terceira = await OpenAsync(client, t.Garcom);
        var vivo = (await LaunchOkAsync(client, t.Garcom, terceira.Id, terceira.Versao, par.ProdutoId, 2)).Itens.Single();
        var cancelado = (await LaunchOkAsync(client, t.Garcom, terceira.Id, terceira.Versao + 1, par.ProdutoId)).Itens.Single();
        using var cancelamento = await CancelAsync(client, t.Dono, terceira.Id, cancelado.Id, terceira.Versao + 2, "erro-de-lancamento");
        Assert.Equal(HttpStatusCode.OK, cancelamento.StatusCode);
        var versaoT = terceira.Versao + 3;

        var r = await BodyAsync<MovimentoBody>(await MergeAsync(client, t.Dono, par.Y, versaoY, terceira.Id, versaoT), HttpStatusCode.OK);

        Assert.Equal((1, "juntada", versaoT + 1, "aberta", versaoY + 1), (r.ItensMovidos, r.Origem.Estado, r.Origem.Versao, r.Destino.Estado, r.Destino.Versao));
        Assert.Equal(("juntada", true, par.Y, DonoA), (await QueryAsync(OrgA, UnitA, $"SELECT estado, fechada_em IS NOT NULL, juntada_na_comanda_id, juntada_por FROM comandas WHERE id = '{terceira.Id:D}'", x => (x.GetString(0), x.GetBoolean(1), x.GetGuid(2), x.GetGuid(3)))).Single());
        var emY = (await ItemsAsync(client, t.Garcom, par.Y)).Itens;
        Assert.Contains(emY, i => i.Id == vivo.Id && i.Quantidade == 2);
        Assert.DoesNotContain(emY, i => i.Id == cancelado.Id);
        var emTerceira = (await ItemsAsync(client, t.Garcom, terceira.Id)).Itens;
        Assert.Equal([cancelado.Id], emTerceira.Select(i => i.Id));
        Assert.Equal(0L, (await ItemsAsync(client, t.Garcom, terceira.Id)).ConsumoCentavos);
        Assert.Equal(da.Quantidade * 1000L + 2000L, (await ItemsAsync(client, t.Garcom, par.Y)).ConsumoCentavos);

        // The merged comanda is gone from the open list and refuses any later write; the history of both tells the merge.
        Assert.DoesNotContain(await ListAsync(client, t.Garcom), c => c.Id == terceira.Id);
        using var lancamento = await LaunchAsync(client, t.Garcom, terceira.Id, versaoT + 1, par.ProdutoId);
        await AssertErrorAsync(lancamento, HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertMovimentoErroAsync(await MergeAsync(client, t.Dono, par.Y, versaoY + 1, terceira.Id, versaoT + 1), HttpStatusCode.Conflict, "comanda-nao-aberta", terceira.Id);
        Assert.Contains(await HistoryAsync(client, t.Dono, terceira.Id), e => e.Acao == "comanda:juntar" && e.Depois["papel"] == "origem");
        Assert.Contains(await HistoryAsync(client, t.Dono, par.Y), e => e.Acao == "comanda:juntar" && e.Depois["papel"] == "destino");
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'comanda.juntada' AND payload->>'OrigemId' = '{terceira.Id:D}' AND payload->>'DestinoId' = '{par.Y:D}' AND payload->>'ItensMovidos' = '1'"));
    }

    [Fact]
    public async Task Separating_items_creates_a_new_comanda_with_the_next_number_the_same_waiter_and_fee_and_the_selected_items()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var par = await PairAsync(client, t, "SEP1");
        var antes = await ListAsync(client, t.Garcom);
        var maiorNumero = antes.Max(c => c.Numero);
        var escolhido = par.ItensDeX[1];

        await AssertMovimentoErroAsync(await SeparateAsync(client, t.Dono, par.X, par.VersaoX, null), HttpStatusCode.BadRequest, "invalido");
        await AssertMovimentoErroAsync(await SeparateAsync(client, t.Dono, par.X, par.VersaoX, new string('x', 41), escolhido.Id), HttpStatusCode.BadRequest, "invalido");
        await AssertMovimentoErroAsync(await SeparateAsync(client, t.Dono, par.X, par.VersaoX - 1, null, escolhido.Id), HttpStatusCode.Conflict, "versao-desatualizada", par.X, par.VersaoX);
        Assert.Equal(antes.Count, (await ListAsync(client, t.Garcom)).Count);

        var r = await BodyAsync<MovimentoBody>(await SeparateAsync(client, t.Dono, par.X, par.VersaoX, "  Mesa dos primos ", escolhido.Id), HttpStatusCode.Created);

        Assert.Equal((1, par.VersaoX + 1, 2, "Mesa dos primos", maiorNumero + 1, Garcom), (r.ItensMovidos, r.Origem.Versao, r.Destino.Versao, r.Destino.Identificacao, r.Destino.Numero, r.Destino.GarcomId));
        Assert.Equal([par.ItensDeX[0].Id], (await ItemsAsync(client, t.Garcom, par.X)).Itens.Select(i => i.Id));
        Assert.Equal([escolhido.Id], (await ItemsAsync(client, t.Garcom, r.Destino.Id)).Itens.Select(i => i.Id));
        Assert.Equal((10m, 2), (await QueryAsync(OrgA, UnitA, $"SELECT taxa_servico_percentual, versao FROM comandas WHERE id = '{r.Destino.Id:D}'", x => (x.GetDecimal(0), x.GetInt32(1)))).Single());
        Assert.Contains(await ListAsync(client, t.Garcom), c => c.Id == r.Destino.Id);
        Assert.Equal(1L, await CountAsync($"auditoria_registros WHERE acao = 'comanda:abrir' AND objeto_id = '{r.Destino.Id:D}'"));
        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'comanda:transferir-item'"));
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'comanda.transferida' AND payload->>'DestinoId' = '{r.Destino.Id:D}' AND payload->>'DestinoCriada' = 'true'"));

        // Without a name it is "Comanda N"; separating everything is allowed while nothing was received.
        var tudo = await BodyAsync<MovimentoBody>(await SeparateAsync(client, t.Dono, par.X, r.Origem.Versao, null, par.ItensDeX[0].Id), HttpStatusCode.Created);
        Assert.Equal($"Comanda {tudo.Destino.Numero}", tudo.Destino.Identificacao);
        Assert.Equal(0L, await CountAsync($"itens_comanda WHERE comanda_id = '{par.X:D}'"));
    }

    [Fact]
    public async Task The_three_routes_are_denied_without_their_permission_and_a_waiter_without_visibility_cannot_move_comandas_of_others()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mudo = await GiveMoverProfileAsync(api);
        var par = await PairAsync(client, t, "NEG1");
        var item = par.ItensDeX[0];

        foreach (var (metodo, caminho, corpo) in new (HttpMethod, string, object)[]
        {
            (HttpMethod.Post, $"/comandas/{par.X:D}/itens/transferir", new { versao = par.VersaoX, destinoId = par.Y, destinoVersao = par.VersaoY, itemIds = new[] { item.Id } }),
            (HttpMethod.Post, $"/comandas/{par.Y:D}/juntar", new { versao = par.VersaoY, origemId = par.X, origemVersao = par.VersaoX }),
            (HttpMethod.Post, $"/comandas/{par.X:D}/itens/separar", new { versao = par.VersaoX, itemIds = new[] { item.Id }, identificacao = (string?)null }),
        })
        {
            using var negada = await SendAsync(client, metodo, caminho, t.Garcom, corpo);
            Assert.Equal(HttpStatusCode.Forbidden, negada.StatusCode);
            Assert.Equal(string.Empty, await negada.Content.ReadAsStringAsync());
            using var anonima = await SendAsync(client, metodo, caminho, null, corpo);
            Assert.Equal(HttpStatusCode.Unauthorized, anonima.StatusCode);

            // Holding the permission is not enough to reach comandas of another waiter: they answer as if they did not exist.
            using var invisivel = await SendAsync(client, metodo, caminho, mudo, corpo);
            Assert.Equal(HttpStatusCode.NotFound, invisivel.StatusCode);
        }

        Assert.Equal((par.VersaoX, par.VersaoY), (await VersionOfAsync(par.X), await VersionOfAsync(par.Y)));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao IN ('comanda:transferir-item', 'comanda:juntar')"));

        // Their own comandas are movable by the same account, and a comanda of another waiter as the destination is not.
        var propria = await OpenAsync(client, mudo);
        var propriaItem = (await LaunchOkAsync(client, mudo, propria.Id, propria.Versao, par.ProdutoId)).Itens.Single();
        var outra = await OpenAsync(client, mudo);
        var ok = await BodyAsync<MovimentoBody>(await TransferAsync(client, mudo, propria.Id, propria.Versao + 1, outra.Id, outra.Versao, propriaItem.Id), HttpStatusCode.OK);
        Assert.Equal(1, ok.ItensMovidos);
        await AssertMovimentoErroAsync(await TransferAsync(client, mudo, outra.Id, ok.Destino.Versao, par.Y, par.VersaoY, propriaItem.Id), HttpStatusCode.NotFound, "nao-encontrada", par.Y);
    }

    [Fact]
    public async Task Another_organization_and_another_unit_cannot_be_reached_by_a_transfer_a_merge_or_a_separation()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var par = await PairAsync(client, t, "ISO1");
        var item = par.ItensDeX[0];
        var daOutraUnidade = Guid.NewGuid();
        await RunSqlAsync(OrgA, UnitA2, $"INSERT INTO comandas (id, organizacao_id, unidade_id, dia_operacional, numero, identificacao, garcom_id, aberta_em, taxa_servico_percentual) VALUES ('{daOutraUnidade:D}', '{OrgA:D}', '{UnitA2:D}', '2026-11-05', 1, 'Praia', '{Garcom:D}', now(), 10)");

        await AssertMovimentoErroAsync(await TransferAsync(client, t.DonoB, par.X, par.VersaoX, par.Y, par.VersaoY, item.Id), HttpStatusCode.NotFound, "nao-encontrada", par.X);
        await AssertMovimentoErroAsync(await MergeAsync(client, t.DonoB, par.Y, par.VersaoY, par.X, par.VersaoX), HttpStatusCode.NotFound, "nao-encontrada", par.X);
        await AssertMovimentoErroAsync(await SeparateAsync(client, t.DonoB, par.X, par.VersaoX, null, item.Id), HttpStatusCode.NotFound, "nao-encontrada", par.X);

        // The session's unit is UnitA: a comanda of UnitA2 is not there, as origin or destination.
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, par.X, par.VersaoX, daOutraUnidade, 1, item.Id), HttpStatusCode.NotFound, "nao-encontrada", daOutraUnidade);
        await AssertMovimentoErroAsync(await TransferAsync(client, t.Dono, daOutraUnidade, 1, par.Y, par.VersaoY, item.Id), HttpStatusCode.NotFound, "nao-encontrada", daOutraUnidade);
        await AssertMovimentoErroAsync(await MergeAsync(client, t.Dono, par.Y, par.VersaoY, daOutraUnidade, 1), HttpStatusCode.NotFound, "nao-encontrada", daOutraUnidade);

        Assert.Equal((par.VersaoX, par.VersaoY), (await VersionOfAsync(par.X), await VersionOfAsync(par.Y)));
        Assert.Equal(2L, await CountAsync($"itens_comanda WHERE comanda_id = '{par.X:D}'"));
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM comandas", r => r.GetInt64(0))).Single());
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao IN ('comanda:transferir-item', 'comanda:juntar')"));
    }

    [Fact]
    public async Task Two_opposite_transfers_at_once_never_deadlock_and_never_lose_or_duplicate_an_item()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "RACE2", "Chopp", 1000);

        for (var volta = 0; volta < 10; volta++)
        {
            var x = await OpenAsync(client, t.Garcom);
            var y = await OpenAsync(client, t.Garcom);
            var itemX = (await LaunchOkAsync(client, t.Garcom, x.Id, x.Versao, produto.Id)).Itens.Single();
            var itemY = (await LaunchOkAsync(client, t.Garcom, y.Id, y.Versao, produto.Id)).Itens.Single();
            var gate = new TaskCompletionSource();
            var statuses = new List<HttpStatusCode>();

            // Each side retries on a stale version with the versions read now, like a screen that refreshes; every attempt takes both comandas in the same order.
            async Task MoveAsync(Guid de, Guid para, Guid item)
            {
                await gate.Task;
                for (var tentativa = 0; tentativa < 20; tentativa++)
                {
                    using var response = await TransferAsync(client, t.Dono, de, await VersionOfAsync(de), para, await VersionOfAsync(para), item);
                    lock (statuses)
                    {
                        statuses.Add(response.StatusCode);
                    }

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }

                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                }

                Assert.Fail("The transfer never got through.");
            }

            var primeiro = MoveAsync(x.Id, y.Id, itemX.Id);
            var segundo = MoveAsync(y.Id, x.Id, itemY.Id);
            gate.SetResult();
            await Task.WhenAll(primeiro, segundo);

            Assert.DoesNotContain(statuses, s => s is not (HttpStatusCode.OK or HttpStatusCode.Conflict));
            Assert.Equal(2L, statuses.Count(s => s == HttpStatusCode.OK));
            var donos = await QueryAsync(OrgA, UnitA, $"SELECT id, comanda_id FROM itens_comanda WHERE comanda_id IN ('{x.Id:D}', '{y.Id:D}') ORDER BY id", r => (r.GetGuid(0), r.GetGuid(1)));
            Assert.Equal(2, donos.Count);
            Assert.Equal(y.Id, donos.Single(d => d.Item1 == itemX.Id).Item2);
            Assert.Equal(x.Id, donos.Single(d => d.Item1 == itemY.Id).Item2);
            Assert.Equal(0L, await CountAsync($"itens_comanda WHERE id IN ('{itemX.Id:D}', '{itemY.Id:D}') AND comanda_id NOT IN ('{x.Id:D}', '{y.Id:D}')"));
        }
    }

    [Fact]
    public async Task A_transfer_racing_a_merge_that_takes_the_same_comanda_loses_or_wins_whole_and_no_item_is_lost()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "RACE3", "Chopp", 1000);

        for (var volta = 0; volta < 6; volta++)
        {
            var x = await OpenAsync(client, t.Garcom);
            var y = await OpenAsync(client, t.Garcom);
            var itemX = (await LaunchOkAsync(client, t.Garcom, x.Id, x.Versao, produto.Id)).Itens.Single();
            var versaoX = x.Versao + 1;
            var gate = new TaskCompletionSource();

            async Task<HttpResponseMessage> TransferirAsync()
            {
                await gate.Task;
                return await TransferAsync(client, t.Dono, x.Id, versaoX, y.Id, y.Versao, itemX.Id);
            }

            async Task<HttpResponseMessage> JuntarAsync()
            {
                await gate.Task;
                return await MergeAsync(client, t.Dono, y.Id, y.Versao, x.Id, versaoX);
            }

            var a = TransferirAsync();
            var b = JuntarAsync();
            gate.SetResult();
            var (transferiu, juntou) = (await a, await b);
            using var _ = transferiu;
            using var __ = juntou;

            // Same versions on both: exactly one wins, the other is stale (or finds the comanda no longer open). The item is in Y either way, once.
            Assert.Equal(1, new[] { transferiu.StatusCode, juntou.StatusCode }.Count(s => s == HttpStatusCode.OK));
            Assert.DoesNotContain(new[] { transferiu.StatusCode, juntou.StatusCode }, s => s == HttpStatusCode.InternalServerError);
            Assert.Equal(1L, await CountAsync($"itens_comanda WHERE id = '{itemX.Id:D}' AND comanda_id = '{y.Id:D}'"));
            Assert.Equal(1L, await CountAsync($"itens_comanda WHERE id = '{itemX.Id:D}'"));
        }
    }
}
