using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0063: payment methods and the partial, split or mixed receipt through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions). The owner holds every permission
/// and plays the manager/cashier; waiters, the supervisor and small profiles are the denied cases. Comanda of the tests: R$ 12,50 x 2 and a 10% fee, so the bill is R$ 27,50.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record LinhaBody(Guid Id, Guid ComandaId, Guid? TurnoId, string Metodo, long RecebidoCentavos, long AplicadoCentavos, long TrocoCentavos, long SaldoRestanteCentavos, int ComandaVersao, DateTimeOffset RegistradoEm, Guid OperadorId, Guid? MetodoId, string MetodoNome, long GorjetaCentavos, string Estado, Guid? DivisaoId, int? DivisaoParte);

    private sealed record RecebimentoBody(List<LinhaBody> Pagamentos, ContaBody Conta);

    private sealed record MetodoBody(Guid Id, string? Chave, string Nome, bool Dinheiro, bool Ativo, int Ordem);

    private static object Linha(Guid metodo, long recebido, long? gorjeta = null, Guid? divisao = null, int? parte = null, Guid? id = null) =>
        new { pagamentoId = id ?? Guid.CreateVersion7(), metodoId = metodo, recebidoCentavos = recebido, gorjetaCentavos = gorjeta, divisaoId = divisao, divisaoParte = parte };

    private static Task<HttpResponseMessage> ReceiveAsync(HttpClient client, string? token, Guid comanda, int versao, params object[] linhas) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/recebimentos", token, new { versao, linhas });

    private static async Task<RecebimentoBody> ReceiveOkAsync(HttpClient client, string token, Guid comanda, int versao, params object[] linhas)
    {
        using var response = await ReceiveAsync(client, token, comanda, versao, linhas);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecebimentoBody>())!;
    }

    private static async Task<List<MetodoBody>> MethodsAsync(HttpClient client, string token) =>
        await BodyAsync<List<MetodoBody>>(await SendAsync(client, HttpMethod.Get, "/pagamentos/metodos", token), HttpStatusCode.OK);

    private static async Task<List<LinhaBody>> LinesOfAsync(HttpClient client, string token, Guid comanda) =>
        await BodyAsync<List<LinhaBody>>(await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/pagamentos", token), HttpStatusCode.OK);

    private static Guid MetodoId(List<MetodoBody> metodos, string chave) => metodos.Single(m => m.Chave == chave).Id;

    private async Task<string> GiveReceiverProfileAsync(Api api, Guid user, string nome, params string[] extras)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        string[] permissoes = ["comanda:ver", "comanda:ver-comandas-de-outros", "comanda:receber-pagamento", "pagamento:registrar", .. extras];
        var perfil = await perfis.CriarAsync(request, nome, [.. permissoes.Select(p => new RegraDeAcesso(RuleEffect.Allow, p))]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, user, perfil.Id!.Value));
        return await FreshTokenAsync(api, user);
    }

    [Fact]
    public async Task Factory_methods_exist_per_organization_can_be_renamed_deactivated_and_extended_but_voucher_and_credit_account_stay_off()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var fabrica = await MethodsAsync(client, t.Dono);
        Assert.Equal(["dinheiro", "credito", "debito", "pix", "vale-refeicao", "vale", "conta-credito"], fabrica.Select(m => m.Chave));
        Assert.Equal([true, true, true, true, true, false, false], fabrica.Select(m => m.Ativo));
        Assert.Equal(["dinheiro"], fabrica.Where(m => m.Dinheiro).Select(m => m.Chave));
        Assert.Equal(7L, await CountAsync("metodos_pagamento"));

        // Extend: a method of the organization, unique by name ignoring case; the factory names count too.
        var cheque = await BodyAsync<MetodoBody>(await SendAsync(client, HttpMethod.Post, "/pagamentos/metodos", t.Dono, new { nome = "  Cheque " }), HttpStatusCode.Created);
        Assert.Equal((null, "Cheque", false, true, 8), (cheque.Chave, cheque.Nome, cheque.Dinheiro, cheque.Ativo, cheque.Ordem));
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Post, "/pagamentos/metodos", t.Dono, new { nome = "CHEQUE" }), HttpStatusCode.Conflict, "ja-existe");
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Post, "/pagamentos/metodos", t.Dono, new { nome = "pix" }), HttpStatusCode.Conflict, "ja-existe");
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Post, "/pagamentos/metodos", t.Dono, new { nome = "   " }), HttpStatusCode.BadRequest, "invalido");
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Post, "/pagamentos/metodos", t.Dono, new { nome = new string('x', 41) }), HttpStatusCode.BadRequest, "invalido");

        // Rename, deactivate and reactivate a factory method; a name taken by another method is refused.
        var pix = MetodoId(fabrica, "pix");
        var renomeado = await BodyAsync<MetodoBody>(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{pix:D}", t.Dono, new { nome = "Pix QR", ativo = true }), HttpStatusCode.OK);
        Assert.Equal(("pix", "Pix QR"), (renomeado.Chave, renomeado.Nome));
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{MetodoId(fabrica, "credito"):D}", t.Dono, new { nome = "pix qr", ativo = true }), HttpStatusCode.Conflict, "ja-existe");
        var credito = MetodoId(fabrica, "credito");
        Assert.False((await BodyAsync<MetodoBody>(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{credito:D}", t.Dono, new { nome = "Crédito", ativo = false }), HttpStatusCode.OK)).Ativo);
        Assert.False((await MethodsAsync(client, t.Dono)).Single(m => m.Chave == "credito").Ativo);
        Assert.True((await BodyAsync<MetodoBody>(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{credito:D}", t.Dono, new { nome = "Crédito", ativo = true }), HttpStatusCode.OK)).Ativo);

        // Voucher and credit account cannot be turned on before phase 2B.
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{MetodoId(fabrica, "vale"):D}", t.Dono, new { nome = "Vale", ativo = true }), HttpStatusCode.Conflict, "indisponivel-na-fase");
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{MetodoId(fabrica, "conta-credito"):D}", t.Dono, new { nome = "Conta crédito", ativo = true }), HttpStatusCode.Conflict, "indisponivel-na-fase");
        Assert.Equal(2L, await CountAsync("metodos_pagamento WHERE NOT ativo"));
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{Guid.NewGuid():D}", t.Dono, new { nome = "X", ativo = true }), HttpStatusCode.NotFound, "nao-encontrado");
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{pix:D}", t.Dono, new { nome = "   ", ativo = true }), HttpStatusCode.BadRequest, "invalido");

        // Audited: one record per change that was made (1 created, 3 changed); the refused ones left nothing.
        Assert.Equal((1L, 3L), (await CountAsync("auditoria_registros WHERE acao = 'metodo-pagamento:criar'"), await CountAsync("auditoria_registros WHERE acao = 'metodo-pagamento:alterar'")));

        // Never deleted, and a custom method is never cash.
        var apagar = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, "DELETE FROM metodos_pagamento"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, apagar.SqlState);
        var dinheiro = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"INSERT INTO metodos_pagamento (id, organizacao_id, chave, nome, tipo, ativo, ordem, criado_em) VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', NULL, 'Outro dinheiro', 'dinheiro', true, 99, now())"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, dinheiro.SqlState);

        // The other organization has its own methods: none of the renames or additions of A is there.
        var deB = await MethodsAsync(client, t.DonoB);
        Assert.Equal(7, deB.Count);
        Assert.Contains(deB, m => m.Nome == "Pix");
        Assert.DoesNotContain(deB, m => m.Nome is "Pix QR" or "Cheque");
        Assert.Equal(7L, (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM metodos_pagamento", r => r.GetInt64(0))).Single());
        await AssertMethodErrorAsync(await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{pix:D}", t.DonoB, new { nome = "Roubado", ativo = false }), HttpStatusCode.NotFound, "nao-encontrado");
        Assert.Equal("Pix QR", (await MethodsAsync(client, t.Dono)).Single(m => m.Chave == "pix").Nome);
    }

    [Fact]
    public async Task Listing_needs_pagamento_ver_and_changing_needs_metodo_pagamento_gerenciar_and_nobody_without_them_reaches_the_methods()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var metodos = await MethodsAsync(client, t.Dono);
        var pix = MetodoId(metodos, "pix");
        var leitor = await GiveReceiverProfileAsync(api, Mudo, "Leitor de metodos", "pagamento:ver");

        foreach (var (metodo, caminho, corpo) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/pagamentos/metodos", new { nome = "Novo" }),
            (HttpMethod.Put, $"/pagamentos/metodos/{pix:D}", new { nome = "Novo", ativo = false }),
        })
        {
            foreach (var token in new[] { t.Garcom, t.Supervisor, leitor })
            {
                using var negada = await SendAsync(client, metodo, caminho, token, corpo);
                Assert.Equal(HttpStatusCode.Forbidden, negada.StatusCode);
                Assert.Equal(string.Empty, await negada.Content.ReadAsStringAsync());
            }

            using var anonima = await SendAsync(client, metodo, caminho, null, corpo);
            Assert.Equal(HttpStatusCode.Unauthorized, anonima.StatusCode);
        }

        using var semVer = await SendAsync(client, HttpMethod.Get, "/pagamentos/metodos", t.Garcom);
        Assert.Equal(HttpStatusCode.Forbidden, semVer.StatusCode);
        using var anonimaLista = await SendAsync(client, HttpMethod.Get, "/pagamentos/metodos", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonimaLista.StatusCode);
        Assert.Equal(7, (await MethodsAsync(client, leitor)).Count);
        Assert.Equal((0L, 0L), (await CountAsync("auditoria_registros WHERE acao LIKE 'metodo-pagamento:%'"), await CountAsync("metodos_pagamento WHERE nome = 'Novo'")));
    }

    [Fact]
    public async Task A_mixed_receipt_with_card_pix_and_cash_is_one_step_the_cash_alone_enters_the_shift_and_the_tip_stays_outside_the_balance()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 10_000);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2);
        var m = await MethodsAsync(client, t.Dono);
        var linhas = new[] { Linha(MetodoId(m, "credito"), 1000, 200), Linha(MetodoId(m, "pix"), 750), Linha(MetodoId(m, "dinheiro"), 2000) };

        using var response = await ReceiveAsync(client, t.Dono, comanda, versao, linhas);
        var r = await BodyAsync<RecebimentoBody>(response, HttpStatusCode.Created);

        Assert.Equal(["credito", "pix", "dinheiro"], r.Pagamentos.Select(p => p.Metodo));
        Assert.Equal(["Crédito", "Pix", "Dinheiro"], r.Pagamentos.Select(p => p.MetodoNome));
        Assert.Equal([1000L, 750L, 2000L], r.Pagamentos.Select(p => p.RecebidoCentavos));
        Assert.Equal([1000L, 750L, 1000L], r.Pagamentos.Select(p => p.AplicadoCentavos));
        Assert.Equal([0L, 0L, 1000L], r.Pagamentos.Select(p => p.TrocoCentavos));
        Assert.Equal([1750L, 1000L, 0L], r.Pagamentos.Select(p => p.SaldoRestanteCentavos));
        Assert.Equal([200L, 0L, 0L], r.Pagamentos.Select(p => p.GorjetaCentavos));
        Assert.All(r.Pagamentos, p => Assert.Equal(("confirmado", versao + 1, DonoA), (p.Estado, p.ComandaVersao, p.OperadorId)));
        Assert.Equal((2750L, 0L, 2750L, versao + 1), (r.Conta.TotalCentavos, r.Conta.SaldoCentavos, r.Conta.PagoCentavos, r.Conta.Versao));

        // One unit of work: one version bump, three rows, three audit records, three events carrying method and tip.
        Assert.Equal(versao + 1, await VersionOfAsync(comanda));
        Assert.Equal((3L, 2750L, 200L), (await CountAsync("pagamentos"), (await QueryAsync(OrgA, UnitA, "SELECT sum(aplicado_centavos) FROM pagamentos", x => x.GetInt64(0))).Single(), (await QueryAsync(OrgA, UnitA, "SELECT sum(gorjeta_centavos) FROM pagamentos", x => x.GetInt64(0))).Single()));
        Assert.Equal(3L, await CountAsync("auditoria_registros WHERE acao = 'pagamento:registrar'"));
        Assert.Equal(3L, await CountAsync("outbox_eventos WHERE tipo = 'pagamento.registrado'"));
        Assert.Equal(1L, await CountAsync("outbox_eventos WHERE tipo = 'pagamento.registrado' AND payload->>'Metodo' = 'credito' AND payload->>'GorjetaCentavos' = '200'"));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'pagamento:registrar' AND depois->>'metodo' = 'pix' AND depois->>'metodoNome' = 'Pix'"));

        // The same ids again are the same receipt: 200, the very same lines, no new row, audit, event or movement.
        var repetida = await BodyAsync<RecebimentoBody>(await ReceiveAsync(client, t.Dono, comanda, versao, linhas), HttpStatusCode.OK);
        Assert.Equal(r.Pagamentos.Select(p => (p.Id, p.RegistradoEm, p.AplicadoCentavos)), repetida.Pagamentos.Select(p => (p.Id, p.RegistradoEm, p.AplicadoCentavos)));
        Assert.Equal((3L, 3L, 3L), (await CountAsync("pagamentos"), await CountAsync("auditoria_registros WHERE acao = 'pagamento:registrar'"), await CountAsync("outbox_eventos WHERE tipo = 'pagamento.registrado'")));

        // Closing still needs a zero balance (it is zero), and the shift expects the float plus the CASH applied only: not the card, not the Pix, not the change, not the tip.
        using var fechada = await CloseAsync(client, t.Dono, comanda, versao + 1);
        Assert.Equal(HttpStatusCode.OK, fechada.StatusCode);
        using var fechamento = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { contadoCentavos = 11_000 });
        var turno = (await fechamento.Content.ReadFromJsonAsync<TurnoBody>())!;
        Assert.Equal((11_000L, 1000L, 0L), (turno.EsperadoCentavos, turno.RecebidoDinheiroCentavos, turno.DiferencaCentavos));
        Assert.Equal(3, (await LinesOfAsync(client, t.Dono, comanda)).Count);
    }

    [Fact]
    public async Task Change_is_only_for_cash_other_methods_never_pass_the_balance_and_a_partial_payment_keeps_the_comanda_open()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2);
        var m = await MethodsAsync(client, t.Dono);
        var (credito, pix, dinheiro) = (MetodoId(m, "credito"), MetodoId(m, "pix"), MetodoId(m, "dinheiro"));

        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, comanda, versao, Linha(pix, 3000)), HttpStatusCode.Conflict, "excede-saldo");
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, comanda, versao, Linha(credito, 1000), Linha(pix, 1750), Linha(dinheiro, 500)), HttpStatusCode.Conflict, "excede-saldo");
        Assert.Equal((0L, versao), (await CountAsync("pagamentos"), await VersionOfAsync(comanda)));
        Assert.Equal(0L, await CountAsync("turnos_caixa WHERE recebido_dinheiro_centavos <> 0"));

        // A partial payment with a non-cash method: the comanda stays open and cannot be closed.
        var parcial = await ReceiveOkAsync(client, t.Dono, comanda, versao, Linha(pix, 1000));
        Assert.Equal((1750L, 2750L - 1750L), (parcial.Conta.SaldoCentavos, parcial.Conta.PagoCentavos));
        await AssertErrorAsync(await CloseAsync(client, t.Dono, comanda, parcial.Conta.Versao), HttpStatusCode.Conflict, "saldo-pendente");
        Assert.Equal(0L, await CountAsync("turnos_caixa WHERE recebido_dinheiro_centavos <> 0"));

        // Cash above the rest applies the rest and gives the change; then it closes.
        var resto = await ReceiveOkAsync(client, t.Dono, comanda, parcial.Conta.Versao, Linha(dinheiro, 5000));
        Assert.Equal((5000L, 1750L, 3250L, 0L), (resto.Pagamentos.Single().RecebidoCentavos, resto.Pagamentos.Single().AplicadoCentavos, resto.Pagamentos.Single().TrocoCentavos, resto.Conta.SaldoCentavos));
        using var fechada = await CloseAsync(client, t.Dono, comanda, resto.Conta.Versao);
        Assert.Equal(HttpStatusCode.OK, fechada.StatusCode);
        Assert.Equal(1L, await CountAsync("turnos_caixa WHERE recebido_dinheiro_centavos = 1750"));

        // The database refuses change on a method that is not cash, and a repeat balance is gone.
        var troco = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, "INSERT INTO pagamentos (id, organizacao_id, unidade_id, comanda_id, turno_id, metodo, recebido_centavos, aplicado_centavos, troco_centavos, operador_id, registrado_em, saldo_apos_centavos, comanda_versao, metodo_id, metodo_nome, gorjeta_centavos, estado) SELECT gen_random_uuid(), organizacao_id, unidade_id, comanda_id, turno_id, 'pix', recebido_centavos, aplicado_centavos, troco_centavos, operador_id, registrado_em, saldo_apos_centavos, comanda_versao, metodo_id, 'Pix', 0, 'confirmado' FROM pagamentos WHERE troco_centavos > 0"));
        Assert.Equal("pagamentos_troco_so_dinheiro", troco.ConstraintName);
    }

    [Fact]
    public async Task A_deactivated_unknown_or_foreign_method_is_refused_and_nothing_is_written()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2);
        var m = await MethodsAsync(client, t.Dono);
        var deB = await MethodsAsync(client, t.DonoB);
        var (credito, dinheiro) = (MetodoId(m, "credito"), MetodoId(m, "dinheiro"));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{credito:D}", t.Dono, new { nome = "Crédito", ativo = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{MetodoId(m, "vale-refeicao"):D}", t.Dono, new { nome = "Vale-refeição", ativo = false })).StatusCode);

        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, comanda, versao, Linha(dinheiro, 500), Linha(credito, 500)), HttpStatusCode.Conflict, "metodo-inativo");
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, comanda, versao, Linha(MetodoId(m, "vale"), 500)), HttpStatusCode.Conflict, "metodo-inativo");
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, comanda, versao, Linha(Guid.NewGuid(), 500)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, comanda, versao, Linha(MetodoId(deB, "pix"), 500)), HttpStatusCode.BadRequest, "invalido");
        Assert.Equal((0L, versao), (await CountAsync("pagamentos"), await VersionOfAsync(comanda)));

        // The cash payment of the original route follows the same rule: with cash turned off it is refused, with it on it still works and records the cash method.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{dinheiro:D}", t.Dono, new { nome = "Dinheiro", ativo = false })).StatusCode);
        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda, 500, versao), HttpStatusCode.Conflict, "metodo-inativo");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/pagamentos/metodos/{dinheiro:D}", t.Dono, new { nome = "Dinheiro", ativo = true })).StatusCode);
        var pago = await PayOkAsync(client, t.Dono, comanda, 500, versao);
        Assert.Equal(("dinheiro", 500L), (pago.Metodo, pago.AplicadoCentavos));
        Assert.Equal(1L, await CountAsync("pagamentos WHERE metodo = 'dinheiro' AND metodo_nome = 'Dinheiro' AND gorjeta_centavos = 0 AND estado = 'confirmado' AND divisao_id IS NULL"));
    }

    [Fact]
    public async Task Receiving_needs_registrar_and_more_than_one_line_or_a_split_part_needs_dividir_and_a_tip_needs_registrar_gorjeta()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        // The shift belongs to one operator: with the rule off, the small profiles (who have no shift of their own) can receive, which is what this test is about.
        Assert.Equal(ResultadoConfiguracao.Ok, await api.Services.GetRequiredService<Configuracoes>().AlterarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), new AlterarConfiguracao(Flow4Tech.Caixa.Application.CaixaSettings.ExigirCaixaAberto.Key, CamadaDeEscrita.Organizacao, "false", null, null, null)));
        var basico = await GiveReceiverProfileAsync(api, Mudo, "Recebe");
        var divide = await GiveReceiverProfileAsync(api, Supervisor, "Recebe e divide", "pagamento:dividir");
        var gorjeta = await GiveReceiverProfileAsync(api, Garcom2, "Recebe com gorjeta", "pagamento:registrar-gorjeta");
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2);
        var m = await MethodsAsync(client, t.Dono);
        var (credito, dinheiro) = (MetodoId(m, "credito"), MetodoId(m, "dinheiro"));

        async Task AssertDeniedAsync(string token, params object[] linhas)
        {
            using var negada = await ReceiveAsync(client, token, comanda, versao, linhas);
            Assert.Equal(HttpStatusCode.Forbidden, negada.StatusCode);
            Assert.Equal(string.Empty, await negada.Content.ReadAsStringAsync());
        }

        // Without the permission of the route, without pagamento:registrar, anonymous, and from another organization.
        await AssertDeniedAsync(t.Garcom, Linha(dinheiro, 100));
        using var anonima = await ReceiveAsync(client, null, comanda, versao, Linha(dinheiro, 100));
        Assert.Equal(HttpStatusCode.Unauthorized, anonima.StatusCode);
        await AssertErrorAsync(await ReceiveAsync(client, t.DonoB, comanda, versao, Linha(dinheiro, 100)), HttpStatusCode.NotFound, "nao-encontrada");

        // Basic receiver: a single line without tip only.
        await AssertDeniedAsync(basico, Linha(credito, 100), Linha(dinheiro, 100));
        await AssertDeniedAsync(basico, Linha(dinheiro, 100, 10));
        await AssertDeniedAsync(basico, Linha(dinheiro, 100, divisao: Guid.NewGuid(), parte: 1));
        // dividir does not give the tip, and the tip does not give a second line.
        await AssertDeniedAsync(divide, Linha(credito, 100, 5), Linha(dinheiro, 100));
        await AssertDeniedAsync(gorjeta, Linha(credito, 100), Linha(dinheiro, 100));
        Assert.Equal((0L, versao), (await CountAsync("pagamentos"), await VersionOfAsync(comanda)));

        var um = await ReceiveOkAsync(client, basico, comanda, versao, Linha(credito, 100));
        var dois = await ReceiveOkAsync(client, divide, comanda, um.Conta.Versao, Linha(credito, 100), Linha(dinheiro, 100));
        var tres = await ReceiveOkAsync(client, gorjeta, comanda, dois.Conta.Versao, Linha(credito, 100, 25));
        Assert.Equal((1L, 2L, 1L), (um.Pagamentos.Count, dois.Pagamentos.Count, tres.Pagamentos.Count));
        Assert.Equal(25L, tres.Pagamentos.Single().GorjetaCentavos);
        Assert.Equal(4L, await CountAsync("pagamentos"));
    }

    [Fact]
    public async Task A_part_of_a_recorded_split_is_paid_by_its_own_lines_never_past_what_it_owes_and_a_changed_comanda_voids_the_link()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var m = await MethodsAsync(client, t.Dono);
        var (credito, pix, dinheiro) = (MetodoId(m, "credito"), MetodoId(m, "pix"), MetodoId(m, "dinheiro"));
        var c = await Bill3Async(client, t, "RM1");
        var divisaoId = Guid.CreateVersion7();
        var divisao = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, divisaoId, c.Versao, Equal(3)), HttpStatusCode.Created);
        Assert.Equal([1100L, 1100L, 1100L], divisao.Partes.Select(p => p.TotalCentavos));
        var versao = c.Versao + 1;

        // Link shapes that cannot mean anything are refused before any work: a split id without a part, a part without a split, part zero.
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, c.ComandaId, versao, Linha(pix, 100, divisao: divisaoId)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, c.ComandaId, versao, Linha(pix, 100, parte: 1)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, c.ComandaId, versao, Linha(pix, 100, divisao: divisaoId, parte: 0)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, c.ComandaId, versao, Linha(pix, 100, divisao: divisaoId, parte: 9)), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, c.ComandaId, versao, Linha(pix, 100, divisao: Guid.CreateVersion7(), parte: 1)), HttpStatusCode.BadRequest, "invalido");

        // One receipt pays one part: lines pointing to different parts are refused.
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, c.ComandaId, versao, Linha(pix, 100, divisao: divisaoId, parte: 1), Linha(credito, 100, divisao: divisaoId, parte: 2)), HttpStatusCode.BadRequest, "invalido");

        // Part 1 in full by Pix; one more cent on it is refused even though the comanda still has a balance.
        var p1 = await ReceiveOkAsync(client, t.Dono, c.ComandaId, versao, Linha(pix, 1100, divisao: divisaoId, parte: 1));
        Assert.Equal((divisaoId, 1), (p1.Pagamentos.Single().DivisaoId, p1.Pagamentos.Single().DivisaoParte));
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, c.ComandaId, p1.Conta.Versao, Linha(pix, 1, divisao: divisaoId, parte: 1)), HttpStatusCode.Conflict, "excede-parte");

        // Part 2 by cash with a tip: it applies its 1100 and the rest is change; the tip is outside.
        var p2 = await ReceiveOkAsync(client, t.Dono, c.ComandaId, p1.Conta.Versao, Linha(dinheiro, 2000, 50, divisao: divisaoId, parte: 2));
        Assert.Equal((1100L, 900L, 50L), (p2.Pagamentos.Single().AplicadoCentavos, p2.Pagamentos.Single().TrocoCentavos, p2.Pagamentos.Single().GorjetaCentavos));
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, c.ComandaId, p2.Conta.Versao, Linha(dinheiro, 1, divisao: divisaoId, parte: 2)), HttpStatusCode.Conflict, "excede-parte");

        // Paying parts does not void the split (it still matches the comanda), but a comanda that received payments cannot be split again (the F4-0061 rule stays).
        var vigente = await BodyAsync<DivisaoBody>(await GetSplitAsync(client, t.Dono, c.ComandaId), HttpStatusCode.OK);
        Assert.True(vigente.Vigente);
        await AssertErrorAsync(await SplitAsync(client, t.Dono, c.ComandaId, Guid.CreateVersion7(), p2.Conta.Versao, Equal(2)), HttpStatusCode.Conflict, "valor-ja-recebido");

        // Part 3 by two methods in one receipt: together exactly its share; then the comanda closes.
        var p3 = await ReceiveOkAsync(client, t.Dono, c.ComandaId, p2.Conta.Versao, Linha(credito, 600, divisao: divisaoId, parte: 3), Linha(dinheiro, 1000, divisao: divisaoId, parte: 3));
        Assert.Equal([600L, 500L], p3.Pagamentos.Select(p => p.AplicadoCentavos));
        Assert.Equal(0L, p3.Conta.SaldoCentavos);
        using var fechada = await CloseAsync(client, t.Dono, c.ComandaId, p3.Conta.Versao);
        Assert.Equal(HttpStatusCode.OK, fechada.StatusCode);
        Assert.Equal(4L, await CountAsync($"pagamentos WHERE divisao_id = '{divisaoId:D}'"));

        // A comanda changed AFTER its split: the link is void (409), a payment without the link still works.
        var d = await Bill3Async(client, t, "RM2");
        var divisaoD = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await SplitAsync(client, t.Dono, d.ComandaId, divisaoD, d.Versao, Equal(3))).StatusCode);
        var lancado = await LaunchOkAsync(client, t.Garcom, d.ComandaId, d.Versao + 1, d.ProdutoId);
        await AssertErrorAsync(await ReceiveAsync(client, t.Dono, d.ComandaId, lancado.ComandaVersao, Linha(pix, 1100, divisao: divisaoD, parte: 1)), HttpStatusCode.Conflict, "divisao-invalida");
        await AssertErrorAsync(await GetSplitAsync(client, t.Dono, d.ComandaId), HttpStatusCode.Conflict, "divisao-invalida");
        Assert.Equal(0L, await CountAsync($"pagamentos WHERE comanda_id = '{d.ComandaId:D}'"));
        Assert.Equal(HttpStatusCode.Created, (await ReceiveAsync(client, t.Dono, d.ComandaId, lancado.ComandaVersao, Linha(pix, 1100))).StatusCode);
    }

    [Fact]
    public async Task Two_receipts_at_once_on_one_comanda_never_apply_more_than_the_balance()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var m = await MethodsAsync(client, t.Dono);
        var (pix, dinheiro) = (MetodoId(m, "pix"), MetodoId(m, "dinheiro"));

        for (var volta = 0; volta < 5; volta++)
        {
            var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2, "CONC" + volta);
            var gate = new TaskCompletionSource();

            async Task<HttpResponseMessage> EnviarAsync(params object[] linhas)
            {
                await gate.Task;
                return await ReceiveAsync(client, t.Dono, comanda, versao, linhas);
            }

            // Both want 20,00 against the same version of a 27,50 bill: one wins, the other is stale and nothing of it is written.
            var a = EnviarAsync(Linha(dinheiro, 2000));
            var b = EnviarAsync(Linha(pix, 1000), Linha(dinheiro, 1000));
            gate.SetResult();
            var (ra, rb) = (await a, await b);
            using var _ = ra;
            using var __ = rb;
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], new[] { ra.StatusCode, rb.StatusCode }.Order());
            var aplicado = (await QueryAsync(OrgA, UnitA, $"SELECT coalesce(sum(aplicado_centavos), 0) FROM pagamentos WHERE comanda_id = '{comanda:D}'", x => x.GetInt64(0))).Single();
            Assert.InRange(aplicado, 1, 2750);
            Assert.Equal(versao + 1, await VersionOfAsync(comanda));

            // The loser retries on the new version: what it applies is capped by what is left, so the total is the bill and no more.
            var nova = await BodyAsync<ContaBody>(await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/conta", t.Dono), HttpStatusCode.OK);
            using var retry = await ReceiveAsync(client, t.Dono, comanda, nova.Versao, Linha(dinheiro, 5000));
            Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
            Assert.Equal(2750L, (await QueryAsync(OrgA, UnitA, $"SELECT sum(aplicado_centavos) FROM pagamentos WHERE comanda_id = '{comanda:D}'", x => x.GetInt64(0))).Single());
            await AssertErrorAsync(await ReceiveAsync(client, t.Dono, comanda, nova.Versao + 1, Linha(dinheiro, 100)), HttpStatusCode.Conflict, "sem-saldo");
        }

        // The same lines sent twice at once are recorded once (the one that loses the lock still gets the stored receipt).
        var (c2, v2, _) = await ComandaWithConsumoAsync(client, t, 1250, 2, "CONCR");
        var linhas = new[] { Linha(pix, 1000), Linha(dinheiro, 2000) };
        var gate2 = new TaskCompletionSource();

        async Task<HttpResponseMessage> RepetirAsync()
        {
            await gate2.Task;
            return await ReceiveAsync(client, t.Dono, c2, v2, linhas);
        }

        var x1 = RepetirAsync();
        var x2 = RepetirAsync();
        gate2.SetResult();
        var (rx, ry) = (await x1, await x2);
        using var ___ = rx;
        using var ____ = ry;
        Assert.All(new[] { rx.StatusCode, ry.StatusCode }, s => Assert.True(s is HttpStatusCode.Created or HttpStatusCode.OK, s.ToString()));
        Assert.Equal((2L, v2 + 1), (await CountAsync($"pagamentos WHERE comanda_id = '{c2:D}'"), await VersionOfAsync(c2)));
    }

    [Fact]
    public async Task A_pending_payment_exists_in_the_model_and_does_not_count_in_the_balance_nothing_sets_it_yet()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2);
        var pago = await PayOkAsync(client, t.Dono, comanda, 1000, versao);
        Assert.Equal(1L, await CountAsync("pagamentos WHERE estado = 'confirmado'"));

        await RunSqlAsync(OrgA, UnitA, "INSERT INTO pagamentos (id, organizacao_id, unidade_id, comanda_id, turno_id, metodo, recebido_centavos, aplicado_centavos, troco_centavos, operador_id, registrado_em, saldo_apos_centavos, comanda_versao, metodo_id, metodo_nome, gorjeta_centavos, estado) " +
            $"SELECT '{Guid.CreateVersion7():D}', organizacao_id, unidade_id, comanda_id, turno_id, 'pix', 500, 500, 0, operador_id, now(), 1250, comanda_versao, metodo_id, 'Pix', 0, 'pendente' FROM pagamentos WHERE id = '{pago.Id:D}'");

        var conta = await BillAsync(client, t.Dono, comanda);
        Assert.Equal((1000L, 1750L), (conta.PagoCentavos, conta.SaldoCentavos));
        var lista = await LinesOfAsync(client, t.Dono, comanda);
        Assert.Equal(["confirmado", "pendente"], lista.Select(p => p.Estado).Order());

        // The only provider is manual: it confirms at once, so no route produces a pending payment.
        var nova = await ReceiveOkAsync(client, t.Dono, comanda, versao + 1, Linha(MetodoId(await MethodsAsync(client, t.Dono), "pix"), 500));
        Assert.Equal("confirmado", nova.Pagamentos.Single().Estado);
        Assert.Equal(1L, await CountAsync("pagamentos WHERE estado = 'pendente'"));
    }

    private static async Task AssertMethodErrorAsync(HttpResponseMessage response, HttpStatusCode status, string codigo)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(codigo, (await response.Content.ReadFromJsonAsync<Erro>())!.Codigo);
    }
}
