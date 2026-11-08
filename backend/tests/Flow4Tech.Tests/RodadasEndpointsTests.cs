using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Infrastructure;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// Dominio do salao, ticket 06: rounds and items through the real authenticated routes (same fixture as the comandas: real PostgreSQL, application credential with RLS, real sessions). Covers price and
/// name frozen at launch, idempotent repeats, the undo window on an injected skew clock, cancelling with a reason, optimistic versions (409 with the current one, changes nothing), true concurrency on
/// one comanda, visibility, isolation between organizations, denial, and rollback of version + rows + audit + outbox together.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    /// <summary>The real clock plus an offset a test can move forward: lets a test pass the 8 s undo window without waiting, and keeps the sessions (same clock) valid.</summary>
    private sealed class SkewClock : IClock
    {
        public TimeSpan Skew { get; set; }

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Skew;
    }

    private sealed record ItemBody(Guid Id, Guid RodadaId, int Posicao, Guid ProdutoId, string ProdutoNome, long PrecoCentavos, int Quantidade, string? Observacao, string Estado, string? MotivoCancelamento, Guid? CanceladoPor, DateTimeOffset? CanceladoEm);

    private sealed record RodadaBody(Guid Id, Guid ComandaId, DateTimeOffset EnviadaEm, DateTimeOffset? DesfeitaEm, int ComandaVersao, List<ItemBody> Itens);

    private sealed record ItemCanceladoBody(ItemBody Item, int ComandaVersao);

    private sealed record ItensBody(List<ItemBody> Itens, long ConsumoCentavos);

    private sealed record ProdutoBody(Guid Id, string Nome, long PrecoCentavos, bool Ativo, int Versao, Guid CategoriaId, string Codigo);

    private sealed record CategoriaBody(Guid Id);

    private static string NewRoundId() => Guid.CreateVersion7().ToString("D");

    private static async Task<ProdutoBody> CreateProductAsync(HttpClient client, string token, string codigo, string nome, long preco)
    {
        using var categoria = await SendAsync(client, HttpMethod.Post, "/cardapio/categorias", token, new { nome = "Cat " + codigo });
        Assert.Equal(HttpStatusCode.Created, categoria.StatusCode);
        var categoriaId = (await categoria.Content.ReadFromJsonAsync<CategoriaBody>())!.Id;
        using var response = await SendAsync(client, HttpMethod.Post, "/cardapio/produtos", token, new { codigo, nome, precoCentavos = preco, categoriaId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProdutoBody>())!;
    }

    private static Task<HttpResponseMessage> LaunchAsync(HttpClient client, string token, Guid comanda, int versao, Guid produto, int quantidade = 1, string? observacao = null, string? rodadaId = null) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/rodadas", token, new { rodadaId = rodadaId ?? NewRoundId(), versao, itens = new[] { new { produtoId = produto, quantidade, observacao } } });

    private static async Task<RodadaBody> LaunchOkAsync(HttpClient client, string token, Guid comanda, int versao, Guid produto, int quantidade = 1, string? observacao = null)
    {
        using var response = await LaunchAsync(client, token, comanda, versao, produto, quantidade, observacao);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RodadaBody>())!;
    }

    private static async Task<ItensBody> ItemsAsync(HttpClient client, string token, Guid comanda)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/itens", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ItensBody>())!;
    }

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, string token, Guid comanda, Guid item, int versao, string motivo, string? texto = null) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/itens/{item:D}/cancelar", token, new { versao, motivo, texto });

    private static Task<HttpResponseMessage> UndoAsync(HttpClient client, string token, Guid comanda, Guid rodada, int versao) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/rodadas/{rodada:D}/desfazer", token, new { versao });

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string codigo, int? versaoAtual = null)
    {
        Assert.Equal(status, response.StatusCode);
        var erro = (await response.Content.ReadFromJsonAsync<Erro>())!;
        Assert.Equal((codigo, versaoAtual), (erro.Codigo, erro.VersaoAtual));
    }

    private async Task<int> VersionOfAsync(Guid comanda) => (await QueryAsync(OrgA, UnitA, $"SELECT versao FROM comandas WHERE id = '{comanda:D}'", r => r.GetInt32(0))).Single();

    [Fact]
    public async Task Launching_copies_name_and_price_and_a_later_product_change_does_not_reach_the_comanda()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp 300ml", 1250);
        var porcao = await CreateProductAsync(client, t.Dono, "PORCAO", "Porcao de batata", 3490);
        var comanda = await OpenAsync(client, t.Garcom);

        using var response = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new
        {
            rodadaId = NewRoundId(),
            versao = comanda.Versao,
            itens = new object[] { new { produtoId = chopp.Id, quantidade = 3, observacao = "  sem colarinho " }, new { produtoId = porcao.Id, quantidade = 1 } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var rodada = (await response.Content.ReadFromJsonAsync<RodadaBody>())!;
        Assert.Equal((comanda.Id, 2, (DateTimeOffset?)null), (rodada.ComandaId, rodada.ComandaVersao, rodada.DesfeitaEm));
        Assert.Equal([("Chopp 300ml", 1250L, 3, "sem colarinho", "lancado", 1), ("Porcao de batata", 3490L, 1, null, "lancado", 2)], rodada.Itens.Select(i => (i.ProdutoNome, i.PrecoCentavos, i.Quantidade, i.Observacao, i.Estado, i.Posicao)).ToArray());
        Assert.Equal(2, await VersionOfAsync(comanda.Id));

        // The menu changes afterwards: the comanda keeps the name and price of the moment of the launch.
        using var change = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{chopp.Id:D}", t.Dono, new { codigo = "CHOPP", nome = "Chopp 500ml", precoCentavos = 1900, categoriaId = chopp.CategoriaId, ativo = true, versao = chopp.Versao });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var itens = await ItemsAsync(client, t.Garcom, comanda.Id);
        Assert.Equal(("Chopp 300ml", 1250L), (itens.Itens[0].ProdutoNome, itens.Itens[0].PrecoCentavos));
        Assert.Equal(1250L * 3 + 3490L, itens.ConsumoCentavos);

        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_tipo, depois->>'itens', depois->>'valorCentavos' FROM auditoria_registros WHERE acao = 'comanda:lancar-rodada'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
        Assert.Equal((Garcom, "sucesso", "rodada", "2", "7240"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'comanda.rodada-enviada' AND payload->>'RodadaId' = '{rodada.Id:D}'", r => r.GetInt32(0)));
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, $"SELECT lancamentos FROM produtos_frequencia WHERE produto_id = '{chopp.Id:D}'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task An_inactive_unknown_or_badly_filled_launch_is_refused_and_a_closed_comanda_takes_no_items()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var ativo = await CreateProductAsync(client, t.Dono, "ATIVO", "Ativo", 1000);
        var inativo = await CreateProductAsync(client, t.Dono, "INATIVO", "Inativo", 1000);
        using var off = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{inativo.Id:D}", t.Dono, new { codigo = "INATIVO", nome = "Inativo", precoCentavos = 1000, categoriaId = inativo.CategoriaId, ativo = false, versao = inativo.Versao });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var comanda = await OpenAsync(client, t.Garcom);

        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, 1, inativo.Id), HttpStatusCode.Conflict, "produto-inativo");
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, 1, Guid.NewGuid()), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, 1, ativo.Id, quantidade: 0), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, 1, ativo.Id, quantidade: 1000), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, 1, ativo.Id, observacao: new string('x', 201)), HttpStatusCode.BadRequest, "invalido");
        using var empty = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new { rodadaId = NewRoundId(), versao = 1, itens = Array.Empty<object>() });
        await AssertErrorAsync(empty, HttpStatusCode.BadRequest, "invalido");
        using var noId = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new { rodadaId = Guid.Empty, versao = 1, itens = new[] { new { produtoId = ativo.Id, quantidade = 1 } } });
        await AssertErrorAsync(noId, HttpStatusCode.BadRequest, "invalido");
        Assert.Equal((0L, 1), (await CountAsync("rodadas"), await VersionOfAsync(comanda.Id)));

        await RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'cancelada', fechada_em = now() WHERE id = '{comanda.Id:D}'");
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, 1, ativo.Id), HttpStatusCode.Conflict, "comanda-nao-aberta");
        Assert.Equal(0L, await CountAsync("rodadas"));
    }

    [Fact]
    public async Task An_acabou_product_cannot_be_launched_until_it_is_back_and_launching_freezes_the_effective_price_of_the_moment()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp 300ml", 1000);
        var comanda = await OpenAsync(client, t.Garcom);

        // "Acabou": the product stays on the menu, but a launch is refused like an inactive one and nothing is written.
        using var acabou = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{chopp.Id:D}", t.Dono, new { codigo = "CHOPP", nome = "Chopp 300ml", precoCentavos = 1000, categoriaId = chopp.CategoriaId, ativo = true, versao = chopp.Versao, indisponivel = true });
        Assert.Equal(HttpStatusCode.OK, acabou.StatusCode);
        var parado = (await acabou.Content.ReadFromJsonAsync<ProdutoBody>())!;
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, 1, chopp.Id), HttpStatusCode.Conflict, "produto-inativo");
        Assert.Equal((0L, 1), (await CountAsync("rodadas"), await VersionOfAsync(comanda.Id)));

        // Back on sale with a promotion in force: the item freezes the promotional price.
        var agora = DateTimeOffset.UtcNow;
        using var volta = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{chopp.Id:D}", t.Dono, new { codigo = "CHOPP", nome = "Chopp 300ml", precoCentavos = 1000, categoriaId = chopp.CategoriaId, ativo = true, versao = parado.Versao, indisponivel = false, promocao = new { precoCentavos = 800, inicio = agora.AddHours(-1), fim = agora.AddHours(1) } });
        Assert.Equal(HttpStatusCode.OK, volta.StatusCode);
        var promocional = (await volta.Content.ReadFromJsonAsync<ProdutoBody>())!;
        var primeira = await LaunchOkAsync(client, t.Garcom, comanda.Id, 1, chopp.Id);
        Assert.Equal(800L, primeira.Itens.Single().PrecoCentavos);

        // The unit price beats the promotion; the next launch freezes it, and the first item keeps 800 whatever happens to the menu afterwards.
        using var unidade = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{chopp.Id:D}/preco-unidade", t.Dono, new { precoCentavos = 1200 });
        Assert.Equal(HttpStatusCode.OK, unidade.StatusCode);
        var segunda = await LaunchOkAsync(client, t.Garcom, comanda.Id, primeira.ComandaVersao, chopp.Id);
        Assert.Equal(1200L, segunda.Itens.Single().PrecoCentavos);
        using var mudou = await SendAsync(client, HttpMethod.Put, $"/cardapio/produtos/{chopp.Id:D}", t.Dono, new { codigo = "CHOPP", nome = "Chopp 300ml", precoCentavos = 5000, categoriaId = chopp.CategoriaId, ativo = true, versao = promocional.Versao });
        Assert.Equal(HttpStatusCode.OK, mudou.StatusCode);
        Assert.Equal([800L, 1200L], (await ItemsAsync(client, t.Garcom, comanda.Id)).Itens.Select(i => i.PrecoCentavos));
        Assert.Equal(2000L, (await ItemsAsync(client, t.Garcom, comanda.Id)).ConsumoCentavos);
    }

    [Fact]
    public async Task Repeating_the_same_round_id_one_to_five_times_leaves_one_round_one_set_of_items_and_the_same_response()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1250);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodadaId = NewRoundId();

        var bodies = new List<string>();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            // The client never learned the answer, so it resends the same version: the repeat must not turn into a 409.
            using var response = await LaunchAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, quantidade: 2, rodadaId: rodadaId);
            statuses.Add(response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK], statuses);
        Assert.Single(bodies.Distinct());
        Assert.Equal((1L, 1L, 2), (await CountAsync("rodadas"), await CountAsync("itens_comanda"), await VersionOfAsync(comanda.Id)));
        Assert.Equal(1L, await CountAsync("outbox_eventos WHERE tipo = 'comanda.rodada-enviada'"));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'comanda:lancar-rodada'"));
    }

    [Fact]
    public async Task The_sender_undoes_the_whole_round_inside_the_window_and_nobody_else_and_never_afterwards()
    {
        var clock = new SkewClock();
        await using var api = new Api(_appConnection, configure: services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, quantidade: 2);
        var outra = await LaunchOkAsync(client, t.Garcom, comanda.Id, rodada.ComandaVersao, produto.Id);

        // Another waiter of the unit cannot even see this comanda; the version is untouched.
        using var notSender = await UndoAsync(client, t.Garcom2, comanda.Id, rodada.Id, outra.ComandaVersao);
        await AssertErrorAsync(notSender, HttpStatusCode.NotFound, "nao-encontrada");

        // The sender undoes the FIRST round inside the window: its items are cancelled with the fixed reason and leave the consumption.
        using var done = await UndoAsync(client, t.Garcom, comanda.Id, rodada.Id, outra.ComandaVersao);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var undone = (await done.Content.ReadFromJsonAsync<RodadaBody>())!;
        Assert.NotNull(undone.DesfeitaEm);
        Assert.Equal(4, undone.ComandaVersao);
        Assert.All(undone.Itens, i => Assert.Equal(("cancelado", "desfeito pelo garçom", Garcom), (i.Estado, i.MotivoCancelamento, i.CanceladoPor)));
        Assert.Equal(1000L, (await ItemsAsync(client, t.Garcom, comanda.Id)).ConsumoCentavos);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, depois->>'motivo' FROM auditoria_registros WHERE acao = 'comanda:desfazer-rodada'", r => (r.GetGuid(0), r.GetString(1))));
        Assert.Equal((Garcom, "desfeito pelo garçom"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'comanda.rodada-desfeita' AND payload->>'RodadaId' = '{rodada.Id:D}'", r => r.GetInt32(0)));

        // Undoing twice is refused; the second round, after the window, is refused too.
        await AssertErrorAsync(await UndoAsync(client, t.Garcom, comanda.Id, rodada.Id, 4), HttpStatusCode.Conflict, "rodada-ja-desfeita");
        clock.Skew = TimeSpan.FromSeconds(9);
        await AssertErrorAsync(await UndoAsync(client, t.Garcom, comanda.Id, outra.Id, 4), HttpStatusCode.Conflict, "janela-expirada");
        Assert.Equal(4, await VersionOfAsync(comanda.Id));
    }

    [Fact]
    public async Task Undo_does_not_need_the_current_version_so_a_second_device_launching_meanwhile_does_not_turn_it_into_409()
    {
        var clock = new SkewClock();
        await using var api = new Api(_appConnection, configure: services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);
        var outra = await LaunchOkAsync(client, t.Garcom, comanda.Id, rodada.ComandaVersao, produto.Id);
        var terceira = await LaunchOkAsync(client, t.Garcom, comanda.Id, outra.ComandaVersao, produto.Id);
        Assert.Equal(4, await VersionOfAsync(comanda.Id));

        // The sender still holds version 2 while the comanda is at 4: the undo goes through and the answer and the audit carry the new version.
        using var done = await UndoAsync(client, t.Garcom, comanda.Id, rodada.Id, rodada.ComandaVersao);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.NotNull((await done.Content.ReadFromJsonAsync<RodadaBody>())!.DesfeitaEm);
        Assert.Equal(5, await VersionOfAsync(comanda.Id));
        Assert.Equal(2000L, (await ItemsAsync(client, t.Garcom, comanda.Id)).ConsumoCentavos);
        Assert.Equal(5, Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT (depois->>'versao')::int FROM auditoria_registros WHERE acao = 'comanda:desfazer-rodada'", r => r.GetInt32(0))));

        // Still refused for another user, with an item of the round cancelled, and after the window, whatever version is sent.
        using var cancel = await CancelAsync(client, t.Garcom, comanda.Id, outra.Itens[0].Id, 5, "cliente-desistiu");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await AssertErrorAsync(await UndoAsync(client, t.Garcom, comanda.Id, outra.Id, 1), HttpStatusCode.Conflict, "rodada-com-item-cancelado");
        await AssertErrorAsync(await UndoAsync(client, t.Dono, comanda.Id, terceira.Id, 1), HttpStatusCode.Conflict, "somente-quem-enviou");
        var antes = await VersionOfAsync(comanda.Id);
        clock.Skew = TimeSpan.FromSeconds(9);
        await AssertErrorAsync(await UndoAsync(client, t.Garcom, comanda.Id, terceira.Id, 1), HttpStatusCode.Conflict, "janela-expirada");
        Assert.Equal(antes, await VersionOfAsync(comanda.Id));
    }

    [Fact]
    public async Task The_same_round_id_sent_twice_at_the_same_time_makes_one_round_and_two_successful_answers()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodadaId = NewRoundId();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Fire()
        {
            await gate.Task;
            return await LaunchAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, rodadaId: rodadaId);
        }

        var first = Fire();
        var second = Fire();
        gate.SetResult();
        var responses = await Task.WhenAll(first, second);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], responses.Select(r => r.StatusCode).Order().ToArray());
        Assert.Single((await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
        Assert.Equal((1L, 1L, 2), (await CountAsync("rodadas"), await CountAsync("itens_comanda"), await VersionOfAsync(comanda.Id)));
        Assert.Equal((1L, 1L), (await CountAsync("outbox_eventos WHERE tipo = 'comanda.rodada-enviada'"), await CountAsync("auditoria_registros WHERE acao = 'comanda:lancar-rodada'")));
    }

    [Fact]
    public async Task Only_the_sender_may_undo_a_round_even_for_a_waiter_who_may_see_the_comanda()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);

        // The owner sees every comanda but is not the sender.
        await AssertErrorAsync(await UndoAsync(client, t.Dono, comanda.Id, rodada.Id, rodada.ComandaVersao), HttpStatusCode.Conflict, "somente-quem-enviou");
        Assert.Equal((2, 0L), (await VersionOfAsync(comanda.Id), await CountAsync("auditoria_registros WHERE acao = 'comanda:desfazer-rodada'")));
    }

    [Fact]
    public async Task A_round_with_an_item_already_cancelled_cannot_be_undone()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);
        using var cancel = await CancelAsync(client, t.Garcom, comanda.Id, rodada.Itens[0].Id, rodada.ComandaVersao, "cliente-desistiu");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        await AssertErrorAsync(await UndoAsync(client, t.Garcom, comanda.Id, rodada.Id, 3), HttpStatusCode.Conflict, "rodada-com-item-cancelado");
        Assert.Equal(3, await VersionOfAsync(comanda.Id));
    }

    [Fact]
    public async Task Cancelling_an_item_needs_a_reason_from_the_list_leaves_the_consumption_and_is_audited_with_the_reason()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, quantidade: 3);
        var item = rodada.Itens[0];

        // Reason missing, not on the list, or 'outro' without text: refused, nothing changes.
        await AssertErrorAsync(await CancelAsync(client, t.Garcom, comanda.Id, item.Id, 2, ""), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await CancelAsync(client, t.Garcom, comanda.Id, item.Id, 2, "porque sim"), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await CancelAsync(client, t.Garcom, comanda.Id, item.Id, 2, "outro"), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await CancelAsync(client, t.Garcom, comanda.Id, item.Id, 2, "outro", "   "), HttpStatusCode.BadRequest, "invalido");
        Assert.Equal((2, 3000L), (await VersionOfAsync(comanda.Id), (await ItemsAsync(client, t.Garcom, comanda.Id)).ConsumoCentavos));

        using var response = await CancelAsync(client, t.Garcom, comanda.Id, item.Id, 2, "outro", " o cliente trocou de mesa ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ItemCanceladoBody>())!;
        Assert.Equal((3, "cancelado", "o cliente trocou de mesa", Garcom), (body.ComandaVersao, body.Item.Estado, body.Item.MotivoCancelamento, body.Item.CanceladoPor));
        Assert.Equal(0L, (await ItemsAsync(client, t.Garcom, comanda.Id)).ConsumoCentavos);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_tipo, objeto_id, depois->>'motivo' FROM auditoria_registros WHERE acao = 'comanda:cancelar-item'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3))));
        Assert.Equal((Garcom, "item-comanda", item.Id.ToString("D"), "o cliente trocou de mesa"), audit);
        Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT 1 FROM outbox_eventos WHERE tipo = 'comanda.item-cancelado' AND payload->>'ItemId' = '{item.Id:D}'", r => r.GetInt32(0)));
        await AssertErrorAsync(await CancelAsync(client, t.Garcom, comanda.Id, item.Id, 3, "cliente-desistiu"), HttpStatusCode.Conflict, "item-ja-cancelado");

        // Every code of the standard list is accepted and stored as its words.
        foreach (var (codigo, palavra) in new[] { ("cliente-desistiu", "cliente desistiu"), ("erro-de-lancamento", "erro de lançamento"), ("item-indisponivel", "item indisponível") })
        {
            var r = await LaunchOkAsync(client, t.Garcom, comanda.Id, await VersionOfAsync(comanda.Id), produto.Id);
            using var c = await CancelAsync(client, t.Garcom, comanda.Id, r.Itens[0].Id, r.ComandaVersao, codigo);
            Assert.Equal(HttpStatusCode.OK, c.StatusCode);
            Assert.Equal(palavra, (await c.Content.ReadFromJsonAsync<ItemCanceladoBody>())!.Item.MotivoCancelamento);
        }
    }

    [Fact]
    public async Task A_stale_version_on_any_write_returns_409_with_the_current_version_and_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);
        var rodadas = await CountAsync("rodadas");

        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda.Id, 1, produto.Id), HttpStatusCode.Conflict, "versao-desatualizada", 2);
        await AssertErrorAsync(await CancelAsync(client, t.Garcom, comanda.Id, rodada.Itens[0].Id, 1, "cliente-desistiu"), HttpStatusCode.Conflict, "versao-desatualizada", 2);

        Assert.Equal((rodadas, 1L, 2, 1L), (await CountAsync("rodadas"), await CountAsync("itens_comanda"), await VersionOfAsync(comanda.Id), await CountAsync("auditoria_registros WHERE acao LIKE 'comanda:%rodada' OR acao = 'comanda:cancelar-item'")));
        Assert.Equal("lancado", (await ItemsAsync(client, t.Garcom, comanda.Id)).Itens.Single().Estado);
    }

    [Fact]
    public async Task Two_clients_launching_into_the_same_comanda_at_the_same_time_lose_no_item()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Dono);

        // Same version, same instant: exactly one wins, the other is told the current version and wrote nothing.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Fire()
        {
            await gate.Task;
            return await LaunchAsync(client, t.Dono, comanda.Id, comanda.Versao, produto.Id);
        }

        var first = Fire();
        var second = Fire();
        gate.SetResult();
        var responses = await Task.WhenAll(first, second);
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order().ToArray());
        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var erro = (await loser.Content.ReadFromJsonAsync<Erro>())!;
        Assert.Equal(("versao-desatualizada", 2), (erro.Codigo, erro.VersaoAtual));
        Assert.Equal((1L, 2), (await CountAsync("rodadas"), await VersionOfAsync(comanda.Id)));

        // Six devices keep launching with the usual retry (read the version, send, retry on 409): every item is in the end.
        async Task LaunchWithRetryAsync()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var versao = (await ListAsync(client, t.Dono)).Single(c => c.Id == comanda.Id).Versao;
                using var response = await LaunchAsync(client, t.Dono, comanda.Id, versao, produto.Id);
                if (response.StatusCode == HttpStatusCode.Created)
                {
                    return;
                }

                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

            throw new InvalidOperationException("never got through");
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => LaunchWithRetryAsync()));
        Assert.Equal((7L, 7L, 8), (await CountAsync("rodadas"), await CountAsync("itens_comanda"), await VersionOfAsync(comanda.Id)));
        Assert.Equal(7000L, (await ItemsAsync(client, t.Dono, comanda.Id)).ConsumoCentavos);
    }

    [Fact]
    public async Task A_waiter_reaches_only_their_comandas_and_another_organization_reaches_none()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);

        // Another waiter of the same unit: not found (the comanda is not theirs to see), and nothing changes.
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom2, comanda.Id, 2, produto.Id), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await CancelAsync(client, t.Garcom2, comanda.Id, rodada.Itens[0].Id, 2, "cliente-desistiu"), HttpStatusCode.NotFound, "nao-encontrada");
        using var items = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda.Id:D}/itens", t.Garcom2);
        await AssertErrorAsync(items, HttpStatusCode.NotFound, "nao-encontrada");

        // Another organization: the same.
        await AssertErrorAsync(await LaunchAsync(client, t.DonoB, comanda.Id, 2, produto.Id), HttpStatusCode.NotFound, "nao-encontrada");
        using var itemsB = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda.Id:D}/itens", t.DonoB);
        await AssertErrorAsync(itemsB, HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal((1L, 2), (await CountAsync("rodadas"), await VersionOfAsync(comanda.Id)));
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM rodadas", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Each_round_route_is_denied_without_its_permission_and_without_a_session()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var comanda = await OpenAsync(client, t.Garcom);
        var id = Guid.NewGuid();

        foreach (var token in new string?[] { t.Mudo, t.Supervisor })
        {
            using var launch = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", token, new { rodadaId = id, versao = 1, itens = new[] { new { produtoId = id, quantidade = 1 } } });
            using var undo = await UndoAsync(client, token!, comanda.Id, id, 1);
            using var cancel = await CancelAsync(client, token!, comanda.Id, id, 1, "cliente-desistiu");
            Assert.All([launch, undo, cancel], r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
            Assert.All([launch, undo, cancel], r => Assert.Equal(0, r.Content.Headers.ContentLength ?? 0));
        }

        using var anonymous = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", null, new { rodadaId = id, versao = 1, itens = Array.Empty<object>() });
        using var anonymousItems = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda.Id:D}/itens", null);
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (anonymous.StatusCode, anonymousItems.StatusCode));
        Assert.Equal(1, await VersionOfAsync(comanda.Id));
    }

    [Fact]
    public async Task When_the_unit_of_work_throws_after_the_outbox_the_version_the_round_the_items_the_audit_and_the_event_roll_back_together()
    {
        await using var api = new Api(_appConnection, configure: services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IEventosDeComandas));
            services.Remove(real);
            services.AddSingleton<IEventosDeComandas>(p => new EventsThatThrow(ActivatorUtilities.CreateInstance<OutboxEventosDeComandas>(p)));
        });
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);

        // The comanda itself is seeded straight into the table (opening goes through the same throwing events).
        var comandaId = Guid.NewGuid();
        await RunSqlAsync(OrgA, UnitA, $"INSERT INTO comandas (id, organizacao_id, unidade_id, dia_operacional, numero, identificacao, garcom_id, aberta_em, taxa_servico_percentual) VALUES ('{comandaId:D}', '{OrgA:D}', '{UnitA:D}', current_date, 1, 'Mesa', '{Garcom:D}', now(), 10)");
        var gestao = api.Services.GetRequiredService<GestaoDeRodadas>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gestao.LancarAsync(new FakeRequest(Garcom, new OrganizationContext(OrgA, UnitA)), comandaId, Guid.CreateVersion7(), 1, [new ItemParaLancar(produto.Id, 1, null)]));

        Assert.Equal((0L, 0L, 0L, 0L, 0L), (await CountAsync("rodadas"), await CountAsync("itens_comanda"), await CountAsync("auditoria_registros WHERE acao = 'comanda:lancar-rodada'"), await CountAsync("outbox_eventos WHERE tipo = 'comanda.rodada-enviada'"), await CountAsync("produtos_frequencia")));
        Assert.Equal(1, await VersionOfAsync(comandaId));
    }

    [Fact]
    public async Task The_round_queries_have_the_expected_sql_with_no_organization_filter()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new MesasDbContext(tx);

        var itens = PostgresRodadasStore.ItensDaRodada(context, Guid.NewGuid()).ToQueryString();
        var daComanda = PostgresRodadasStore.ItensDaComanda(context, Guid.NewGuid()).ToQueryString();
        var achar = PostgresComandasStore.Achar(context, UnitA, Guid.NewGuid()).ToQueryString();

        Assert.Contains("FROM itens_comanda AS", itens, StringComparison.Ordinal);
        Assert.Contains("i.rodada_id = @", itens, StringComparison.Ordinal);
        Assert.Contains("ORDER BY i.posicao", itens, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.enviada_em, i.rodada_id, i.posicao", daComanda, StringComparison.Ordinal);
        Assert.Contains("c.unidade_id = @", achar, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", itens + daComanda + achar, StringComparison.Ordinal);
        Assert.Empty(await SchemaDrift.FindAsync(context));
    }

    private async Task<long> CountAsync(string tableAndFilter) => (await QueryAsync(OrgA, UnitA, $"SELECT count(*) FROM {tableAndFilter}", r => r.GetInt64(0))).Single();

    private async Task RunSqlAsync(Guid org, Guid unit, string sql)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        await command.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }
}
