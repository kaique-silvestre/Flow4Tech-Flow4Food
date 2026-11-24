using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Comandas.Infrastructure;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0151: promotions and combos through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions). The owner holds <c>promocao:ver|gerenciar</c>; "Mudo" is given only
/// <c>promocao:ver</c>; the waiter holds neither. The rule is stored, judged by the SERVER at launch, and the promotion applied is written on the item and as the ordinary item discount of ADR-018.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record PromocaoLida(Guid Id, Guid? UnidadeId, bool TodasAsUnidades, string Nome, string Tipo, decimal? Percentual, long? ValorCentavos, bool Ativo, DateTimeOffset? ValidoDe, DateTimeOffset? ValidoAte, List<int> DiasDaSemana, string? HoraInicio, string? HoraFim, int Versao, List<ProdutoDaPromocaoLido> Produtos, Guid CriadoPor, Guid AtualizadoPor);

    private sealed record ProdutoDaPromocaoLido(Guid ProdutoId, int Quantidade);

    private sealed record PromocaoErroBody(string Codigo, int? VersaoAtual);

    private sealed record ItemComPromocao(Guid Id, Guid ProdutoId, long PrecoCentavos, int Quantidade, long TotalCentavos, Guid? PromocaoId, string? PromocaoNome, long? PromocaoCentavos);

    private sealed record RodadaComPromocao(int ComandaVersao, List<ItemComPromocao> Itens);

    private sealed record RepetidaComPromocao(RodadaComPromocao Rodada);

    private sealed record DescontoDaPromocao(Guid Id, Guid? ItemId, long AplicadoCentavos, string Motivo, Guid? PromocaoId);

    private static readonly int[] FridayAndSaturday = [5, 6];

    private static readonly int[] EveryDay = [0, 1, 2, 3, 4, 5, 6];

    private static Task<HttpResponseMessage> CreatePromoAsync(HttpClient client, string? token, object body) => SendAsync(client, HttpMethod.Post, "/promocoes", token, body);

    private static object PercentRule(Guid produto, decimal percentual = 10m, string nome = "Chopp em dobro", bool ativo = true, bool todas = true) =>
        new { nome, tipo = "percentual", percentual, todasAsUnidades = todas, ativo, produtos = new[] { new { produtoId = produto } } };

    private static async Task<ContaLida> AccountAsync(HttpClient client, string token, Guid comanda) =>
        await BodyAsync<ContaLida>(await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/conta", token), HttpStatusCode.OK);

    private static async Task<PromocaoLida> CreatePromoOkAsync(HttpClient client, string token, object body) =>
        await BodyAsync<PromocaoLida>(await CreatePromoAsync(client, token, body), HttpStatusCode.Created);

    private static async Task<RodadaComPromocao> LaunchManyAsync(HttpClient client, string token, Guid comanda, int versao, params (Guid Produto, int Quantidade)[] itens) =>
        await BodyAsync<RodadaComPromocao>(await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/rodadas", token, new { rodadaId = NewRoundId(), versao, itens = itens.Select(i => new { produtoId = i.Produto, quantidade = i.Quantidade }).ToArray() }), HttpStatusCode.Created);

    private static async Task<List<DescontoDaPromocao>> DiscountsAsync(HttpClient client, string token, Guid comanda) =>
        await BodyAsync<List<DescontoDaPromocao>>(await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/descontos", token), HttpStatusCode.OK);

    /// <summary>The unit's wall clock now (the default zone of a unit), to build windows that contain or miss the present.</summary>
    private static DateTime LocalNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime;

    private async Task<string> GivePromoViewerAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfil = await perfis.CriarAsync(request, "Ve promocoes", [new RegraDeAcesso(RuleEffect.Allow, "promocao:ver")]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Mudo, perfil.Id!.Value));
        return await FreshTokenAsync(api, Mudo);
    }

    [Fact]
    public async Task Creating_then_editing_a_rule_stores_it_writes_one_audit_record_each_and_a_stale_version_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);

        var criada = await CreatePromoOkAsync(client, t.Dono, new { nome = "  Happy hour  ", tipo = "percentual", percentual = 12.5m, todasAsUnidades = false, ativo = true, diasDaSemana = FridayAndSaturday, horaInicio = "18:00", horaFim = "22:00", validoDe = "2026-01-01T00:00:00Z", produtos = new[] { new { produtoId = chopp.Id } } });

        Assert.Equal(("Happy hour", "percentual", 12.5m, (long?)null, false, 1, true, "18:00", "22:00"), (criada.Nome, criada.Tipo, criada.Percentual, criada.ValorCentavos, criada.TodasAsUnidades, criada.Versao, criada.Ativo, criada.HoraInicio, criada.HoraFim));
        Assert.Equal((UnitA, DonoA, DonoA), (criada.UnidadeId, criada.CriadoPor, criada.AtualizadoPor));
        Assert.Equal(FridayAndSaturday, criada.DiasDaSemana);
        Assert.Equal([(chopp.Id, 1)], criada.Produtos.Select(p => (p.ProdutoId, p.Quantidade)));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'promocao:criar' AND resultado = 'sucesso'"));
        var auditoria = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT usuario_id, objeto_tipo, objeto_id, depois->>'nome', depois->>'tipo' FROM auditoria_registros WHERE acao = 'promocao:criar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
        Assert.Equal((DonoA, "promocao", criada.Id.ToString("D"), "Happy hour", "percentual"), auditoria);

        var lida = await BodyAsync<PromocaoLida>(await SendAsync(client, HttpMethod.Get, $"/promocoes/{criada.Id:D}", t.Dono), HttpStatusCode.OK);
        var lista = await BodyAsync<List<PromocaoLida>>(await SendAsync(client, HttpMethod.Get, "/promocoes", t.Dono), HttpStatusCode.OK);
        Assert.Equal(criada.Id, lida.Id);
        Assert.Equal([criada.Id], lista.Select(p => p.Id));

        var editada = await BodyAsync<PromocaoLida>(await SendAsync(client, HttpMethod.Put, $"/promocoes/{criada.Id:D}", t.Dono, new { versao = 1, nome = "Happy hour 2", percentual = 20m, todasAsUnidades = true, ativo = false }), HttpStatusCode.OK);
        Assert.Equal(("Happy hour 2", (decimal?)20m, true, false, 2, (string?)null), (editada.Nome, editada.Percentual, editada.TodasAsUnidades, editada.Ativo, editada.Versao, editada.HoraInicio));
        Assert.Equal(("percentual", (Guid?)null), (editada.Tipo, editada.UnidadeId));
        Assert.Equal(EveryDay, editada.DiasDaSemana);
        var depois = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT antes->>'nome', depois->>'nome', depois->>'versao' FROM auditoria_registros WHERE acao = 'promocao:alterar'", r => (r.GetString(0), r.GetString(1), r.GetString(2))));
        Assert.Equal(("Happy hour", "Happy hour 2", "2"), depois);

        using var velha = await SendAsync(client, HttpMethod.Put, $"/promocoes/{criada.Id:D}", t.Dono, new { versao = 1, nome = "Outra", percentual = 5m, todasAsUnidades = true, ativo = true });
        Assert.Equal(HttpStatusCode.Conflict, velha.StatusCode);
        var erro = (await velha.Content.ReadFromJsonAsync<PromocaoErroBody>())!;
        Assert.Equal(("versao-desatualizada", 2), (erro.Codigo, erro.VersaoAtual));
        Assert.Equal(("Happy hour 2", 2L, 1L), ((await QueryAsync(OrgA, UnitA, "SELECT nome FROM promocoes", r => r.GetString(0))).Single(), (await QueryAsync(OrgA, UnitA, "SELECT versao::bigint FROM promocoes", r => r.GetInt64(0))).Single(), await CountAsync("auditoria_registros WHERE acao = 'promocao:alterar'")));
    }

    [Fact]
    public async Task A_rule_that_does_not_make_sense_is_400_and_writes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var porcao = await CreateProductAsync(client, t.Dono, "PORCAO", "Porcao", 3000);
        var ok = new[] { new { produtoId = chopp.Id, quantidade = 1 } };

        var invalidas = new object[]
        {
            new { nome = "", tipo = "percentual", percentual = 10m, ativo = true, produtos = ok },
            new { nome = new string('x', 81), tipo = "percentual", percentual = 10m, ativo = true, produtos = ok },
            new { nome = "x", tipo = "sorteio", percentual = 10m, ativo = true, produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 0m, ativo = true, produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 100.01m, ativo = true, produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 10.005m, ativo = true, produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 10m, valorCentavos = 5, ativo = true, produtos = ok },
            new { nome = "x", tipo = "valor", ativo = true, produtos = ok },
            new { nome = "x", tipo = "valor", valorCentavos = 0, ativo = true, produtos = ok },
            new { nome = "x", tipo = "valor", valorCentavos = 100, percentual = 10m, ativo = true, produtos = ok },
            new { nome = "x", tipo = "combo", valorCentavos = 3000, ativo = true, produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, produtos = new[] { new { produtoId = chopp.Id, quantidade = 2 } } },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, produtos = new[] { new { produtoId = chopp.Id, quantidade = 1 }, new { produtoId = chopp.Id, quantidade = 1 } } },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, produtos = Array.Empty<object>() },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, diasDaSemana = new[] { 7 }, produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, diasDaSemana = Array.Empty<int>(), produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, horaInicio = "18:00", produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, horaInicio = "18:00", horaFim = "18:00", produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, horaInicio = "25:00", horaFim = "26:00", produtos = ok },
            new { nome = "x", tipo = "percentual", percentual = 10m, ativo = true, validoDe = "2026-12-01T00:00:00Z", validoAte = "2026-12-01T00:00:00Z", produtos = ok },
        };
        foreach (var corpo in invalidas)
        {
            using var response = await CreatePromoAsync(client, t.Dono, corpo);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalido", (await response.Content.ReadFromJsonAsync<PromocaoErroBody>())!.Codigo);
        }

        using var inexistente = await CreatePromoAsync(client, t.Dono, new { nome = "x", tipo = "combo", valorCentavos = 3000, ativo = true, produtos = new[] { new { produtoId = porcao.Id, quantidade = 1 }, new { produtoId = Guid.NewGuid(), quantidade = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, inexistente.StatusCode);
        Assert.Equal("produto-invalido", (await inexistente.Content.ReadFromJsonAsync<PromocaoErroBody>())!.Codigo);
        Assert.Equal((0L, 0L, 0L), (await CountAsync("promocoes"), await CountAsync("promocao_produtos"), await CountAsync("auditoria_registros WHERE acao = 'promocao:criar'")));

        var criada = await CreatePromoOkAsync(client, t.Dono, PercentRule(chopp.Id));
        foreach (var corpo in new object[] { new { versao = 1, nome = "", percentual = 10m, ativo = true }, new { versao = 1, nome = "x", ativo = true }, new { versao = 1, nome = "x", percentual = 10m, valorCentavos = 3, ativo = true }, new { versao = 0, nome = "x", percentual = 10m, ativo = true } })
        {
            using var response = await SendAsync(client, HttpMethod.Put, $"/promocoes/{criada.Id:D}", t.Dono, corpo);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var some = await SendAsync(client, HttpMethod.Put, $"/promocoes/{Guid.NewGuid():D}", t.Dono, new { versao = 1, nome = "x", percentual = 10m, ativo = true });
        Assert.Equal(HttpStatusCode.NotFound, some.StatusCode);
        Assert.Equal((1L, 0L), (await CountAsync("promocoes WHERE versao = 1"), await CountAsync("auditoria_registros WHERE acao = 'promocao:alterar'")));
    }

    [Fact]
    public async Task Without_the_permission_every_route_is_401_or_an_empty_403_and_nothing_is_written_while_the_viewer_cannot_manage()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var existente = await CreatePromoOkAsync(client, t.Dono, PercentRule(chopp.Id));
        var leitor = await GivePromoViewerAsync(api);
        var antes = await CountAsync("auditoria_registros WHERE acao LIKE 'promocao:%'");

        foreach (var semPermissao in new[] { t.Garcom, t.Supervisor })
        {
            foreach (var (metodo, caminho, corpo) in new (HttpMethod, string, object?)[]
            {
                (HttpMethod.Get, "/promocoes", null),
                (HttpMethod.Get, $"/promocoes/{existente.Id:D}", null),
                (HttpMethod.Post, "/promocoes", PercentRule(chopp.Id)),
                (HttpMethod.Put, $"/promocoes/{existente.Id:D}", new { versao = 1, nome = "x", percentual = 5m, ativo = true }),
            })
            {
                using var response = await SendAsync(client, metodo, caminho, semPermissao, corpo);
                await AssertForbiddenEmptyAsync(response);
            }
        }

        foreach (var (metodo, caminho, corpo) in new (HttpMethod, string, object?)[] { (HttpMethod.Get, "/promocoes", null), (HttpMethod.Post, "/promocoes", PercentRule(chopp.Id)) })
        {
            using var response = await SendAsync(client, metodo, caminho, null, corpo);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Only promocao:ver: reads, never writes.
        using var lista = await SendAsync(client, HttpMethod.Get, "/promocoes", leitor);
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        await AssertForbiddenEmptyAsync(await CreatePromoAsync(client, leitor, PercentRule(chopp.Id, nome: "Do leitor")));
        await AssertForbiddenEmptyAsync(await SendAsync(client, HttpMethod.Put, $"/promocoes/{existente.Id:D}", leitor, new { versao = 1, nome = "x", percentual = 5m, ativo = true }));
        Assert.Equal((1L, antes), (await CountAsync("promocoes"), await CountAsync("auditoria_registros WHERE acao LIKE 'promocao:%'")));
        Assert.Equal(1L, (await QueryAsync(OrgA, UnitA, "SELECT versao::bigint FROM promocoes", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Another_organization_neither_sees_nor_changes_nor_uses_the_rules_and_products_of_this_one()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var regra = await CreatePromoOkAsync(client, t.Dono, PercentRule(chopp.Id));

        Assert.Empty(await BodyAsync<List<PromocaoLida>>(await SendAsync(client, HttpMethod.Get, "/promocoes", t.DonoB), HttpStatusCode.OK));
        using var ver = await SendAsync(client, HttpMethod.Get, $"/promocoes/{regra.Id:D}", t.DonoB);
        using var mudar = await SendAsync(client, HttpMethod.Put, $"/promocoes/{regra.Id:D}", t.DonoB, new { versao = 1, nome = "invadida", percentual = 99m, todasAsUnidades = true, ativo = true });
        using var usar = await CreatePromoAsync(client, t.DonoB, PercentRule(chopp.Id, nome: "Com produto alheio"));

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.BadRequest), (ver.StatusCode, mudar.StatusCode, usar.StatusCode));
        Assert.Equal("produto-invalido", (await usar.Content.ReadFromJsonAsync<PromocaoErroBody>())!.Codigo);
        Assert.Empty(await QueryAsync(OrgB, UnitB, "SELECT 1 FROM promocoes", r => r.GetInt32(0)));
        Assert.Empty(await QueryAsync(OrgB, UnitB, "SELECT 1 FROM promocao_produtos", r => r.GetInt32(0)));
        Assert.Equal(("Chopp em dobro", 1L), ((await QueryAsync(OrgA, UnitA, "SELECT nome FROM promocoes", r => r.GetString(0))).Single(), await CountAsync("promocoes")));

        // Org B has its own product and rule, and the two never mix.
        var geral = await CreateProductAsync(client, t.DonoB, "GERAL", "Geral", 500);
        var minha = await CreatePromoOkAsync(client, t.DonoB, PercentRule(geral.Id, nome: "De B"));
        Assert.Equal([minha.Id], (await BodyAsync<List<PromocaoLida>>(await SendAsync(client, HttpMethod.Get, "/promocoes", t.DonoB), HttpStatusCode.OK)).Select(p => p.Id));
        Assert.Equal([regra.Id], (await BodyAsync<List<PromocaoLida>>(await SendAsync(client, HttpMethod.Get, "/promocoes", t.Dono), HttpStatusCode.OK)).Select(p => p.Id));
    }

    [Fact]
    public async Task A_rule_in_force_is_applied_by_the_server_on_the_item_and_as_a_discount_and_the_manual_discount_comes_after_it_in_the_ADR_018_order()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var regra = await CreatePromoOkAsync(client, t.Dono, PercentRule(chopp.Id, 10m, "Chopp com 10%"));
        var comanda = await OpenAsync(client, t.Garcom);

        // The client cannot say the amount: extra fields on the item are ignored, the server decides.
        using var pedido = await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas", t.Garcom, new { rodadaId = NewRoundId(), versao = comanda.Versao, itens = new[] { new { produtoId = chopp.Id, quantidade = 2, promocaoId = Guid.NewGuid(), promocaoCentavos = 1999, descontoCentavos = 1999 } } });
        var rodada = await BodyAsync<RodadaComPromocao>(pedido, HttpStatusCode.Created);

        var item = Assert.Single(rodada.Itens);
        Assert.Equal((regra.Id, "Chopp com 10%", 200L, 2000L), (item.PromocaoId, item.PromocaoNome, item.PromocaoCentavos, item.TotalCentavos));
        var conta = await AccountAsync(client, t.Garcom, comanda.Id);
        Assert.Equal((2000L, 200L, 1980L), (conta.ConsumoCentavos, conta.DescontoCentavos, conta.TotalCentavos));
        var descontos = await DiscountsAsync(client, t.Garcom, comanda.Id);
        var desconto = Assert.Single(descontos);
        Assert.Equal((item.Id, 200L, "Promoção: Chopp com 10%", (Guid?)regra.Id), (desconto.ItemId, desconto.AplicadoCentavos, desconto.Motivo, desconto.PromocaoId));
        var guardado = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT promocao_id, promocao_nome, promocao_centavos FROM itens_comanda WHERE id = '{item.Id:D}'", r => (r.GetGuid(0), r.GetString(1), r.GetInt64(2))));
        Assert.Equal((regra.Id, "Chopp com 10%", 200L), guardado);
        var rodadaAuditada = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT depois->>'promocaoCentavos', depois->>'valorCentavos' FROM auditoria_registros WHERE acao = 'comanda:lancar-rodada'", r => (r.GetString(0), r.GetString(1))));
        Assert.Equal(("200", "2000"), rodadaAuditada);

        // ADR-018: the promotion came off the line first; a manual 50% is taken from what is left (1800), not from the table value (2000).
        var versao = rodada.ComandaVersao;
        var manual = await BodyAsync<DescontoAplicadoBody>(await PercentAsync(client, t.Dono, comanda.Id, versao, 50m, item.Id), HttpStatusCode.Created);
        Assert.Equal(900L, manual.Desconto.AplicadoCentavos);
        Assert.Equal(1100L, manual.Conta.DescontoCentavos);
        using var alem = await ValueAsync(client, t.Dono, comanda.Id, manual.Conta.Versao, 901, item.Id);
        await AssertErrorAsync(alem, HttpStatusCode.Conflict, "desconto-excede");
        var total = await BodyAsync<DescontoAplicadoBody>(await ValueAsync(client, t.Dono, comanda.Id, manual.Conta.Versao, 900, item.Id), HttpStatusCode.Created);
        Assert.Equal((2000L, 0L), (total.Conta.DescontoCentavos, total.Conta.TotalCentavos));
    }

    [Fact]
    public async Task A_combo_in_one_round_prices_the_full_sets_and_writes_each_line_with_its_share()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var porcao = await CreateProductAsync(client, t.Dono, "PORCAO", "Porcao", 3000);
        var combo = await CreatePromoOkAsync(client, t.Dono, new { nome = "Chopp com porcao", tipo = "combo", valorCentavos = 3300, ativo = true, todasAsUnidades = true, produtos = new[] { new { produtoId = chopp.Id, quantidade = 1 }, new { produtoId = porcao.Id, quantidade = 1 } } });
        var comanda = await OpenAsync(client, t.Garcom);

        var rodada = await LaunchManyAsync(client, t.Garcom, comanda.Id, comanda.Versao, (chopp.Id, 2), (porcao.Id, 2));

        Assert.Equal([350L, 1050L], rodada.Itens.Select(i => i.PromocaoCentavos));
        Assert.All(rodada.Itens, i => Assert.Equal(combo.Id, i.PromocaoId));
        var conta = await AccountAsync(client, t.Garcom, comanda.Id);
        Assert.Equal((8000L, 1400L), (conta.ConsumoCentavos, conta.DescontoCentavos));

        // Only the chopp: the combo needs the porcao too, so the next round pays table price.
        var sozinho = await LaunchManyAsync(client, t.Garcom, comanda.Id, rodada.ComandaVersao, (chopp.Id, 1));
        Assert.Equal([(long?)null], sozinho.Itens.Select(i => i.PromocaoCentavos));
        Assert.Null(sozinho.Itens[0].PromocaoId);
    }

    [Fact]
    public async Task A_rule_turned_off_expired_not_started_outside_its_days_or_hours_or_of_another_unit_or_product_is_not_applied_and_one_inside_all_of_them_is()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var outro = await CreateProductAsync(client, t.Dono, "OUTRO", "Outro", 1000);
        var agora = DateTimeOffset.UtcNow;
        var local = LocalNow();
        var hoje = (int)local.DayOfWeek;
        var amanha = (hoje + 1) % 7;
        string Hora(DateTime d) => d.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        // Rules that must NOT apply to a chopp launched now in UnitA.
        await CreatePromoOkAsync(client, t.Dono, PercentRule(chopp.Id, 50m, "Desligada", ativo: false));
        await CreatePromoOkAsync(client, t.Dono, new { nome = "Expirada", tipo = "percentual", percentual = 50m, ativo = true, validoAte = agora.AddMinutes(-1), produtos = new[] { new { produtoId = chopp.Id } } });
        await CreatePromoOkAsync(client, t.Dono, new { nome = "Futura", tipo = "percentual", percentual = 50m, ativo = true, validoDe = agora.AddHours(1), produtos = new[] { new { produtoId = chopp.Id } } });
        await CreatePromoOkAsync(client, t.Dono, new { nome = "Amanha", tipo = "percentual", percentual = 50m, ativo = true, diasDaSemana = new[] { amanha }, produtos = new[] { new { produtoId = chopp.Id } } });
        await CreatePromoOkAsync(client, t.Dono, new { nome = "Mais tarde", tipo = "percentual", percentual = 50m, ativo = true, horaInicio = Hora(local.AddHours(2)), horaFim = Hora(local.AddHours(3)), produtos = new[] { new { produtoId = chopp.Id } } });
        await CreatePromoOkAsync(client, t.Dono, PercentRule(outro.Id, 50m, "Outro produto"));
        await RunSqlAsync(OrgA, UnitA, $"INSERT INTO promocoes (id, organizacao_id, unidade_id, nome, tipo, percentual, ativo, dias_semana, versao, criado_por, criado_em, atualizado_por, atualizado_em) VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', '{UnitA2:D}', 'Outra unidade', 'percentual', 50, true, 127, 1, '{DonoA:D}', now(), '{DonoA:D}', now())");
        var outraUnidade = (await QueryAsync(OrgA, UnitA, "SELECT id FROM promocoes WHERE nome = 'Outra unidade'", r => r.GetGuid(0))).Single();
        await RunSqlAsync(OrgA, UnitA, $"INSERT INTO promocao_produtos (organizacao_id, promocao_id, produto_id, quantidade) VALUES ('{OrgA:D}', '{outraUnidade:D}', '{chopp.Id:D}', 1)");
        var comanda = await OpenAsync(client, t.Garcom);

        var nada = await LaunchManyAsync(client, t.Garcom, comanda.Id, comanda.Versao, (chopp.Id, 1));

        Assert.Equal(((long?)null, (Guid?)null), (nada.Itens[0].PromocaoCentavos, nada.Itens[0].PromocaoId));
        var semDesconto = await AccountAsync(client, t.Garcom, comanda.Id);
        Assert.Equal((0L, 1000L), (semDesconto.DescontoCentavos, semDesconto.ConsumoCentavos));
        Assert.Empty(await DiscountsAsync(client, t.Garcom, comanda.Id));

        // The same chopp now meets one rule that holds in every dimension: on, valid, today, inside the hours, this unit.
        var vigente = await CreatePromoOkAsync(client, t.Dono, new { nome = "Vigente", tipo = "valor", valorCentavos = 250, ativo = true, todasAsUnidades = false, validoDe = agora.AddHours(-1), validoAte = agora.AddHours(1), diasDaSemana = new[] { hoje, (hoje + 6) % 7 }, horaInicio = Hora(local.AddHours(-1)), horaFim = Hora(local.AddHours(1)), produtos = new[] { new { produtoId = chopp.Id } } });
        var aplicada = await LaunchManyAsync(client, t.Garcom, comanda.Id, nada.ComandaVersao, (chopp.Id, 2));

        Assert.Equal((vigente.Id, 500L), (aplicada.Itens[0].PromocaoId, aplicada.Itens[0].PromocaoCentavos));
    }

    [Fact]
    public async Task Repeating_the_last_round_judges_the_promotions_again_at_that_moment()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var regra = await CreatePromoOkAsync(client, t.Dono, PercentRule(chopp.Id, 10m));
        var comanda = await OpenAsync(client, t.Garcom);
        var primeira = await LaunchManyAsync(client, t.Garcom, comanda.Id, comanda.Versao, (chopp.Id, 1));
        Assert.Equal(100L, primeira.Itens[0].PromocaoCentavos);

        // The rule is turned off; the repeat is a new launch, so the promotion no longer applies.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/promocoes/{regra.Id:D}", t.Dono, new { versao = 1, nome = regra.Nome, percentual = 10m, todasAsUnidades = true, ativo = false })).StatusCode);
        var repetida = (await BodyAsync<RepetidaComPromocao>(await SendAsync(client, HttpMethod.Post, $"/comandas/{comanda.Id:D}/rodadas/repetir", t.Garcom, new { rodadaId = NewRoundId(), versao = primeira.ComandaVersao }), HttpStatusCode.Created)).Rodada;

        Assert.Equal([(long?)null], repetida.Itens.Select(i => i.PromocaoCentavos));
        var conta = await AccountAsync(client, t.Garcom, comanda.Id);
        Assert.Equal((2000L, 100L), (conta.ConsumoCentavos, conta.DescontoCentavos));
    }

    [Fact]
    public async Task The_rules_are_tenant_data_the_app_credential_cannot_delete_them_and_the_queries_and_model_match_the_database()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var chopp = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        _ = await CreatePromoOkAsync(client, t.Dono, PercentRule(chopp.Id));

        await Assert.ThrowsAnyAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, "DELETE FROM promocoes"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, "UPDATE promocao_produtos SET quantidade = 2"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, "DELETE FROM promocao_produtos"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE promocoes SET percentual = 0 WHERE organizacao_id = '{OrgA:D}'"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"INSERT INTO promocoes (id, organizacao_id, nome, tipo, percentual, ativo, versao, criado_por, criado_em, atualizado_por, atualizado_em) VALUES ('{Guid.NewGuid():D}', '{OrgB:D}', 'x', 'percentual', 10, true, 1, '{DonoA:D}', now(), '{DonoA:D}', now())"));
        Assert.Equal(1L, await CountAsync("promocoes"));

        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new MesasDbContext(tx);
        var lista = context.Promocoes.AsNoTracking().Where(p => (p.UnidadeId == null || p.UnidadeId == UnitA) && p.Ativo).ToQueryString();
        Assert.Contains("FROM promocoes AS", lista, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", lista, StringComparison.Ordinal);
        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
