using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Comandas.Infrastructure;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0052: quick-launch data through the real authenticated routes (same fixture as the comandas: real PostgreSQL, application credential with RLS, real sessions). Covers "repeat the last round"
/// (new round from the previous one, prices re-read, unavailable items reported, idempotent by the client id, stale version, denial, other waiter and other organization), the favorites feed
/// written on launch per waiter, and "last time at this table" (closed comandas of that table, that unit, visibility of other waiters' comandas, empty, denial, other organization).
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record IgnoradoBody(Guid ProdutoId, string ProdutoNome, int Quantidade, string Motivo);

    private sealed record RepetidaBody(RodadaComOpcoesBody Rodada, List<IgnoradoBody> Ignorados);

    private sealed record RepeticaoErroBody(string Codigo, int? VersaoAtual, List<IgnoradoBody> Ignorados);

    private sealed record UltimaVezBody(Guid? ComandaId, int? Numero, string? NumeroTexto, DateTimeOffset? FechadaEm, List<ItemComOpcoesBody> Itens);

    private static Task<HttpResponseMessage> RepeatAsync(HttpClient client, string? token, Guid comanda, int versao, string? rodadaId = null) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/rodadas/repetir", token, new { rodadaId = rodadaId ?? NewRoundId(), versao });

    private static async Task<RepetidaBody> RepeatOkAsync(HttpClient client, string token, Guid comanda, int versao)
    {
        using var response = await RepeatAsync(client, token, comanda, versao);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RepetidaBody>())!;
    }

    private static async Task AssertRepeatRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string codigo, int? versaoAtual = null)
    {
        Assert.Equal(status, response.StatusCode);
        var erro = (await response.Content.ReadFromJsonAsync<RepeticaoErroBody>())!;
        Assert.Equal((codigo, versaoAtual), (erro.Codigo, erro.VersaoAtual));
    }

    private static async Task PutProductAsync(HttpClient client, string token, ProdutoBody produto, long preco, int versao, bool indisponivel = false, bool ativo = true)
    {
        using var response = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id:D}", token, new { codigo = produto.Codigo, nome = produto.Nome, precoCentavos = preco, categoriaId = produto.CategoriaId, ativo, versao, indisponivel });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Repeating_the_last_round_makes_a_new_round_re_reads_the_prices_reports_what_is_unavailable_and_is_idempotent_by_the_client_id()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var porcao = await CreateProductAsync(client, t.Dono, "PORCAO", "Porcao", 2000);
        var comanda = await OpenAsync(client, t.Garcom);
        using var primeira = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new
        {
            rodadaId = NewRoundId(),
            versao = comanda.Versao,
            itens = new object[] { new { produtoId = chopp.Id, quantidade = 2, observacao = "sem colarinho" }, new { produtoId = porcao.Id, quantidade = 1 } },
        });
        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        var original = (await primeira.Content.ReadFromJsonAsync<RodadaBody>())!;

        // The menu moves on: Chopp is dearer, Porcao is "acabou". The repeat charges the price of NOW and reports the porcao instead of dropping it silently.
        await PutProductAsync(client, t.Dono, chopp, 1500, chopp.Versao);
        await PutProductAsync(client, t.Dono, porcao, 2000, porcao.Versao, indisponivel: true);
        var rodadaId = NewRoundId();
        using var response = await RepeatAsync(client, t.Garcom, comanda.Id, original.ComandaVersao, rodadaId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var repetida = (await response.Content.ReadFromJsonAsync<RepetidaBody>())!;
        Assert.Equal((Guid.Parse(rodadaId), 3), (repetida.Rodada.Id, repetida.Rodada.ComandaVersao));
        var item = Assert.Single(repetida.Rodada.Itens);
        Assert.Equal(("Chopp", 1500L, 2, 3000L), (item.ProdutoNome, item.PrecoCentavos, item.Quantidade, item.TotalCentavos));
        Assert.Equal([("Porcao", 1, "produto-inativo")], repetida.Ignorados.Select(i => (i.ProdutoNome, i.Quantidade, i.Motivo)).ToArray());
        var itens = await ItemsAsync(client, t.Garcom, comanda.Id);
        Assert.Equal(2000L + 2000L + 3000L, itens.ConsumoCentavos);
        Assert.Equal("sem colarinho", itens.Itens.Single(i => i.RodadaId == Guid.Parse(rodadaId)).Observacao);
        Assert.Equal(3, await VersionOfAsync(comanda.Id));

        // Same audit action and event name as any round, plus where it came from; the favorites feed counts the second launch.
        var audit = await QueryAsync(OrgA, UnitA, "SELECT objeto_id, depois->>'repeticaoDe', depois->>'valorCentavos' FROM auditoria_registros WHERE acao = 'comanda:lancar-rodada' ORDER BY ocorrido_em", r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        Assert.Equal([(original.Id.ToString("D"), null, "4000"), (rodadaId, original.Id.ToString("D"), "3000")], audit);
        Assert.Equal(2L, await CountAsync("outbox_eventos WHERE tipo = 'comanda.rodada-enviada'"));
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, $"SELECT lancamentos FROM produtos_frequencia_garcom WHERE garcom_id = '{Garcom:D}' AND produto_id = '{chopp.Id:D}'", r => r.GetInt64(0))).Single());

        // A resend of the same id answers the stored round (200) and writes nothing, even with the version the client still holds.
        using var again = await RepeatAsync(client, t.Garcom, comanda.Id, original.ComandaVersao, rodadaId);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var stored = (await again.Content.ReadFromJsonAsync<RepetidaBody>())!;
        Assert.Equal((repetida.Rodada.Id, 3, 0), (stored.Rodada.Id, stored.Rodada.ComandaVersao, stored.Ignorados.Count));
        Assert.Equal((2L, 3, 2L), (await CountAsync("rodadas"), await VersionOfAsync(comanda.Id), await CountAsync("auditoria_registros WHERE acao = 'comanda:lancar-rodada'")));

        // A stale version is a 409 with the current one and changes nothing; the next repeat repeats THIS round (the latest one that stands).
        await AssertRepeatRefusedAsync(await RepeatAsync(client, t.Garcom, comanda.Id, 1), HttpStatusCode.Conflict, "versao-desatualizada", 3);
        var terceira = await RepeatOkAsync(client, t.Garcom, comanda.Id, 3);
        Assert.Equal([("Chopp", 1500L, 2)], terceira.Rodada.Itens.Select(i => (i.ProdutoNome, i.PrecoCentavos, i.Quantidade)).ToArray());
        Assert.Empty(terceira.Ignorados);
        Assert.Equal((3L, 4), (await CountAsync("rodadas"), await VersionOfAsync(comanda.Id)));
    }

    [Fact]
    public async Task Repeating_with_nothing_left_to_repeat_or_nothing_launched_or_a_bad_request_writes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var porcao = await CreateProductAsync(client, t.Dono, "PORCAO", "Porcao", 2000);
        var comanda = await OpenAsync(client, t.Garcom);

        await AssertRepeatRefusedAsync(await RepeatAsync(client, t.Garcom, comanda.Id, 1), HttpStatusCode.Conflict, "sem-rodada-anterior");

        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, 1, chopp.Id);
        using var semId = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas/repetir", t.Garcom, new { rodadaId = Guid.Empty, versao = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, semId.StatusCode);
        using var semVersao = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas/repetir", t.Garcom, new { rodadaId = NewRoundId(), versao = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, semVersao.StatusCode);

        // The only item of the previous round was cancelled: there is no live round to repeat.
        using var cancel = await CancelAsync(client, t.Garcom, comanda.Id, rodada.Itens[0].Id, rodada.ComandaVersao, "cliente-desistiu");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await AssertRepeatRefusedAsync(await RepeatAsync(client, t.Garcom, comanda.Id, 3), HttpStatusCode.Conflict, "sem-rodada-anterior");

        // Every item of the previous round is now off: nothing is launched, the reasons come back.
        var segunda = await LaunchOkAsync(client, t.Garcom, comanda.Id, 3, porcao.Id, quantidade: 3);
        await PutProductAsync(client, t.Dono, porcao, 2000, porcao.Versao, ativo: false);
        using var nada = await RepeatAsync(client, t.Garcom, comanda.Id, segunda.ComandaVersao);
        Assert.Equal(HttpStatusCode.Conflict, nada.StatusCode);
        var erro = (await nada.Content.ReadFromJsonAsync<RepeticaoErroBody>())!;
        Assert.Equal("nada-para-repetir", erro.Codigo);
        Assert.Equal([("Porcao", 3, "produto-inativo")], erro.Ignorados.Select(i => (i.ProdutoNome, i.Quantidade, i.Motivo)).ToArray());
        Assert.Equal((2L, 2L, 4, 2L), (await CountAsync("rodadas"), await CountAsync("itens_comanda"), await VersionOfAsync(comanda.Id), await CountAsync("auditoria_registros WHERE acao = 'comanda:lancar-rodada'")));

        // A closed comanda takes no new round, repeated or not.
        await RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'fechada', fechada_em = now() WHERE id = '{comanda.Id:D}'");
        await AssertRepeatRefusedAsync(await RepeatAsync(client, t.Garcom, comanda.Id, 4), HttpStatusCode.Conflict, "comanda-nao-aberta");
        Assert.Equal(2L, await CountAsync("rodadas"));
    }

    [Fact]
    public async Task Repeating_a_round_with_add_ons_repeats_the_choices_at_todays_price_and_reports_an_item_whose_add_on_was_turned_off()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var burger = await CreateProductAsync(client, t.Dono, "HAMB", "X-Burger", 2500);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var extras = await CreateGroupAsync(client, t.Dono, "Extras", 0, 1, ("Bacon", 500), ("Queijo", 300));
        var ponto = await CreateGroupAsync(client, t.Dono, "Ponto", 1, 1, ("Mal", 0), ("Bem", 0));
        await LinkGroupsAsync(client, t.Dono, burger.Id, extras.Id, ponto.Id);
        var comanda = await OpenAsync(client, t.Garcom);
        using var enviada = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new
        {
            rodadaId = NewRoundId(),
            versao = comanda.Versao,
            itens = new object[] { new { produtoId = burger.Id, quantidade = 1, opcaoIds = new[] { extras.Opcoes[0].Id, ponto.Opcoes[1].Id } }, new { produtoId = chopp.Id, quantidade = 2 } },
        });
        Assert.Equal(HttpStatusCode.Created, enviada.StatusCode);
        var original = (await enviada.Content.ReadFromJsonAsync<RodadaComOpcoesBody>())!;

        // Bacon gets dearer: the repeat carries the same choices, frozen at the new price.
        using var change = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{extras.Id:D}", t.Dono, new { nome = "Extras", minimo = 0, maximo = 1, ativo = true, versao = 1, opcoes = new object[] { new { id = extras.Opcoes[0].Id, nome = "Bacon", precoCentavos = 700 }, new { id = extras.Opcoes[1].Id, nome = "Queijo", precoCentavos = 300 } } });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var repetida = await RepeatOkAsync(client, t.Garcom, comanda.Id, original.ComandaVersao);
        var hamburguer = repetida.Rodada.Itens.Single(i => i.ProdutoNome == "X-Burger");
        Assert.Equal([("Bacon", 700L), ("Bem", 0L)], hamburguer.Opcoes.OrderByDescending(o => o.PrecoCentavos).Select(o => (o.Nome, o.PrecoCentavos)).ToArray());
        Assert.Equal(3200L, hamburguer.TotalCentavos);
        Assert.Empty(repetida.Ignorados);
        Assert.Equal(4L, await CountAsync("item_opcoes"));

        // Bacon is turned off: the burger is reported (not silently dropped, not launched without its add-on); the chopp still goes.
        using var off = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{extras.Id:D}", t.Dono, new { nome = "Extras", minimo = 0, maximo = 1, ativo = true, versao = 2, opcoes = new object[] { new { id = extras.Opcoes[0].Id, nome = "Bacon", precoCentavos = 700, ativa = false }, new { id = extras.Opcoes[1].Id, nome = "Queijo", precoCentavos = 300 } } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var terceira = await RepeatOkAsync(client, t.Garcom, comanda.Id, repetida.Rodada.ComandaVersao);
        Assert.Equal(["Chopp"], terceira.Rodada.Itens.Select(i => i.ProdutoNome).ToArray());
        Assert.Equal([("X-Burger", "opcao-inativa")], terceira.Ignorados.Select(i => (i.ProdutoNome, i.Motivo)).ToArray());
    }

    [Fact]
    public async Task Repeating_is_denied_without_the_permission_and_to_other_people_and_other_organizations_and_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, 1, chopp.Id);

        foreach (var token in new[] { t.Mudo, t.Supervisor })
        {
            using var denied = await RepeatAsync(client, token, comanda.Id, rodada.ComandaVersao);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Empty(await denied.Content.ReadAsByteArrayAsync());
        }

        using var anonymous = await RepeatAsync(client, null, comanda.Id, rodada.ComandaVersao);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // Another waiter does not see this comanda (404 like a missing one); another organization neither.
        using var outro = await RepeatAsync(client, t.Garcom2, comanda.Id, rodada.ComandaVersao);
        using var deB = await RepeatAsync(client, t.DonoB, comanda.Id, rodada.ComandaVersao);
        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound], [outro.StatusCode, deB.StatusCode]);
        Assert.Equal((1L, 2), (await CountAsync("rodadas"), await VersionOfAsync(comanda.Id)));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'comanda:lancar-rodada'"));
    }

    [Fact]
    public async Task Launching_counts_the_product_for_the_waiter_who_launched_it_and_for_nobody_else()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var porcao = await CreateProductAsync(client, t.Dono, "PORCAO", "Porcao", 2000);
        var doGarcom = await OpenAsync(client, t.Garcom);
        var doOutro = await OpenAsync(client, t.Garcom2);

        var r1 = await LaunchOkAsync(client, t.Garcom, doGarcom.Id, 1, chopp.Id);
        _ = await LaunchOkAsync(client, t.Garcom, doGarcom.Id, r1.ComandaVersao, chopp.Id);
        _ = await LaunchOkAsync(client, t.Garcom2, doOutro.Id, 1, porcao.Id);

        var linhas = await QueryAsync(OrgA, UnitA, "SELECT garcom_id, produto_id, lancamentos FROM produtos_frequencia_garcom ORDER BY lancamentos DESC, garcom_id", r => (r.GetGuid(0), r.GetGuid(1), r.GetInt64(2)));
        Assert.Equal([(Garcom, chopp.Id, 2L), (Garcom2, porcao.Id, 1L)], linhas);
        Assert.Equal(3L, (await QueryAsync(OrgA, UnitA, "SELECT sum(lancamentos)::bigint FROM produtos_frequencia", r => r.GetInt64(0))).Single());

        // The rollback of a refused launch leaves the counters alone.
        _ = await LaunchAsync(client, t.Garcom, doGarcom.Id, 1, chopp.Id);
        Assert.Equal(2L, (await QueryAsync(OrgA, UnitA, $"SELECT lancamentos FROM produtos_frequencia_garcom WHERE garcom_id = '{Garcom:D}'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Last_time_at_a_table_returns_the_latest_closed_comanda_of_that_table_without_cancelled_items_and_only_what_the_caller_may_see()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await CreateTableAsync(client, t.Dono, "7");
        var outraMesa = await CreateTableAsync(client, t.Dono, "8");
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var porcao = await CreateProductAsync(client, t.Dono, "PORCAO", "Porcao", 2000);

        // Nothing closed yet: an empty answer, never an error.
        var vazia = await UltimaVezAsync(client, t.Garcom, mesa.Id);
        Assert.Equal((null, 0), (vazia.ComandaId, vazia.Itens.Count));

        // An older closed comanda, then the latest one (chopp x2 stands, the porcao was cancelled), and one still open on the same table.
        var antiga = await OpenAsync(client, t.Garcom, new { mesaId = mesa.Id });
        _ = await LaunchOkAsync(client, t.Garcom, antiga.Id, 1, porcao.Id);
        var recente = await OpenAsync(client, t.Garcom, new { mesaId = mesa.Id });
        using var round = await SendAsync(client, HttpMethod.Post, $"/comandas/{recente.Id:D}/rodadas", t.Garcom, new { rodadaId = NewRoundId(), versao = 1, itens = new object[] { new { produtoId = chopp.Id, quantidade = 2, observacao = "gelada" }, new { produtoId = porcao.Id, quantidade = 1 } } });
        var enviada = (await round.Content.ReadFromJsonAsync<RodadaBody>())!;
        using var cancel = await CancelAsync(client, t.Garcom, recente.Id, enviada.Itens[1].Id, enviada.ComandaVersao, "cliente-desistiu");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var aberta = await OpenAsync(client, t.Garcom, new { mesaId = mesa.Id });
        _ = await LaunchOkAsync(client, t.Garcom, aberta.Id, 1, porcao.Id);
        await RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'fechada', fechada_em = TIMESTAMPTZ '2026-11-05 20:00:00+00' WHERE id = '{antiga.Id:D}'");
        await RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'fechada', fechada_em = TIMESTAMPTZ '2026-11-05 22:00:00+00' WHERE id = '{recente.Id:D}'");

        var ultima = await UltimaVezAsync(client, t.Garcom, mesa.Id);
        Assert.Equal((recente.Id, recente.Numero, new DateTimeOffset(2026, 11, 5, 22, 0, 0, TimeSpan.Zero)), (ultima.ComandaId, ultima.Numero, ultima.FechadaEm));
        Assert.Equal(recente.NumeroTexto, ultima.NumeroTexto);
        var item = Assert.Single(ultima.Itens);
        Assert.Equal(("Chopp", 2, 2000L), (item.ProdutoNome, item.Quantidade, item.TotalCentavos));

        // Another table has no history; the owner (sees everyone's) gets the same card; another waiter without that permission sees none of the first waiter's comandas.
        Assert.Empty((await UltimaVezAsync(client, t.Garcom, outraMesa.Id)).Itens);
        Assert.Equal(recente.Id, (await UltimaVezAsync(client, t.Dono, mesa.Id)).ComandaId);
        var doOutro = await UltimaVezAsync(client, t.Garcom2, mesa.Id);
        Assert.Equal((null, 0), (doOutro.ComandaId, doOutro.Itens.Count));
    }

    [Fact]
    public async Task Last_time_at_a_table_is_denied_without_the_permission_and_never_crosses_units_or_organizations()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await CreateTableAsync(client, t.Dono, "7");
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom, new { mesaId = mesa.Id });
        _ = await LaunchOkAsync(client, t.Garcom, comanda.Id, 1, chopp.Id);
        await RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'fechada', fechada_em = now() WHERE id = '{comanda.Id:D}'");

        // A table of the second unit of the same organization is not a table of the active unit: 404, like a missing one.
        var mesaDaOutraUnidade = Guid.CreateVersion7();
        await RunSqlAsync(OrgA, UnitA2, $"INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, ativa, criada_em) VALUES ('{mesaDaOutraUnidade:D}', '{OrgA:D}', '{UnitA2:D}', 'Praia 1', true, now())");

        using var mudo = await SendAsync(client, HttpMethod.Get, $"/mesas/{mesa.Id:D}/ultima-vez", t.Mudo);
        Assert.Equal(HttpStatusCode.Forbidden, mudo.StatusCode);
        Assert.Empty(await mudo.Content.ReadAsByteArrayAsync());
        using var anonymous = await SendAsync(client, HttpMethod.Get, $"/mesas/{mesa.Id:D}/ultima-vez", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var (token, id) in new[] { (t.DonoB, mesa.Id), (t.Dono, mesaDaOutraUnidade), (t.Dono, Guid.NewGuid()) })
        {
            using var missing = await SendAsync(client, HttpMethod.Get, $"/mesas/{id:D}/ultima-vez", token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // The query names the unit and the table, closed comandas only, newest first, one row, and leaves the organization to RLS.
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new MesasDbContext(tx);
        var sql = PostgresComandasStore.UltimaFechadaDaMesa(context, UnitA, mesa.Id, Garcom).ToQueryString();
        Assert.Contains("c.unidade_id = @", sql, StringComparison.Ordinal);
        Assert.Contains("c.mesa_id = @", sql, StringComparison.Ordinal);
        Assert.Contains("c.estado = 'fechada'", sql, StringComparison.Ordinal);
        Assert.Contains("c.garcom_id = @", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY c.fechada_em DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", sql, StringComparison.Ordinal);
    }

    private static async Task<UltimaVezBody> UltimaVezAsync(HttpClient client, string token, Guid mesa)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/mesas/{mesa:D}/ultima-vez", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UltimaVezBody>())!;
    }
}
