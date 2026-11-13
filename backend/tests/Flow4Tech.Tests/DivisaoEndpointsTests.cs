using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0061: splitting the bill through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions). The owner holds every permission; "Garcom" does not hold
/// <c>comanda:dividir</c> (the denied case); "Mudo" holds it but NOT <c>comanda:ver-comandas-de-outros</c> (the visibility case). Comanda of the tests: three items of R$ 10,00 and a 10% fee, so the bill is R$ 33,00.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record ParteBody(int Numero, int? Assento, List<Guid> ItemIds, long ConsumoCentavos, long TaxaServicoCentavos, long TotalCentavos, long GorjetaCentavos, long APagarCentavos);

    private sealed record PreviaBody(string Modo, int ComandaVersao, long TotalCentavos, long TaxaServicoCentavos, long GorjetaCentavos, List<ParteBody> Partes);

    private sealed record DivisaoBody(Guid Id, Guid ComandaId, string Modo, int ComandaVersao, bool Vigente, long TotalCentavos, long TaxaServicoCentavos, long GorjetaCentavos, DateTimeOffset CriadoEm, List<ParteBody> Partes);

    private static Task<HttpResponseMessage> PreviewSplitAsync(HttpClient client, string? token, Guid comanda, object pedido) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/divisao/previa", token, pedido);

    private static Task<HttpResponseMessage> SplitAsync(HttpClient client, string? token, Guid comanda, Guid divisaoId, int versao, object pedido) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/divisao", token, new { divisaoId, versao, pedido });

    private static Task<HttpResponseMessage> GetSplitAsync(HttpClient client, string? token, Guid comanda) =>
        SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/divisao", token);

    private static readonly string[] PermissoesDeQuemDivide = ["comanda:ver", "comanda:abrir", "comanda:lancar-item", "comanda:dividir"];

    private static List<long> L(params long[] v) => [.. v];

    private static List<decimal> P(params decimal[] v) => [.. v];

    private static List<List<Guid>> Grupos(params Guid[][] g) => [.. g.Select(x => x.ToList())];

    private static object Equal(int partes, long? gorjeta = null) => new { modo = "iguais", partes, gorjetaCentavos = gorjeta };

    private sealed record Conta3(Guid ComandaId, int Versao, Guid ProdutoId, List<ItemBody> Itens);

    /// <summary>A comanda of the waiter with three items of R$ 10,00 (the 10% fee makes the bill R$ 33,00), the items carrying the given seats.</summary>
    private static async Task<Conta3> Bill3Async(HttpClient client, Tokens t, string codigo, int?[]? assentos = null)
    {
        var produto = await CreateProductAsync(client, t.Dono, codigo, "Produto " + codigo, 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var itens = Enumerable.Range(0, 3).Select(i => new { produtoId = produto.Id, quantidade = 1, observacao = (string?)null, assento = assentos?[i] }).ToArray();
        using var response = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new { rodadaId = NewRoundId(), versao = comanda.Versao, itens });
        var rodada = await BodyAsync<RodadaBody>(response, HttpStatusCode.Created);
        return new Conta3(comanda.Id, rodada.ComandaVersao, produto.Id, rodada.Itens);
    }

    private async Task<string> GiveSplitterProfileAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfil = await perfis.CriarAsync(request, "Divide", [.. PermissoesDeQuemDivide.Select(p => new RegraDeAcesso(RuleEffect.Allow, p))]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Mudo, perfil.Id!.Value));
        return await FreshTokenAsync(api, Mudo);
    }

    [Fact]
    public async Task A_preview_of_each_mode_is_read_only_idempotent_and_shares_the_bill_the_fee_and_the_tip()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var c = await Bill3Async(client, t, "DIV1", [1, 2, null]);
        var (a, b, d) = (c.Itens[0], c.Itens[1], c.Itens[2]);

        var iguais = await BodyAsync<PreviaBody>(await PreviewSplitAsync(client, t.Dono, c.ComandaId, Equal(7, 100)), HttpStatusCode.OK);
        Assert.Equal(("iguais", c.Versao, 3300L, 300L, 100L), (iguais.Modo, iguais.ComandaVersao, iguais.TotalCentavos, iguais.TaxaServicoCentavos, iguais.GorjetaCentavos));
        Assert.Equal([472L, 472L, 472L, 471L, 471L, 471L, 471L], iguais.Partes.Select(p => p.TotalCentavos));
        Assert.Equal(3300L, iguais.Partes.Sum(p => p.TotalCentavos));
        Assert.Equal(300L, iguais.Partes.Sum(p => p.TaxaServicoCentavos));
        Assert.Equal(100L, iguais.Partes.Sum(p => p.GorjetaCentavos));
        Assert.All(iguais.Partes, p => Assert.Equal(p.TotalCentavos + p.GorjetaCentavos, p.APagarCentavos));

        var assento = await BodyAsync<PreviaBody>(await PreviewSplitAsync(client, t.Dono, c.ComandaId, new { modo = "assento" }), HttpStatusCode.OK);
        Assert.Equal([1, 2], assento.Partes.Select(p => p.Assento));
        Assert.Equal([1650L, 1650L], assento.Partes.Select(p => p.TotalCentavos));
        Assert.Equal([[a.Id], [b.Id]], assento.Partes.Select(p => p.ItemIds));

        var porItem = await BodyAsync<PreviaBody>(await PreviewSplitAsync(client, t.Dono, c.ComandaId, new { modo = "item", grupos = Grupos([a.Id], [b.Id, d.Id]) }), HttpStatusCode.OK);
        Assert.Equal([1100L, 2200L], porItem.Partes.Select(p => p.TotalCentavos));

        var livres = await BodyAsync<PreviaBody>(await PreviewSplitAsync(client, t.Dono, c.ComandaId, new { modo = "valores", valores = L(2000, 1300) }), HttpStatusCode.OK);
        Assert.Equal([2000L, 1300L], livres.Partes.Select(p => p.TotalCentavos));

        var pct = await BodyAsync<PreviaBody>(await PreviewSplitAsync(client, t.Dono, c.ComandaId, new { modo = "percentual", percentuais = P(50m, 30m, 20m) }), HttpStatusCode.OK);
        Assert.Equal([1650L, 990L, 660L], pct.Partes.Select(p => p.TotalCentavos));

        // Repeating gives the same answer, and nothing was written anywhere: not the version, not a row, not an audit record, not an event.
        var denovo = await BodyAsync<PreviaBody>(await PreviewSplitAsync(client, t.Dono, c.ComandaId, Equal(7, 100)), HttpStatusCode.OK);
        Assert.Equal(iguais.Partes.Select(p => (p.TotalCentavos, p.GorjetaCentavos)), denovo.Partes.Select(p => (p.TotalCentavos, p.GorjetaCentavos)));
        Assert.Equal(c.Versao, await VersionOfAsync(c.ComandaId));
        Assert.Equal(0L, await CountAsync("divisoes"));
        Assert.Equal(0L, await CountAsync("divisao_partes"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'comanda:dividir'"));
        Assert.Equal(0L, await CountAsync("outbox_eventos WHERE tipo = 'comanda.dividida'"));
    }

    [Fact]
    public async Task A_preview_refuses_bad_requests_wrong_sums_and_a_comanda_that_cannot_be_split_and_says_why()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 1000);
        var c = await Bill3Async(client, t, "DIV2");
        var (a, b) = (c.Itens[0], c.Itens[1]);

        async Task AssertAsync(object pedido, HttpStatusCode status, string codigo) => await AssertErrorAsync(await PreviewSplitAsync(client, t.Dono, c.ComandaId, pedido), status, codigo);

        await AssertAsync(new { modo = "triplo" }, HttpStatusCode.BadRequest, "invalido");
        await AssertAsync(new { modo = "iguais" }, HttpStatusCode.BadRequest, "invalido");
        await AssertAsync(Equal(1), HttpStatusCode.BadRequest, "invalido");
        await AssertAsync(Equal(51), HttpStatusCode.BadRequest, "invalido");
        await AssertAsync(Equal(2, -1), HttpStatusCode.BadRequest, "invalido");
        await AssertAsync(new { modo = "assento" }, HttpStatusCode.BadRequest, "invalido");
        await AssertAsync(new { modo = "valores", valores = L(1000, 1000) }, HttpStatusCode.BadRequest, "soma-diferente");
        await AssertAsync(new { modo = "valores", valores = L(3400, -100) }, HttpStatusCode.BadRequest, "invalido");
        await AssertAsync(new { modo = "percentual", percentuais = P(50m, 49m) }, HttpStatusCode.BadRequest, "soma-diferente");
        await AssertAsync(new { modo = "item", grupos = Grupos([a.Id], [b.Id]) }, HttpStatusCode.BadRequest, "itens-fora-das-partes");
        await AssertAsync(new { modo = "item", grupos = Grupos([a.Id], [a.Id, b.Id, c.Itens[2].Id]) }, HttpStatusCode.BadRequest, "invalido");

        // Unknown comanda, then a comanda that already received money (the bill was not split before charging), then one that is not open any more.
        await AssertErrorAsync(await PreviewSplitAsync(client, t.Dono, Guid.NewGuid(), Equal(2)), HttpStatusCode.NotFound, "nao-encontrada");
        var pago = await PayOkAsync(client, t.Dono, c.ComandaId, 100, c.Versao);
        await AssertAsync(Equal(2), HttpStatusCode.Conflict, "valor-ja-recebido");
        Assert.Equal(pago.ComandaVersao, await VersionOfAsync(c.ComandaId));
        var vazia = await OpenAsync(client, t.Garcom);
        using var cancelada = await CancelComandaAsync(client, t.Dono, vazia.Id, vazia.Versao);
        Assert.Equal(HttpStatusCode.OK, cancelada.StatusCode);
        await AssertErrorAsync(await PreviewSplitAsync(client, t.Dono, vazia.Id, Equal(2)), HttpStatusCode.Conflict, "comanda-nao-aberta");
    }

    [Fact]
    public async Task Recording_a_split_bumps_the_version_writes_the_parts_audits_publishes_and_a_repeat_of_the_id_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var c = await Bill3Async(client, t, "DIV3");
        var id = Guid.CreateVersion7();

        var r = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, id, c.Versao, Equal(3, 100)), HttpStatusCode.Created);

        Assert.Equal((id, c.ComandaId, "iguais", c.Versao + 1, true, 3300L, 300L, 100L), (r.Id, r.ComandaId, r.Modo, r.ComandaVersao, r.Vigente, r.TotalCentavos, r.TaxaServicoCentavos, r.GorjetaCentavos));
        Assert.Equal([1100L, 1100L, 1100L], r.Partes.Select(p => p.TotalCentavos));
        Assert.Equal([34L, 33L, 33L], r.Partes.Select(p => p.GorjetaCentavos));
        Assert.Equal(c.Versao + 1, await VersionOfAsync(c.ComandaId));
        var linhas = await QueryAsync(OrgA, UnitA, $"SELECT modo, comanda_versao, total_centavos, gorjeta_centavos, criado_por, length(assinatura) FROM divisoes WHERE id = '{id:D}'", x => (x.GetString(0), x.GetInt32(1), x.GetInt64(2), x.GetInt64(3), x.GetGuid(4), x.GetInt32(5)));
        Assert.Equal([("iguais", c.Versao + 1, 3300L, 100L, DonoA, 64)], linhas);
        var partes = await QueryAsync(OrgA, UnitA, $"SELECT numero, total_centavos, gorjeta_centavos FROM divisao_partes WHERE divisao_id = '{id:D}' ORDER BY numero", x => (x.GetInt32(0), x.GetInt64(1), x.GetInt64(2)));
        Assert.Equal([(1, 1100L, 34L), (2, 1100L, 33L), (3, 1100L, 33L)], partes);
        var auditoria = await QueryAsync(OrgA, UnitA, $"SELECT usuario_id, depois->>'divisaoId', depois->>'modo', depois->>'partes' FROM auditoria_registros WHERE acao = 'comanda:dividir' AND objeto_id = '{c.ComandaId:D}'", x => (x.GetGuid(0), x.GetString(1), x.GetString(2), x.GetString(3)));
        Assert.Equal([(DonoA, id.ToString("D"), "iguais", "3")], auditoria);
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'comanda.dividida' AND payload->>'ComandaId' = '{c.ComandaId:D}' AND payload->>'DivisaoId' = '{id:D}' AND payload->>'Partes' = '3'"));
        Assert.Contains(await HistoryAsync(client, t.Dono, c.ComandaId), e => e.Acao == "comanda:dividir");

        // The same client id again (even with a stale version, as a retry of a lost answer has) answers the recorded split: 200, no new effect.
        var repetida = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, id, c.Versao, Equal(3, 100)), HttpStatusCode.OK);
        Assert.Equal(r.Partes.Select(p => p.TotalCentavos), repetida.Partes.Select(p => p.TotalCentavos));
        Assert.Equal((r.CriadoEm, r.ComandaVersao), (repetida.CriadoEm, repetida.ComandaVersao));
        Assert.Equal(c.Versao + 1, await VersionOfAsync(c.ComandaId));
        Assert.Equal(1L, await CountAsync("divisoes"));
        Assert.Equal(3L, await CountAsync("divisao_partes"));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'comanda:dividir'"));
        Assert.Equal(1L, await CountAsync("outbox_eventos WHERE tipo = 'comanda.dividida'"));

        // The id of a split belongs to its comanda; a stale version of a new id is 409 with the current one, and a refused request changes nothing.
        var outra = await Bill3Async(client, t, "DIV3B");
        await AssertErrorAsync(await SplitAsync(client, t.Dono, outra.ComandaId, id, outra.Versao, Equal(2)), HttpStatusCode.Conflict, "id-em-uso");
        await AssertErrorAsync(await SplitAsync(client, t.Dono, outra.ComandaId, Guid.CreateVersion7(), outra.Versao - 1, Equal(2)), HttpStatusCode.Conflict, "versao-desatualizada", outra.Versao);
        await AssertErrorAsync(await SplitAsync(client, t.Dono, outra.ComandaId, Guid.CreateVersion7(), outra.Versao, new { modo = "valores", valores = L(1, 2) }), HttpStatusCode.BadRequest, "soma-diferente");
        await AssertErrorAsync(await SplitAsync(client, t.Dono, outra.ComandaId, Guid.Empty, outra.Versao, Equal(2)), HttpStatusCode.BadRequest, "invalido");
        Assert.Equal(outra.Versao, await VersionOfAsync(outra.ComandaId));
        Assert.Equal(1L, await CountAsync("divisoes"));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'comanda:dividir'"));
    }

    [Fact]
    public async Task Changing_the_comanda_after_the_split_invalidates_it_and_a_new_split_replaces_it()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 1000);
        var c = await Bill3Async(client, t, "DIV4", [1, 2, 2]);

        await AssertErrorAsync(await GetSplitAsync(client, t.Dono, c.ComandaId), HttpStatusCode.NotFound, "sem-divisao");
        var id = Guid.CreateVersion7();
        var r = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, id, c.Versao, new { modo = "assento", gorjetaCentavos = 50 }), HttpStatusCode.Created);
        Assert.Equal([1, 2], r.Partes.Select(p => p.Assento));
        Assert.Equal([1100L, 2200L], r.Partes.Select(p => p.TotalCentavos));
        Assert.Equal(3300L, r.TotalCentavos);

        // Valid while nothing changed; the read does not touch anything.
        var vigente = await BodyAsync<DivisaoBody>(await GetSplitAsync(client, t.Dono, c.ComandaId), HttpStatusCode.OK);
        Assert.Equal((id, true), (vigente.Id, vigente.Vigente));
        Assert.Equal(r.ComandaVersao, await VersionOfAsync(c.ComandaId));

        // A payment does not change what the bill is made of, so it does not invalidate (it will reference the split).
        // Launching another round does: the split no longer matches the comanda.
        var mais = await LaunchOkAsync(client, t.Dono, c.ComandaId, r.ComandaVersao, c.ProdutoId);
        await AssertErrorAsync(await GetSplitAsync(client, t.Dono, c.ComandaId), HttpStatusCode.Conflict, "divisao-invalida");

        // A new split (new id) on the changed comanda is the one that counts now, and the old row stays as it was.
        var novo = Guid.CreateVersion7();
        var segunda = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, novo, mais.ComandaVersao, Equal(2)), HttpStatusCode.Created);
        Assert.Equal(4400L, segunda.TotalCentavos);
        var atual = await BodyAsync<DivisaoBody>(await GetSplitAsync(client, t.Dono, c.ComandaId), HttpStatusCode.OK);
        Assert.Equal((novo, true), (atual.Id, atual.Vigente));
        Assert.Equal(2L, await CountAsync($"divisoes WHERE comanda_id = '{c.ComandaId:D}'"));

        // Cancelling an item, a discount and removing the fee each invalidate it as well.
        using var cancelamento = await CancelAsync(client, t.Dono, c.ComandaId, mais.Itens.Single().Id, segunda.ComandaVersao, "erro-de-lancamento");
        Assert.Equal(HttpStatusCode.OK, cancelamento.StatusCode);
        await AssertErrorAsync(await GetSplitAsync(client, t.Dono, c.ComandaId), HttpStatusCode.Conflict, "divisao-invalida");
        var terceira = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, Guid.CreateVersion7(), segunda.ComandaVersao + 1, Equal(2)), HttpStatusCode.Created);
        var desconto = await BodyAsync<System.Text.Json.JsonElement>(await PercentAsync(client, t.Dono, c.ComandaId, terceira.ComandaVersao, 10m), HttpStatusCode.Created);
        await AssertErrorAsync(await GetSplitAsync(client, t.Dono, c.ComandaId), HttpStatusCode.Conflict, "divisao-invalida");
        var comDesconto = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, Guid.CreateVersion7(), desconto.GetProperty("conta").GetProperty("versao").GetInt32(), Equal(2)), HttpStatusCode.Created);
        Assert.Equal(2970L, comDesconto.TotalCentavos);

        // Once money was received the bill is not split any more, but the recorded split stays valid and readable.
        var pago = await PayOkAsync(client, t.Dono, c.ComandaId, 100, comDesconto.ComandaVersao);
        Assert.Equal(HttpStatusCode.OK, (await GetSplitAsync(client, t.Dono, c.ComandaId)).StatusCode);
        await AssertErrorAsync(await SplitAsync(client, t.Dono, c.ComandaId, Guid.CreateVersion7(), pago.ComandaVersao, Equal(2)), HttpStatusCode.Conflict, "valor-ja-recebido");
    }

    [Fact]
    public async Task Launching_with_a_seat_stores_it_and_refuses_a_seat_out_of_range()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "DIV5", "Produto", 1000);
        var comanda = await OpenAsync(client, t.Garcom);

        foreach (var assento in new[] { 0, 100, -1 })
        {
            await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new { rodadaId = NewRoundId(), versao = comanda.Versao, itens = new[] { new { produtoId = produto.Id, quantidade = 1, assento } } }), HttpStatusCode.BadRequest, "invalido");
        }

        Assert.Equal(comanda.Versao, await VersionOfAsync(comanda.Id));
        using var ok = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new { rodadaId = NewRoundId(), versao = comanda.Versao, itens = new[] { new { produtoId = produto.Id, quantidade = 1, assento = (int?)99 }, new { produtoId = produto.Id, quantidade = 1, assento = (int?)null } } });
        var rodada = await BodyAsync<RodadaBody>(ok, HttpStatusCode.Created);
        Assert.Equal([99, (int?)null], await QueryAsync(OrgA, UnitA, $"SELECT assento FROM itens_comanda WHERE rodada_id = '{rodada.Id:D}' ORDER BY posicao", x => x.IsDBNull(0) ? (int?)null : x.GetInt32(0)));
        using var itens = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda.Id:D}/itens", t.Garcom);
        var lidos = await itens.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal([99, (int?)null], lidos.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("assento").ValueKind == System.Text.Json.JsonValueKind.Null ? (int?)null : i.GetProperty("assento").GetInt32()));
    }

    [Fact]
    public async Task The_three_routes_are_denied_without_the_permission_a_waiter_without_visibility_and_another_organization_cannot_reach_the_comanda()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mudo = await GiveSplitterProfileAsync(api);
        var c = await Bill3Async(client, t, "DIV6");
        var id = Guid.CreateVersion7();
        var corpo = new { divisaoId = id, versao = c.Versao, pedido = Equal(2) };

        foreach (var (metodo, caminho, enviar) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, $"/comandas/{c.ComandaId:D}/divisao/previa", Equal(2)),
            (HttpMethod.Post, $"/comandas/{c.ComandaId:D}/divisao", corpo),
            (HttpMethod.Get, $"/comandas/{c.ComandaId:D}/divisao", null),
        })
        {
            using var negada = await SendAsync(client, metodo, caminho, t.Garcom, enviar);
            Assert.Equal(HttpStatusCode.Forbidden, negada.StatusCode);
            Assert.Equal(string.Empty, await negada.Content.ReadAsStringAsync());
            using var anonima = await SendAsync(client, metodo, caminho, null, enviar);
            Assert.Equal(HttpStatusCode.Unauthorized, anonima.StatusCode);

            // Holding the permission is not enough to reach the comanda of another waiter: it answers as if it did not exist.
            await AssertErrorAsync(await SendAsync(client, metodo, caminho, mudo, enviar), HttpStatusCode.NotFound, "nao-encontrada");

            // Nor from another organization.
            await AssertErrorAsync(await SendAsync(client, metodo, caminho, t.DonoB, enviar), HttpStatusCode.NotFound, "nao-encontrada");
        }

        Assert.Equal(c.Versao, await VersionOfAsync(c.ComandaId));
        Assert.Equal(0L, await CountAsync("divisoes"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'comanda:dividir'"));
        Assert.Equal(0L, await CountAsync("outbox_eventos WHERE tipo = 'comanda.dividida'"));
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM divisoes", r => r.GetInt64(0))).Single());

        // The same account splits its OWN comanda, and a comanda of another unit is not reachable either.
        var propria = await OpenAsync(client, mudo);
        var itens = await LaunchOkAsync(client, mudo, propria.Id, propria.Versao, c.ProdutoId);
        var ok = await BodyAsync<DivisaoBody>(await SplitAsync(client, mudo, propria.Id, id, itens.ComandaVersao, Equal(2)), HttpStatusCode.Created);
        Assert.Equal([550L, 550L], ok.Partes.Select(p => p.TotalCentavos));
        var daOutraUnidade = Guid.NewGuid();
        await RunSqlAsync(OrgA, UnitA2, $"INSERT INTO comandas (id, organizacao_id, unidade_id, dia_operacional, numero, identificacao, garcom_id, aberta_em, taxa_servico_percentual) VALUES ('{daOutraUnidade:D}', '{OrgA:D}', '{UnitA2:D}', '2026-11-05', 1, 'Outra', '{Garcom:D}', now(), 10)");
        await AssertErrorAsync(await PreviewSplitAsync(client, t.Dono, daOutraUnidade, Equal(2)), HttpStatusCode.NotFound, "nao-encontrada");
    }

    [Fact]
    public async Task The_same_split_sent_twice_at_once_is_recorded_once_and_two_splits_against_one_version_record_one()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        for (var volta = 0; volta < 6; volta++)
        {
            var c = await Bill3Async(client, t, "DIVR" + volta);
            var id = Guid.CreateVersion7();
            var gate = new TaskCompletionSource();

            async Task<HttpResponseMessage> EnviarAsync(Guid divisaoId, object pedido)
            {
                await gate.Task;
                return await SplitAsync(client, t.Dono, c.ComandaId, divisaoId, c.Versao, pedido);
            }

            // Same id twice: the one that loses the race on the version lock still answers the recorded split (a retry is never a conflict).
            var a = EnviarAsync(id, Equal(3));
            var b = EnviarAsync(id, Equal(3));
            gate.SetResult();
            var (ra, rb) = (await a, await b);
            using var _ = ra;
            using var __ = rb;
            Assert.All(new[] { ra.StatusCode, rb.StatusCode }, s => Assert.True(s is HttpStatusCode.Created or HttpStatusCode.OK, s.ToString()));
            Assert.Equal(1L, await CountAsync($"divisoes WHERE comanda_id = '{c.ComandaId:D}'"));
            Assert.Equal(3L, await CountAsync($"divisao_partes WHERE divisao_id = '{id:D}'"));
            Assert.Equal(1L, await CountAsync($"auditoria_registros WHERE acao = 'comanda:dividir' AND objeto_id = '{c.ComandaId:D}'"));
            Assert.Equal(c.Versao + 1, await VersionOfAsync(c.ComandaId));

            // Two DIFFERENT ids against the same version: exactly one wins, the other is stale.
            var d = await Bill3Async(client, t, "DIVS" + volta);
            var gate2 = new TaskCompletionSource();

            async Task<HttpResponseMessage> Enviar2Async()
            {
                await gate2.Task;
                return await SplitAsync(client, t.Dono, d.ComandaId, Guid.CreateVersion7(), d.Versao, Equal(2));
            }

            var x = Enviar2Async();
            var y = Enviar2Async();
            gate2.SetResult();
            var (rx, ry) = (await x, await y);
            using var ___ = rx;
            using var ____ = ry;
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], new[] { rx.StatusCode, ry.StatusCode }.Order());
            Assert.Equal(1L, await CountAsync($"divisoes WHERE comanda_id = '{d.ComandaId:D}'"));
            Assert.Equal(d.Versao + 1, await VersionOfAsync(d.ComandaId));
        }
    }
}
