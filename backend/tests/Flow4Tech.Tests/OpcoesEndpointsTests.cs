using System.Net;
using System.Net.Http.Json;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0050: option groups and add-ons through the real authenticated routes (same fixture as the comandas: real PostgreSQL, application credential with RLS, real sessions). Covers managing groups
/// (audit + outbox in one unit, optimistic version, denial, other organization), launching with chosen option ids (price recomputed on the server and frozen, the bill includes them, the whole round
/// refused on one bad choice, idempotent repeat) and the usage counts that feed "most used add-ons".
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record OpcaoBody(Guid Id, string Nome, long PrecoCentavos, bool Ativa);

    private sealed record GrupoBody(Guid Id, string Nome, int Minimo, int Maximo, bool Obrigatorio, bool Ativo, int Versao, List<OpcaoBody> Opcoes);

    private sealed record OpcaoFrequenteBody(Guid GrupoId, string GrupoNome, OpcaoBody Opcao, long Lancamentos);

    private sealed record OpcaoDoItemBody(Guid OpcaoId, Guid GrupoId, string Nome, long PrecoCentavos);

    private sealed record ItemComOpcoesBody(Guid Id, string ProdutoNome, long PrecoCentavos, int Quantidade, List<OpcaoDoItemBody> Opcoes, long TotalCentavos);

    private sealed record RodadaComOpcoesBody(Guid Id, int ComandaVersao, List<ItemComOpcoesBody> Itens);

    private sealed record ItensComOpcoesBody(List<ItemComOpcoesBody> Itens, long ConsumoCentavos);

    private static async Task<GrupoBody> CreateGroupAsync(HttpClient client, string token, string nome, int minimo, int maximo, params (string Nome, long Preco)[] opcoes)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/cardapio/grupos-opcao", token, new { nome, minimo, maximo, opcoes = opcoes.Select(o => new { nome = o.Nome, precoCentavos = o.Preco }).ToArray() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GrupoBody>())!;
    }

    private static async Task LinkGroupsAsync(HttpClient client, string token, Guid produto, params Guid[] grupos)
    {
        using var response = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto:D}/grupos-opcao", token, new { grupoIds = grupos });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> LaunchWithOptionsAsync(HttpClient client, string token, Guid comanda, int versao, Guid produto, int quantidade, string? rodadaId = null, params Guid[] opcoes) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/rodadas", token, new { rodadaId = rodadaId ?? NewRoundId(), versao, itens = new[] { new { produtoId = produto, quantidade, opcaoIds = opcoes } } });

    [Fact]
    public async Task Managing_groups_stores_options_audits_and_publishes_and_a_stale_version_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var grupo = await CreateGroupAsync(client, t.Dono, "  Adicionais ", 0, 2, ("Bacon", 500), ("Sem cebola", 0));
        Assert.Equal(("Adicionais", 0, 2, false, true, 1), (grupo.Nome, grupo.Minimo, grupo.Maximo, grupo.Obrigatorio, grupo.Ativo, grupo.Versao));
        Assert.Equal([("Bacon", 500L), ("Sem cebola", 0L)], grupo.Opcoes.Select(o => (o.Nome, o.PrecoCentavos)).ToArray());

        // Renames one option, drops the other from the list (turned off, never deleted) and adds a new one.
        var bacon = grupo.Opcoes[0];
        using var update = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{grupo.Id:D}", t.Dono, new { nome = "Adicionais", minimo = 0, maximo = 3, ativo = true, versao = 1, opcoes = new object[] { new { id = bacon.Id, nome = "Bacon crocante", precoCentavos = 600 }, new { nome = "Queijo", precoCentavos = 300 } } });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var novo = (await update.Content.ReadFromJsonAsync<GrupoBody>())!;
        Assert.Equal((2, 3), (novo.Versao, novo.Maximo));
        Assert.Equal([("Bacon crocante", 600L, true), ("Queijo", 300L, true), ("Sem cebola", 0L, false)], novo.Opcoes.Select(o => (o.Nome, o.PrecoCentavos, o.Ativa)).ToArray());
        Assert.Equal(bacon.Id, novo.Opcoes[0].Id);

        using var stale = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{grupo.Id:D}", t.Dono, new { nome = "Outro", minimo = 0, maximo = 1, ativo = true, versao = 1 });
        await AssertCardapioErrorAsync(stale, HttpStatusCode.Conflict, "versao-desatualizada");
        Assert.Equal("Adicionais", (await QueryAsync(OrgA, UnitA, $"SELECT nome FROM grupos_opcao WHERE id = '{grupo.Id:D}'", r => r.GetString(0))).Single());

        var audit = await QueryAsync(OrgA, UnitA, "SELECT acao, usuario_id, resultado FROM auditoria_registros WHERE acao LIKE 'grupo-opcao:%' ORDER BY acao", r => (r.GetString(0), r.GetGuid(1), r.GetString(2)));
        Assert.Equal([("grupo-opcao:alterar", DonoA, "sucesso"), ("grupo-opcao:criar", DonoA, "sucesso")], audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'cardapio.grupo-opcao-criado' AND payload->>'GrupoId' = '{grupo.Id:D}'", r => r.GetInt32(0)));
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'cardapio.grupo-opcao-alterado' AND payload->>'GrupoId' = '{grupo.Id:D}'", r => r.GetInt32(0)));
    }

    [Theory]
    [InlineData("", 0, 1)]
    [InlineData("Grupo", 2, 1)]
    [InlineData("Grupo", -1, 1)]
    [InlineData("Grupo", 0, 0)]
    public async Task A_group_with_a_blank_name_or_impossible_limits_is_refused(string nome, int minimo, int maximo)
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var response = await SendAsync(client, HttpMethod.Post, "/cardapio/grupos-opcao", t.Dono, new { nome, minimo, maximo });

        await AssertCardapioErrorAsync(response, HttpStatusCode.BadRequest, "invalido");
        Assert.Empty(await QueryAsync(OrgA, UnitA, "SELECT 1 FROM grupos_opcao", r => r.GetInt32(0)));
    }

    [Fact]
    public async Task A_group_with_a_negative_price_or_a_foreign_option_id_is_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var grupo = await CreateGroupAsync(client, t.Dono, "Adicionais", 0, 1, ("Bacon", 500));

        using var negativo = await SendAsync(client, HttpMethod.Post, "/cardapio/grupos-opcao", t.Dono, new { nome = "Outro", minimo = 0, maximo = 1, opcoes = new[] { new { nome = "X", precoCentavos = -1 } } });
        using var alheio = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{grupo.Id:D}", t.Dono, new { nome = "Adicionais", minimo = 0, maximo = 1, ativo = true, versao = 1, opcoes = new[] { new { id = Guid.CreateVersion7(), nome = "X", precoCentavos = 1 } } });

        await AssertCardapioErrorAsync(negativo, HttpStatusCode.BadRequest, "invalido");
        await AssertCardapioErrorAsync(alheio, HttpStatusCode.BadRequest, "invalido");
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM opcoes", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Managing_groups_needs_the_manage_permission_and_another_organization_sees_and_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "HAMB", "Hamburguer", 2500);
        var grupo = await CreateGroupAsync(client, t.Dono, "Adicionais", 0, 1, ("Bacon", 500));
        await LinkGroupsAsync(client, t.Dono, produto.Id, grupo.Id);

        foreach (var token in new[] { t.Garcom, t.Mudo })
        {
            using var create = await SendAsync(client, HttpMethod.Post, "/cardapio/grupos-opcao", token, new { nome = "X", minimo = 0, maximo = 1 });
            using var update = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{grupo.Id:D}", token, new { nome = "X", minimo = 0, maximo = 1, ativo = true, versao = 1 });
            using var link = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id:D}/grupos-opcao", token, new { grupoIds = Array.Empty<Guid>() });
            using var list = await SendAsync(client, HttpMethod.Get, "/cardapio/grupos-opcao", token);
            Assert.All(new[] { create, update, link, list }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
            Assert.Empty(await ReadBodyAsync(create));
        }

        using var anonymous = await SendAsync(client, HttpMethod.Get, "/cardapio/grupos-opcao", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var mudoReads = await SendAsync(client, HttpMethod.Get, $"/cardapio/produtos/{produto.Id:D}/grupos-opcao", t.Mudo);
        Assert.Equal(HttpStatusCode.Forbidden, mudoReads.StatusCode);

        // The other organization: its owner cannot read, change or link what is not theirs (404 like a missing id) and its own list is empty.
        using var foreignList = await SendAsync(client, HttpMethod.Get, "/cardapio/grupos-opcao", t.DonoB);
        Assert.Empty((await foreignList.Content.ReadFromJsonAsync<List<GrupoBody>>())!);
        using var foreignRead = await SendAsync(client, HttpMethod.Get, $"/cardapio/produtos/{produto.Id:D}/grupos-opcao", t.DonoB);
        using var foreignUpdate = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{grupo.Id:D}", t.DonoB, new { nome = "Invadido", minimo = 0, maximo = 1, ativo = true, versao = 1 });
        using var foreignLink = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{produto.Id:D}/grupos-opcao", t.DonoB, new { grupoIds = new[] { grupo.Id } });
        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound], [foreignRead.StatusCode, foreignUpdate.StatusCode, foreignLink.StatusCode]);
        Assert.Equal("Adicionais", (await QueryAsync(OrgA, UnitA, $"SELECT nome FROM grupos_opcao WHERE id = '{grupo.Id:D}'", r => r.GetString(0))).Single());
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static async Task AssertCardapioErrorAsync(HttpResponseMessage response, HttpStatusCode status, string codigo)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(codigo, (await response.Content.ReadFromJsonAsync<Erro>())!.Codigo);
    }

    [Fact]
    public async Task Launching_with_options_recomputes_the_price_on_the_server_freezes_name_and_price_and_the_bill_includes_them()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var burger = await CreateProductAsync(client, t.Dono, "HAMB", "X-Burger", 2500);
        var adicionais = await CreateGroupAsync(client, t.Dono, "Adicionais", 0, 2, ("Bacon", 500), ("Queijo extra", 300));
        var ponto = await CreateGroupAsync(client, t.Dono, "Ponto", 1, 1, ("Mal passado", 0), ("Ao ponto", 0));
        await LinkGroupsAsync(client, t.Dono, burger.Id, adicionais.Id, ponto.Id);
        var comanda = await OpenAsync(client, t.Garcom);

        using var response = await LaunchWithOptionsAsync(client, t.Garcom, comanda.Id, comanda.Versao, burger.Id, 2, null, adicionais.Opcoes[0].Id, ponto.Opcoes[1].Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var rodada = (await response.Content.ReadFromJsonAsync<RodadaComOpcoesBody>())!;
        var item = Assert.Single(rodada.Itens);
        Assert.Equal((2500L, 2, 6000L), (item.PrecoCentavos, item.Quantidade, item.TotalCentavos));
        Assert.Equal([("Bacon", 500L), ("Ao ponto", 0L)], item.Opcoes.OrderByDescending(o => o.PrecoCentavos).Select(o => (o.Nome, o.PrecoCentavos)).ToArray());

        // The group changes afterwards (price and name): the comanda keeps what was frozen.
        using var change = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{adicionais.Id:D}", t.Dono, new { nome = "Extras", minimo = 0, maximo = 2, ativo = true, versao = 1, opcoes = new object[] { new { id = adicionais.Opcoes[0].Id, nome = "Bacon premium", precoCentavos = 900 } } });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        using var itens = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda.Id:D}/itens", t.Garcom);
        var lista = (await itens.Content.ReadFromJsonAsync<ItensComOpcoesBody>())!;
        Assert.Equal(6000L, lista.ConsumoCentavos);
        Assert.Contains(lista.Itens.Single().Opcoes, o => o is { Nome: "Bacon", PrecoCentavos: 500 });

        // 10% service fee on top of the consumption with options.
        var conta = await BillAsync(client, t.Garcom, comanda.Id);
        Assert.Equal((6000L, 600L, 6600L), (conta.ConsumoCentavos, conta.TaxaServicoCentavos, conta.TotalCentavos));

        var congeladas = await QueryAsync(OrgA, UnitA, $"SELECT nome, preco_centavos FROM item_opcoes WHERE item_id = '{item.Id:D}' ORDER BY preco_centavos DESC", r => (r.GetString(0), r.GetInt64(1)));
        Assert.Equal([("Bacon", 500L), ("Ao ponto", 0L)], congeladas);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT depois->>'valorCentavos' FROM auditoria_registros WHERE acao = 'comanda:lancar-rodada'", r => r.GetString(0)));
        Assert.Equal("6000", audit);
    }

    [Fact]
    public async Task One_bad_choice_refuses_the_whole_round_and_nothing_is_written()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var burger = await CreateProductAsync(client, t.Dono, "HAMB", "X-Burger", 2500);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1200);
        var adicionais = await CreateGroupAsync(client, t.Dono, "Adicionais", 0, 1, ("Bacon", 500), ("Queijo", 300));
        var ponto = await CreateGroupAsync(client, t.Dono, "Ponto", 1, 1, ("Mal", 0), ("Bem", 0));
        await LinkGroupsAsync(client, t.Dono, burger.Id, adicionais.Id, ponto.Id);
        var comanda = await OpenAsync(client, t.Garcom);

        async Task AssertRefusedAsync(Guid produto, Guid[] opcoes, HttpStatusCode status, string codigo)
        {
            // The good item (chopp) travels in the same round: the refusal must take it down too.
            using var response = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new
            {
                rodadaId = NewRoundId(),
                versao = comanda.Versao,
                itens = new object[] { new { produtoId = chopp.Id, quantidade = 1 }, new { produtoId = produto, quantidade = 1, opcaoIds = opcoes } },
            });
            await AssertCardapioErrorAsync(response, status, codigo);
        }

        await AssertRefusedAsync(burger.Id, [adicionais.Opcoes[0].Id], HttpStatusCode.BadRequest, "opcao-invalida"); // required group "Ponto" missing
        await AssertRefusedAsync(burger.Id, [ponto.Opcoes[0].Id, ponto.Opcoes[1].Id], HttpStatusCode.BadRequest, "opcao-invalida"); // above the maximum
        await AssertRefusedAsync(burger.Id, [ponto.Opcoes[0].Id, adicionais.Opcoes[0].Id, adicionais.Opcoes[1].Id], HttpStatusCode.BadRequest, "opcao-invalida"); // above the maximum of another group
        await AssertRefusedAsync(burger.Id, [ponto.Opcoes[0].Id, ponto.Opcoes[0].Id], HttpStatusCode.BadRequest, "opcao-invalida"); // repeated
        await AssertRefusedAsync(burger.Id, [ponto.Opcoes[0].Id, Guid.CreateVersion7()], HttpStatusCode.BadRequest, "opcao-invalida"); // unknown
        await AssertRefusedAsync(chopp.Id, [adicionais.Opcoes[0].Id], HttpStatusCode.BadRequest, "opcao-invalida"); // option of a group the product does not offer

        // Turning an option off: refused as inactive (409). Turning a whole group off: its options are refused too.
        using var off = await SendAsync(client, HttpMethod.Put, $"/cardapio/grupos-opcao/{adicionais.Id:D}", t.Dono, new { nome = "Adicionais", minimo = 0, maximo = 1, ativo = true, versao = 1, opcoes = new object[] { new { id = adicionais.Opcoes[0].Id, nome = "Bacon", precoCentavos = 500, ativa = false }, new { id = adicionais.Opcoes[1].Id, nome = "Queijo", precoCentavos = 300 } } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        await AssertRefusedAsync(burger.Id, [ponto.Opcoes[0].Id, adicionais.Opcoes[0].Id], HttpStatusCode.Conflict, "opcao-inativa");

        Assert.Equal(comanda.Versao, await VersionOfAsync(comanda.Id));
        Assert.Equal((0L, 0L, 0L, 0L), (await CountRowsAsync("rodadas"), await CountRowsAsync("itens_comanda"), await CountRowsAsync("item_opcoes"), await CountRowsAsync("opcoes_frequencia")));
        Assert.Equal(0L, await CountRowsAsync("auditoria_registros WHERE acao = 'comanda:lancar-rodada'"));
        Assert.Equal(0L, await CountRowsAsync("outbox_eventos WHERE tipo = 'comanda.rodada-enviada'"));
    }

    [Fact]
    public async Task A_required_group_is_enforced_even_when_the_client_sends_no_options_and_a_product_without_groups_takes_none()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var burger = await CreateProductAsync(client, t.Dono, "HAMB", "X-Burger", 2500);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1200);
        var ponto = await CreateGroupAsync(client, t.Dono, "Ponto", 1, 1, ("Mal", 0));
        await LinkGroupsAsync(client, t.Dono, burger.Id, ponto.Id);
        var comanda = await OpenAsync(client, t.Garcom);

        using var semOpcoes = await LaunchAsync(client, t.Garcom, comanda.Id, comanda.Versao, burger.Id);
        using var comOpcaoSobrando = await LaunchWithOptionsAsync(client, t.Garcom, comanda.Id, comanda.Versao, chopp.Id, 1, null, ponto.Opcoes[0].Id);
        using var semGrupos = await LaunchAsync(client, t.Garcom, comanda.Id, comanda.Versao, chopp.Id);

        await AssertCardapioErrorAsync(semOpcoes, HttpStatusCode.BadRequest, "opcao-invalida");
        await AssertCardapioErrorAsync(comOpcaoSobrando, HttpStatusCode.BadRequest, "opcao-invalida");
        Assert.Equal(HttpStatusCode.Created, semGrupos.StatusCode);
    }

    [Fact]
    public async Task Repeating_a_round_with_options_answers_the_same_frozen_options_and_counts_usage_once()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var burger = await CreateProductAsync(client, t.Dono, "HAMB", "X-Burger", 2500);
        var adicionais = await CreateGroupAsync(client, t.Dono, "Adicionais", 0, 2, ("Bacon", 500));
        await LinkGroupsAsync(client, t.Dono, burger.Id, adicionais.Id);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodadaId = NewRoundId();

        using var first = await LaunchWithOptionsAsync(client, t.Garcom, comanda.Id, comanda.Versao, burger.Id, 1, rodadaId, adicionais.Opcoes[0].Id);
        using var again = await LaunchWithOptionsAsync(client, t.Garcom, comanda.Id, comanda.Versao, burger.Id, 1, rodadaId, adicionais.Opcoes[0].Id);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, again.StatusCode));
        var a = (await first.Content.ReadFromJsonAsync<RodadaComOpcoesBody>())!;
        var b = (await again.Content.ReadFromJsonAsync<RodadaComOpcoesBody>())!;
        Assert.Equal(a.Itens.Single().Opcoes, b.Itens.Single().Opcoes);
        Assert.Equal(3000L, b.Itens.Single().TotalCentavos);
        Assert.Equal(1L, await CountRowsAsync("item_opcoes"));
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT lancamentos FROM opcoes_frequencia", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Option_usage_feeds_the_most_used_add_ons_of_the_product_in_the_active_unit()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var burger = await CreateProductAsync(client, t.Dono, "HAMB", "X-Burger", 2500);
        var adicionais = await CreateGroupAsync(client, t.Dono, "Adicionais", 0, 3, ("Bacon", 500), ("Queijo", 300), ("Ovo", 200));
        await LinkGroupsAsync(client, t.Dono, burger.Id, adicionais.Id);
        var comanda = await OpenAsync(client, t.Garcom);
        var (bacon, queijo, ovo) = (adicionais.Opcoes[0].Id, adicionais.Opcoes[1].Id, adicionais.Opcoes[2].Id);

        using var empty = await SendAsync(client, HttpMethod.Get, $"/cardapio/produtos/{burger.Id:D}/opcoes-frequentes", t.Dono);
        Assert.Empty((await empty.Content.ReadFromJsonAsync<List<OpcaoFrequenteBody>>())!);

        var versao = comanda.Versao;
        foreach (var escolha in new[] { new[] { queijo }, new[] { queijo, bacon }, new[] { queijo } })
        {
            using var response = await LaunchWithOptionsAsync(client, t.Garcom, comanda.Id, versao, burger.Id, 1, null, escolha);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            versao = (await response.Content.ReadFromJsonAsync<RodadaComOpcoesBody>())!.ComandaVersao;
        }

        using var ranked = await SendAsync(client, HttpMethod.Get, $"/cardapio/produtos/{burger.Id:D}/opcoes-frequentes", t.Dono);
        var lista = (await ranked.Content.ReadFromJsonAsync<List<OpcaoFrequenteBody>>())!;
        Assert.Equal([("Queijo", 3L, "Adicionais"), ("Bacon", 1L, "Adicionais")], lista.Select(f => (f.Opcao.Nome, f.Lancamentos, f.GrupoNome)).ToArray());
        Assert.DoesNotContain(lista, f => f.Opcao.Id == ovo);

        // Counted per unit: the other unit of the organization has no history, and an unknown product is 404.
        Assert.Equal(3L, (await QueryAsync(OrgA, UnitA, $"SELECT lancamentos FROM opcoes_frequencia WHERE opcao_id = '{queijo:D}' AND unidade_id = '{UnitA:D}'", r => r.GetInt64(0))).Single());
        using var missing = await SendAsync(client, HttpMethod.Get, $"/cardapio/produtos/{Guid.CreateVersion7():D}/opcoes-frequentes", t.Dono);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var denied = await SendAsync(client, HttpMethod.Get, $"/cardapio/produtos/{burger.Id:D}/opcoes-frequentes", t.Mudo);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Linking_groups_to_a_product_keeps_the_order_replaces_the_set_audits_and_refuses_unknown_groups()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var burger = await CreateProductAsync(client, t.Dono, "HAMB", "X-Burger", 2500);
        var a = await CreateGroupAsync(client, t.Dono, "A", 0, 1, ("a1", 100));
        var b = await CreateGroupAsync(client, t.Dono, "B", 0, 1, ("b1", 100));

        await LinkGroupsAsync(client, t.Dono, burger.Id, b.Id, a.Id);
        using var read = await SendAsync(client, HttpMethod.Get, $"/cardapio/produtos/{burger.Id:D}/grupos-opcao", t.Dono);
        Assert.Equal(["B", "A"], (await read.Content.ReadFromJsonAsync<List<GrupoBody>>())!.Select(g => g.Nome).ToArray());

        using var unknown = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{burger.Id:D}/grupos-opcao", t.Dono, new { grupoIds = new[] { a.Id, Guid.CreateVersion7() } });
        using var duplicated = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{burger.Id:D}/grupos-opcao", t.Dono, new { grupoIds = new[] { a.Id, a.Id } });
        using var noProduct = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{Guid.CreateVersion7():D}/grupos-opcao", t.Dono, new { grupoIds = new[] { a.Id } });
        await AssertCardapioErrorAsync(unknown, HttpStatusCode.BadRequest, "invalido");
        await AssertCardapioErrorAsync(duplicated, HttpStatusCode.BadRequest, "invalido");
        Assert.Equal(HttpStatusCode.NotFound, noProduct.StatusCode);

        await LinkGroupsAsync(client, t.Dono, burger.Id, a.Id);
        using var after = await SendAsync(client, HttpMethod.Get, $"/cardapio/produtos/{burger.Id:D}/grupos-opcao", t.Dono);
        Assert.Equal(["A"], (await after.Content.ReadFromJsonAsync<List<GrupoBody>>())!.Select(g => g.Nome).ToArray());
        Assert.Equal(2L, await CountRowsAsync("auditoria_registros WHERE acao = 'produto:grupos-opcao-definir'"));
        Assert.Equal(2L, await CountRowsAsync("outbox_eventos WHERE tipo = 'cardapio.produto-grupos-opcao-definidos'"));
    }

    private async Task<long> CountRowsAsync(string from) => (await QueryAsync(OrgA, UnitA, $"SELECT count(*) FROM {from}", r => r.GetInt64(0))).Single();
}
