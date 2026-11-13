using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0058: discount, courtesy and gift with limits decided by the engine, through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions).
/// "Mudo" holds a manager profile limited to 10 percent of discount and R$ 20,00 of courtesy or gift; "Supervisor" holds a discount limited by a daily cap of R$ 30,00; "Garcom" holds none of the new
/// permissions; the owner holds everything without limits.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record DescontoLido(Guid Id, Guid ComandaId, Guid? ItemId, string Forma, decimal? Percentual, long? ValorCentavos, long AplicadoCentavos, string Motivo, Guid CriadoPor);

    private sealed record ContaLida(long ConsumoCentavos, long DescontoCentavos, long TaxaServicoCentavos, long TotalCentavos, long PagoCentavos, long SaldoCentavos, int Versao, string Estado);

    private sealed record DescontoAplicadoBody(DescontoLido Desconto, ContaLida Conta);

    private sealed record ItemDadoLido(Guid Id, long PrecoCentavos, int Quantidade, string Tipo, string? MotivoDoTipo, long TotalCentavos, long ValorDeTabelaCentavos);

    private sealed record ItemDadoBody(ItemDadoLido Item, ContaLida Conta);

    private sealed record Limitados(string Gerente, string Capado);

    private static Task<HttpResponseMessage> DiscountAsync(HttpClient client, string? token, Guid comanda, int versao, object? corpo) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/descontos", token, corpo is null ? new { versao } : corpo);

    private static Task<HttpResponseMessage> PercentAsync(HttpClient client, string? token, Guid comanda, int versao, decimal percentual, Guid? item = null, string motivo = "cliente antigo") =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/descontos", token, new { versao, itemId = item, percentual, motivo });

    private static Task<HttpResponseMessage> ValueAsync(HttpClient client, string? token, Guid comanda, int versao, long valorCentavos, Guid? item = null, string motivo = "ajuste") =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/descontos", token, new { versao, itemId = item, valorCentavos, motivo });

    private static Task<HttpResponseMessage> CourtesyAsync(HttpClient client, string? token, Guid comanda, Guid item, int versao, string motivo = "aniversario") =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/itens/{item:D}/cortesia", token, new { versao, motivo });

    private static Task<HttpResponseMessage> GiftAsync(HttpClient client, string? token, Guid comanda, Guid item, int versao, string motivo = "degustacao") =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/itens/{item:D}/brinde", token, new { versao, motivo });

    private static async Task AssertForbiddenEmptyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The limited personas: the manager (Mudo) and the capped one (Supervisor), each with the read permissions they need to reach other waiters' comandas.</summary>
    private async Task<Limitados> GiveDiscountProfilesAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var leitura = new[] { "comanda:ver", "comanda:ver-comandas-de-outros" };
        var gerente = await perfis.CriarAsync(request, "Gerente de descontos", [.. leitura.Select(p => new RegraDeAcesso(RuleEffect.Allow, p)), new RegraDeAcesso(RuleEffect.Allow, "comanda:desconto", MaxPercent: 10m), new RegraDeAcesso(RuleEffect.Allow, "comanda:cortesia", MaxValueCents: 2000), new RegraDeAcesso(RuleEffect.Allow, "comanda:brinde", MaxValueCents: 2000)]);
        var capado = await perfis.CriarAsync(request, "Desconto com teto", [.. leitura.Select(p => new RegraDeAcesso(RuleEffect.Allow, p)), new RegraDeAcesso(RuleEffect.Allow, "comanda:desconto", DailyCapCents: 3000)]);
        Assert.Equal(ResultadoGestao.Ok, gerente.Resultado);
        Assert.Equal(ResultadoGestao.Ok, capado.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Mudo, gerente.Id!.Value));
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Supervisor, capado.Id!.Value));
        return new Limitados(await FreshTokenAsync(api, Mudo), await FreshTokenAsync(api, Supervisor));
    }

    private async Task<long> ClosedSuccessAuditsAsync(string acao) => await CountAsync($"auditoria_registros WHERE acao = '{acao}' AND resultado = 'sucesso'");

    private sealed record RegrasDeDescontoLidas(bool Desconto, bool Cortesia, bool Brinde);

    [Fact]
    public async Task The_discount_visibility_route_tells_only_whether_a_rule_exists_decided_by_the_engine_and_never_reserves_a_cap()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var limitados = await GiveDiscountProfilesAsync(api);

        async Task<RegrasDeDescontoLidas> AskAsync(string token)
        {
            using var response = await SendAsync(client, HttpMethod.Get, "/sessao/regras-de-desconto", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var texto = await response.Content.ReadAsStringAsync();
            using var json = System.Text.Json.JsonDocument.Parse(texto);
            var corpo = json.RootElement;
            Assert.Equal(["brinde", "cortesia", "desconto"], corpo.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
            return new RegrasDeDescontoLidas(corpo.GetProperty("desconto").GetBoolean(), corpo.GetProperty("cortesia").GetBoolean(), corpo.GetProperty("brinde").GetBoolean());
        }

        Assert.Equal(new RegrasDeDescontoLidas(true, true, true), await AskAsync(t.Dono));
        Assert.Equal(new RegrasDeDescontoLidas(true, true, true), await AskAsync(limitados.Gerente));
        Assert.Equal(new RegrasDeDescontoLidas(true, false, false), await AskAsync(limitados.Capado));
        Assert.Equal(new RegrasDeDescontoLidas(false, false, false), await AskAsync(t.Garcom));
        Assert.Equal(new RegrasDeDescontoLidas(true, true, true), await AskAsync(t.DonoB));
        using var semSessao = await SendAsync(client, HttpMethod.Get, "/sessao/regras-de-desconto", null);
        Assert.Equal(HttpStatusCode.Unauthorized, semSessao.StatusCode);

        // Asking the capped profile reserved nothing: the cap counter is untouched.
        Assert.Equal(0L, await CountAsync("limites_consumo"));
    }

    [Fact]
    public async Task A_percent_discount_on_an_item_is_computed_on_the_server_audited_published_and_the_fee_follows_the_discounted_consumption()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var gerente = (await GiveDiscountProfilesAsync(api)).Gerente;
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "DESC1");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();

        var r = await BodyAsync<DescontoAplicadoBody>(await PercentAsync(client, gerente, comanda, versao, 10m, item.Id, "cliente antigo"), HttpStatusCode.Created);

        // 10% of the line 20,00 = 2,00; consumption 20,00 - 2,00 = 18,00; fee 10% of 18,00 = 1,80; total 19,80.
        Assert.Equal(("percentual", 10m, null, 200L, item.Id), (r.Desconto.Forma, r.Desconto.Percentual, r.Desconto.ValorCentavos, r.Desconto.AplicadoCentavos, r.Desconto.ItemId));
        Assert.Equal((2000L, 200L, 180L, 1980L, versao + 1), (r.Conta.ConsumoCentavos, r.Conta.DescontoCentavos, r.Conta.TaxaServicoCentavos, r.Conta.TotalCentavos, r.Conta.Versao));
        var conta = await BillAsync(client, t.Dono, comanda);
        Assert.Equal((1980L, versao + 1), (conta.TotalCentavos, conta.Versao));
        var linha = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT forma, percentual, valor_centavos, aplicado_centavos, motivo, criado_por, item_id FROM descontos_comanda WHERE comanda_id = '{comanda:D}'", x => (x.GetString(0), x.GetDecimal(1), x.IsDBNull(2), x.GetInt64(3), x.GetString(4), x.GetGuid(5), x.GetGuid(6))));
        Assert.Equal(("percentual", 10m, true, 200L, "cliente antigo", Mudo, item.Id), linha);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_tipo, objeto_id, depois->>'aplicadoCentavos', depois->>'motivo', depois->>'forma' FROM auditoria_registros WHERE acao = 'comanda:desconto' AND resultado = 'sucesso'", x => (x.GetGuid(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4), x.GetString(5))));
        Assert.Equal((Mudo, "comanda", comanda.ToString("D"), "200", "cliente antigo", "percentual"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'comanda.desconto-aplicado' AND payload->>'ComandaId' = '{comanda:D}' AND (payload->>'AplicadoCentavos')::bigint = 200", x => x.GetInt32(0)));
        using var listagem = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/descontos", t.Dono);
        Assert.Equal(r.Desconto.Id, (await listagem.Content.ReadFromJsonAsync<List<DescontoLido>>())!.Single().Id);
        Assert.Contains(await HistoryAsync(client, t.Dono, comanda), e => e.Acao == "comanda:desconto" && e.Depois["aplicadoCentavos"] == "200");
    }

    [Fact]
    public async Task A_value_discount_on_the_comanda_is_measured_by_its_real_share_of_the_base_and_a_percent_above_the_limit_is_403_without_reason_and_audited()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var gerente = (await GiveDiscountProfilesAsync(api)).Gerente;
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "DESC2");

        // Beyond the 10 percent limit: by percent, and by a value that is 50 percent of the 20,00 base.
        await AssertForbiddenEmptyAsync(await PercentAsync(client, gerente, comanda, versao, 10.01m));
        await AssertForbiddenEmptyAsync(await ValueAsync(client, gerente, comanda, versao, 1000));
        Assert.Equal((versao, 0L), (await VersionOfAsync(comanda), await CountAsync("descontos_comanda")));
        Assert.Equal(0L, await ClosedSuccessAuditsAsync("comanda:desconto"));
        var negados = await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_tipo, objeto_id, depois->>'permissao' FROM auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' AND objeto_tipo = 'comanda'", x => (x.GetGuid(0), x.GetString(1), x.GetString(2), x.GetString(3)));
        Assert.Equal(2, negados.Count);
        Assert.All(negados, n => Assert.Equal((Mudo, "comanda", comanda.ToString("D"), "comanda:desconto"), n));

        // Within the limit (7,5 percent) it goes, and a later percent is measured against what is left.
        var ok = await BodyAsync<DescontoAplicadoBody>(await ValueAsync(client, gerente, comanda, versao, 150), HttpStatusCode.Created);
        Assert.Equal(("valor", 150L, null), (ok.Desconto.Forma, ok.Desconto.AplicadoCentavos, ok.Desconto.Percentual));
        Assert.Equal((2000L, 150L, 185L, 2035L), (ok.Conta.ConsumoCentavos, ok.Conta.DescontoCentavos, ok.Conta.TaxaServicoCentavos, ok.Conta.TotalCentavos));
    }

    [Fact]
    public async Task A_discount_is_refused_for_bad_input_for_more_than_what_is_chargeable_and_for_a_stale_version_or_a_wrong_target()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, produto) = await ComandaWithConsumoAsync(client, t, 1000, 2, "DESC3");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();
        var outra = await OpenAsync(client, t.Garcom);
        var outraRodada = await LaunchOkAsync(client, t.Garcom, outra.Id, outra.Versao, produto.Id);

        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/descontos", t.Dono, new { versao, percentual = 10m, valorCentavos = 100L, motivo = "x" }), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/descontos", t.Dono, new { versao, motivo = "x" }), HttpStatusCode.BadRequest, "invalido");
        foreach (var percentual in new[] { 0m, -5m, 100.01m, 10.005m })
        {
            await AssertErrorAsync(await PercentAsync(client, t.Dono, comanda, versao, percentual), HttpStatusCode.BadRequest, "invalido");
        }

        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao, 0), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao, 100, motivo: "  "), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao, 100, motivo: new string('x', 201)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, 0, 100), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao, 2001, item.Id), HttpStatusCode.Conflict, "desconto-excede");
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao, 2001), HttpStatusCode.Conflict, "desconto-excede");
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao - 1, 100), HttpStatusCode.Conflict, "versao-desatualizada", versao);
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao, 100, Guid.NewGuid()), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao, 100, outraRodada.Itens.Single().Id), HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal((versao, 0L), (await VersionOfAsync(comanda), await CountAsync("descontos_comanda")));

        // The whole line, then nothing is left to discount; a cancelled item and a closed comanda take none.
        var inteiro = await BodyAsync<DescontoAplicadoBody>(await PercentAsync(client, t.Dono, comanda, versao, 100m, item.Id), HttpStatusCode.Created);
        Assert.Equal((2000L, 0L), (inteiro.Desconto.AplicadoCentavos, inteiro.Conta.TotalCentavos));
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao + 1, 1, item.Id), HttpStatusCode.Conflict, "desconto-excede");
        using var cancelamento = await CancelAsync(client, t.Dono, comanda, item.Id, versao + 1, "erro-de-lancamento");
        Assert.Equal(HttpStatusCode.OK, cancelamento.StatusCode);
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao + 2, 1, item.Id), HttpStatusCode.Conflict, "item-ja-cancelado");
        using var fechar = await CloseAsync(client, t.Dono, comanda, versao + 2);
        Assert.Equal(HttpStatusCode.OK, fechar.StatusCode);
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao + 3, 1), HttpStatusCode.Conflict, "comanda-nao-aberta");
    }

    [Fact]
    public async Task A_discount_that_would_put_the_bill_below_what_was_received_is_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "DESC4");
        var pago = await PayOkAsync(client, t.Dono, comanda, 1500, versao);

        // Bill 22,00, paid 15,00: taking 15,00 off leaves 5,00 + 0,50 < 15,00.
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, pago.ComandaVersao, 1500), HttpStatusCode.Conflict, "valor-ja-recebido");
        Assert.Equal(0L, await CountAsync("descontos_comanda"));
        // 5,00 off leaves 15,00 + 1,50 = 16,50: balance 1,50, allowed.
        var ok = await BodyAsync<DescontoAplicadoBody>(await ValueAsync(client, t.Dono, comanda, pago.ComandaVersao, 500), HttpStatusCode.Created);
        Assert.Equal((1650L, 150L), (ok.Conta.TotalCentavos, ok.Conta.SaldoCentavos));
    }

    [Fact]
    public async Task A_courtesy_charges_zero_keeps_the_original_price_and_reason_is_audited_and_publishes_what_the_stock_write_off_needs()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var gerente = (await GiveDiscountProfilesAsync(api)).Gerente;
        var (comanda, versao, produto) = await ComandaWithConsumoAsync(client, t, 1000, 2, "CORT1");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();

        var r = await BodyAsync<ItemDadoBody>(await CourtesyAsync(client, gerente, comanda, item.Id, versao, "  aniversario do Joao "), HttpStatusCode.OK);

        Assert.Equal(("cortesia", "aniversario do Joao", 1000L, 0L, 2000L), (r.Item.Tipo, r.Item.MotivoDoTipo, r.Item.PrecoCentavos, r.Item.TotalCentavos, r.Item.ValorDeTabelaCentavos));
        Assert.Equal((0L, 0L, versao + 1), (r.Conta.TotalCentavos, r.Conta.ConsumoCentavos, r.Conta.Versao));
        var salvo = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT tipo, motivo_tipo, tipo_por, tipo_em IS NOT NULL, preco_centavos, quantidade, estado FROM itens_comanda WHERE id = '{item.Id:D}'", x => (x.GetString(0), x.GetString(1), x.GetGuid(2), x.GetBoolean(3), x.GetInt64(4), x.GetInt32(5), x.GetString(6))));
        Assert.Equal(("cortesia", "aniversario do Joao", Mudo, true, 1000L, 2, "lancado"), salvo);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_tipo, objeto_id, antes->>'tipo', depois->>'tipo', depois->>'valorDeTabelaCentavos' FROM auditoria_registros WHERE acao = 'comanda:cortesia' AND resultado = 'sucesso'", x => (x.GetGuid(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4), x.GetString(5))));
        Assert.Equal((Mudo, "item-comanda", item.Id.ToString("D"), "normal", "cortesia", "2000"), audit);
        var evento = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT payload->>'ProdutoId', payload->>'ProdutoNome', payload->>'Quantidade', payload->>'ValorDeTabelaCentavos', payload->>'Motivo' FROM outbox_eventos WHERE tipo = 'comanda.item-cortesia' AND payload->>'ItemId' = '{item.Id:D}'", x => (x.GetString(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4))));
        Assert.Equal((produto.Id.ToString("D"), "Produto CORT1", "2", "2000", "aniversario do Joao"), evento);
        var itens = await ItemsAsync(client, t.Dono, comanda);
        Assert.Equal(0L, itens.ConsumoCentavos);

        // It is no longer a normal item: neither another courtesy nor a gift, and a discount on it takes nothing more.
        await AssertErrorAsync(await CourtesyAsync(client, t.Dono, comanda, item.Id, versao + 1), HttpStatusCode.Conflict, "item-nao-normal");
        await AssertErrorAsync(await GiftAsync(client, t.Dono, comanda, item.Id, versao + 1), HttpStatusCode.Conflict, "item-nao-normal");
        await AssertErrorAsync(await ValueAsync(client, t.Dono, comanda, versao + 1, 100, item.Id), HttpStatusCode.Conflict, "desconto-excede");
    }

    [Fact]
    public async Task A_gift_has_its_own_permission_event_and_limit_and_one_above_the_limit_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var gerente = (await GiveDiscountProfilesAsync(api)).Gerente;
        var produto = await CreateProductAsync(client, t.Dono, "BRIN1", "Dose", 1500);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, 2);
        var item = rodada.Itens.Single();

        // 2 x 15,00 = 30,00 is above the R$ 20,00 limit; the denial rolls everything back.
        await AssertForbiddenEmptyAsync(await GiftAsync(client, gerente, comanda.Id, item.Id, rodada.ComandaVersao));
        Assert.Equal(("lancado", "normal", rodada.ComandaVersao), (await QueryAsync(OrgA, UnitA, $"SELECT i.estado, i.tipo, c.versao FROM itens_comanda i JOIN comandas c ON c.id = i.comanda_id WHERE i.id = '{item.Id:D}'", x => (x.GetString(0), x.GetString(1), x.GetInt32(2)))).Single());
        Assert.Equal(0L, await ClosedSuccessAuditsAsync("comanda:brinde"));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' AND depois->>'permissao' = 'comanda:brinde'"));

        // One unit is 15,00 and fits.
        using var baixa = await AlterItemAsync(client, t.Dono, comanda.Id, item.Id, rodada.ComandaVersao, 1);
        Assert.Equal(HttpStatusCode.OK, baixa.StatusCode);
        var r = await BodyAsync<ItemDadoBody>(await GiftAsync(client, gerente, comanda.Id, item.Id, rodada.ComandaVersao + 1, "degustacao"), HttpStatusCode.OK);
        Assert.Equal(("brinde", 0L, 1500L), (r.Item.Tipo, r.Item.TotalCentavos, r.Item.ValorDeTabelaCentavos));
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'comanda.item-brinde' AND payload->>'ItemId' = '{item.Id:D}'", x => x.GetInt32(0)));
        Assert.Equal(0L, await CountAsync("outbox_eventos WHERE tipo = 'comanda.item-cortesia'"));
    }

    [Fact]
    public async Task The_three_actions_need_their_own_limited_permission_a_session_and_a_visible_comanda()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await GiveDiscountProfilesAsync(api);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "PERM2");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();

        foreach (var token in new string?[] { null, t.Garcom })
        {
            var esperado = token is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            Assert.Equal(esperado, (await PercentAsync(client, token, comanda, versao, 5m)).StatusCode);
            Assert.Equal(esperado, (await CourtesyAsync(client, token, comanda, item.Id, versao)).StatusCode);
            Assert.Equal(esperado, (await GiftAsync(client, token, comanda, item.Id, versao)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/descontos", null)).StatusCode);
        Assert.Equal((versao, 0L), (await VersionOfAsync(comanda), await CountAsync("descontos_comanda")));
        // The waiter who owns the comanda holds none of the three: each refusal is 403 without reason and audited as a denial of that permission (nothing else is written).
        var negados = await QueryAsync(OrgA, UnitA, "SELECT depois->>'permissao' FROM auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' ORDER BY 1", x => x.GetString(0));
        Assert.Equal(["comanda:brinde", "comanda:cortesia", "comanda:desconto"], negados);
        // Another waiter, who cannot see it, learns nothing: it is as if it did not exist.
        await AssertErrorAsync(await PercentAsync(client, t.Garcom2, comanda, versao, 5m), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await CourtesyAsync(client, t.Garcom2, comanda, item.Id, versao), HttpStatusCode.NotFound, "nao-encontrada");

        // A manager who cannot see other waiters' comandas does not see this one.
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var sem = await perfis.CriarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), "Desconto sem ver outros", [new RegraDeAcesso(RuleEffect.Allow, "comanda:ver"), new RegraDeAcesso(RuleEffect.Allow, "comanda:desconto")]);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), Garcom2, sem.Id!.Value));
        var cego = await FreshTokenAsync(api, Garcom2);
        await AssertErrorAsync(await PercentAsync(client, cego, comanda, versao, 5m), HttpStatusCode.NotFound, "nao-encontrada");
        Assert.NotEqual(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/descontos", cego)).StatusCode);
        Assert.Equal(0L, await CountAsync("descontos_comanda"));
    }

    [Fact]
    public async Task The_daily_cap_is_reserved_with_the_discount_and_a_refused_one_gives_the_budget_back()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var capado = (await GiveDiscountProfilesAsync(api)).Capado;
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 5000, 2, "CAP1");

        // Cap R$ 30,00: 20,00 fits (10,00 left), 15,00 does not (and keeps nothing), 10,00 fits.
        var primeira = await BodyAsync<DescontoAplicadoBody>(await ValueAsync(client, capado, comanda, versao, 2000), HttpStatusCode.Created);
        await AssertForbiddenEmptyAsync(await ValueAsync(client, capado, comanda, primeira.Conta.Versao, 1500));
        Assert.Equal((versao + 1, 1L), (await VersionOfAsync(comanda), await CountAsync("descontos_comanda")));
        var terceira = await BodyAsync<DescontoAplicadoBody>(await ValueAsync(client, capado, comanda, primeira.Conta.Versao, 1000), HttpStatusCode.Created);
        await AssertForbiddenEmptyAsync(await ValueAsync(client, capado, comanda, terceira.Conta.Versao, 1));
        Assert.Equal(3000L, (await QueryAsync(OrgA, UnitA, $"SELECT sum(aplicado_centavos) FROM descontos_comanda WHERE comanda_id = '{comanda:D}'", x => x.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Concurrent_discounts_on_different_comandas_never_pass_the_daily_cap_of_one_person()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var capado = (await GiveDiscountProfilesAsync(api)).Capado;
        var produto = await CreateProductAsync(client, t.Dono, "CAP2", "Garrafa", 10_000);
        var alvos = new List<(Guid Id, int Versao)>();
        for (var i = 0; i < 8; i++)
        {
            var c = await OpenAsync(client, t.Garcom);
            var rodada = await LaunchOkAsync(client, t.Garcom, c.Id, c.Versao, produto.Id);
            alvos.Add((c.Id, rodada.ComandaVersao));
        }

        var gate = new TaskCompletionSource();

        async Task<HttpResponseMessage> Fire((Guid Id, int Versao) alvo)
        {
            await gate.Task;
            return await ValueAsync(client, capado, alvo.Id, alvo.Versao, 1000);
        }

        var respostas = alvos.Select(Fire).ToList();
        gate.SetResult();
        var status = (await Task.WhenAll(respostas)).Select(r => r.StatusCode).ToList();

        // R$ 30,00 of cap and R$ 10,00 each: exactly three go through, the other five are refused whole.
        Assert.Equal((3, 5), (status.Count(s => s == HttpStatusCode.Created), status.Count(s => s == HttpStatusCode.Forbidden)));
        Assert.Equal((3L, 3000L), (await CountAsync("descontos_comanda"), (await QueryAsync(OrgA, UnitA, "SELECT coalesce(sum(aplicado_centavos), 0) FROM descontos_comanda", x => x.GetInt64(0))).Single()));
        Assert.Equal(3L, await ClosedSuccessAuditsAsync("comanda:desconto"));
    }

    [Fact]
    public async Task A_failure_after_the_audit_and_the_event_rolls_the_discount_back_completely()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "ROLL2");
        await using var falha = new Api(_appConnection, configure: services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IEventosDeComandas));
            services.Remove(real);
            services.AddSingleton<IEventosDeComandas>(p => new EventsThatThrow(ActivatorUtilities.CreateInstance<Flow4Tech.Comandas.Infrastructure.OutboxEventosDeComandas>(p)));
        });
        var gestao = falha.Services.GetRequiredService<GestaoDeDescontos>();
        var dono = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gestao.AplicarAsync(dono, comanda, versao, null, 10m, null, "teste"));
        var item = (await ItemsAsync(client, t.Dono, comanda)).Itens.Single();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gestao.DarCortesiaAsync(dono, comanda, item.Id, versao, "teste"));

        Assert.Equal(versao, await VersionOfAsync(comanda));
        Assert.Equal((0L, 0L, 0L), (await CountAsync("descontos_comanda"), await CountAsync("auditoria_registros WHERE acao IN ('comanda:desconto', 'comanda:cortesia')"), await CountAsync("outbox_eventos WHERE tipo IN ('comanda.desconto-aplicado', 'comanda.item-cortesia')")));
        Assert.Equal("normal", (await QueryAsync(OrgA, UnitA, $"SELECT tipo FROM itens_comanda WHERE id = '{item.Id:D}'", x => x.GetString(0))).Single());
    }

    [Fact]
    public async Task Another_organization_reaches_none_of_it_and_the_database_keeps_discounts_written_once_and_consistent()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "ISO2");
        var item = (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single();
        var feito = await BodyAsync<DescontoAplicadoBody>(await ValueAsync(client, t.Dono, comanda, versao, 100), HttpStatusCode.Created);

        await AssertErrorAsync(await PercentAsync(client, t.DonoB, comanda, feito.Conta.Versao, 5m), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await CourtesyAsync(client, t.DonoB, comanda, item.Id, feito.Conta.Versao), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await GiftAsync(client, t.DonoB, comanda, item.Id, feito.Conta.Versao), HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/descontos", t.DonoB)).StatusCode);
        Assert.Equal((feito.Conta.Versao, 1L), (await VersionOfAsync(comanda), await CountAsync("descontos_comanda")));

        // Written once: no UPDATE or DELETE for the application credential; the checks refuse half-filled rows.
        await Assert.ThrowsAnyAsync<Exception>(() => RunSqlAsync(OrgA, UnitA, "UPDATE descontos_comanda SET aplicado_centavos = 1"));
        await Assert.ThrowsAnyAsync<Exception>(() => RunSqlAsync(OrgA, UnitA, "DELETE FROM descontos_comanda"));
        await Assert.ThrowsAnyAsync<Exception>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE itens_comanda SET tipo = 'cortesia' WHERE id = '{item.Id:D}'"));
        await Assert.ThrowsAnyAsync<Exception>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE itens_comanda SET tipo = 'estranho', motivo_tipo = 'x', tipo_por = '{DonoA:D}', tipo_em = now() WHERE id = '{item.Id:D}'"));
        // An item of another comanda cannot carry this comanda's discount (composite foreign key).
        var outra = await OpenAsync(client, t.Garcom);
        var outraRodada = await LaunchOkAsync(client, t.Garcom, outra.Id, outra.Versao, (await ItemsAsync(client, t.Garcom, comanda)).Itens.Single().ProdutoId);
        await Assert.ThrowsAnyAsync<Exception>(() => RunSqlAsync(OrgA, UnitA, $"INSERT INTO descontos_comanda (id, organizacao_id, unidade_id, comanda_id, item_id, forma, valor_centavos, aplicado_centavos, motivo, criado_por, criado_em) VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', '{UnitA:D}', '{comanda:D}', '{outraRodada.Itens.Single().Id:D}', 'valor', 10, 10, 'x', '{DonoA:D}', now())"));
    }
}
