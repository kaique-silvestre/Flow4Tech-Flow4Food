using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Pagamentos.Application;
using Flow4Tech.Pagamentos.Infrastructure;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Flow4Tech.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// Dominio do salao, ticket 07: receiving cash, the service fee and closing, through the real authenticated routes (same fixture as the comandas: real PostgreSQL, application credential with RLS,
/// real sessions). The owner plays the cashier (it holds every permission); waiters and the supervisor are the denied cases. Covers partial payment and change, the shift's expected cash, the
/// option that requires an open shift, idempotent repeats, the close rule, the fee and its removal, immutability of payments, optimistic versions, concurrency, isolation and rollback.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record PagamentoBody(Guid Id, Guid ComandaId, Guid? TurnoId, string Metodo, long RecebidoCentavos, long AplicadoCentavos, long TrocoCentavos, long SaldoRestanteCentavos, int ComandaVersao, DateTimeOffset RegistradoEm, Guid OperadorId);

    private sealed record ContaBody(long ConsumoCentavos, long TaxaServicoCentavos, long TotalCentavos, long PagoCentavos, long SaldoCentavos, bool TaxaServicoRemovida, decimal TaxaServicoPercentual, int Versao, string Estado);

    private sealed record TurnoBody(Guid Id, long FundoCentavos, long? ContadoCentavos, long? EsperadoCentavos, long? DiferencaCentavos, long RecebidoDinheiroCentavos);

    private static Task<HttpResponseMessage> PayAsync(HttpClient client, string token, Guid comanda, long recebido, int versao, string? pagamentoId = null) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/pagamentos", token, new { pagamentoId = pagamentoId ?? NewRoundId(), recebidoCentavos = recebido, versao });

    private static async Task<PagamentoBody> PayOkAsync(HttpClient client, string token, Guid comanda, long recebido, int versao)
    {
        using var response = await PayAsync(client, token, comanda, recebido, versao);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagamentoBody>())!;
    }

    private static async Task<ContaBody> BillAsync(HttpClient client, string token, Guid comanda)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/conta", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ContaBody>())!;
    }

    private static async Task<TurnoBody> OpenShiftAsync(HttpClient client, string token, long fundo)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/caixa/turnos", token, new { fundoCentavos = fundo });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TurnoBody>())!;
    }

    private static Task<HttpResponseMessage> CloseAsync(HttpClient client, string token, Guid comanda, int versao) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/fechar", token, new { versao });

    private static Task<HttpResponseMessage> RemoveFeeAsync(HttpClient client, string token, Guid comanda, int versao) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/taxa-servico/remover", token, new { versao });

    /// <summary>A comanda of the waiter holding <paramref name="quantidade"/> units of a product at <paramref name="preco"/> cents; returns the comanda id and its current version.</summary>
    private static async Task<(Guid Id, int Versao, ProdutoBody Produto)> ComandaWithConsumoAsync(HttpClient client, Tokens t, long preco, int quantidade, string codigo = "CHOPP")
    {
        var produto = await CreateProductAsync(client, t.Dono, codigo, "Produto " + codigo, preco);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, quantidade);
        return (comanda.Id, rodada.ComandaVersao, produto);
    }

    [Fact]
    public async Task Partial_cash_then_the_rest_with_change_closes_the_comanda_and_the_applied_cash_enters_the_expected_of_the_shift()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var turno = await OpenShiftAsync(client, t.Dono, 10_000);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2);

        var conta = await BillAsync(client, t.Dono, comanda);
        Assert.Equal((2500L, 250L, 2750L, 0L, 2750L, false, 10m, versao, "aberta"), (conta.ConsumoCentavos, conta.TaxaServicoCentavos, conta.TotalCentavos, conta.PagoCentavos, conta.SaldoCentavos, conta.TaxaServicoRemovida, conta.TaxaServicoPercentual, conta.Versao, conta.Estado));

        // Partial: the response says what remains.
        var parcial = await PayOkAsync(client, t.Dono, comanda, 1000, versao);
        Assert.Equal(("dinheiro", 1000L, 1000L, 0L, 1750L, versao + 1, (Guid?)turno.Id, DonoA), (parcial.Metodo, parcial.RecebidoCentavos, parcial.AplicadoCentavos, parcial.TrocoCentavos, parcial.SaldoRestanteCentavos, parcial.ComandaVersao, parcial.TurnoId, parcial.OperadorId));

        // Close with a balance is refused; paying more than the balance applies only the balance and gives the change back.
        await AssertErrorAsync(await CloseAsync(client, t.Dono, comanda, parcial.ComandaVersao), HttpStatusCode.Conflict, "saldo-pendente");
        var resto = await PayOkAsync(client, t.Dono, comanda, 2000, parcial.ComandaVersao);
        Assert.Equal((2000L, 1750L, 250L, 0L), (resto.RecebidoCentavos, resto.AplicadoCentavos, resto.TrocoCentavos, resto.SaldoRestanteCentavos));
        Assert.Equal((2750L, 0L), ((await BillAsync(client, t.Dono, comanda)).PagoCentavos, (await BillAsync(client, t.Dono, comanda)).SaldoCentavos));

        using var closed = await CloseAsync(client, t.Dono, comanda, resto.ComandaVersao);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var final = (await closed.Content.ReadFromJsonAsync<ContaBody>())!;
        Assert.Equal(("fechada", resto.ComandaVersao + 1), (final.Estado, final.Versao));
        Assert.Equal(("fechada", true), (await QueryAsync(OrgA, UnitA, $"SELECT estado, fechada_em IS NOT NULL FROM comandas WHERE id = '{comanda:D}'", r => (r.GetString(0), r.GetBoolean(1)))).Single());

        // The change never enters the drawer: expected = float + applied.
        using var fechamento = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { contadoCentavos = 12_750 });
        Assert.Equal(HttpStatusCode.OK, fechamento.StatusCode);
        var turnoFechado = (await fechamento.Content.ReadFromJsonAsync<TurnoBody>())!;
        Assert.Equal((12_750L, 2750L, 0L), (turnoFechado.EsperadoCentavos, turnoFechado.RecebidoDinheiroCentavos, turnoFechado.DiferencaCentavos));

        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'pagamento:registrar'"));
        Assert.Equal(2L, await CountAsync("outbox_eventos WHERE tipo = 'pagamento.registrado'"));
        Assert.Equal(1L, await CountAsync($"auditoria_registros WHERE acao = 'comanda:fechar' AND objeto_id = '{comanda:D}'"));
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'comanda.fechada' AND payload->>'ComandaId' = '{comanda:D}'"));
        var pagamentos = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/pagamentos", t.Dono);
        Assert.Equal(2, (await pagamentos.Content.ReadFromJsonAsync<List<PagamentoBody>>())!.Count);
    }

    [Fact]
    public async Task A_closed_comanda_takes_no_more_items_payments_cancellations_or_fee_changes()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, produto) = await ComandaWithConsumoAsync(client, t, 1000, 1);
        var pago = await PayOkAsync(client, t.Dono, comanda, 1100, versao);
        using var closed = await CloseAsync(client, t.Dono, comanda, pago.ComandaVersao);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var item = (await ItemsAsync(client, t.Dono, comanda)).Itens.Single();
        var versaoFinal = pago.ComandaVersao + 1;

        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, comanda, versaoFinal, produto.Id), HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda, 100, versaoFinal), HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertErrorAsync(await CancelAsync(client, t.Garcom, comanda, item.Id, versaoFinal, "cliente-desistiu"), HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertErrorAsync(await RemoveFeeAsync(client, t.Dono, comanda, versaoFinal), HttpStatusCode.Conflict, "comanda-nao-aberta");
        await AssertErrorAsync(await CloseAsync(client, t.Dono, comanda, versaoFinal), HttpStatusCode.Conflict, "comanda-nao-aberta");
        Assert.Equal((1L, versaoFinal), (await CountAsync("pagamentos"), await VersionOfAsync(comanda)));
    }

    [Fact]
    public async Task Repeating_the_same_payment_id_one_to_five_times_leaves_one_payment_the_same_response_and_the_shift_counted_once()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 5000);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);
        var pagamentoId = NewRoundId();

        var bodies = new List<string>();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            // The client never learned the answer, so it resends the same version: the repeat must not turn into a 409.
            using var response = await PayAsync(client, t.Dono, comanda, 500, versao, pagamentoId);
            statuses.Add(response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK], statuses);
        Assert.Single(bodies.Distinct());
        Assert.Equal((1L, versao + 1, 500L), (await CountAsync("pagamentos"), await VersionOfAsync(comanda), (await QueryAsync(OrgA, UnitA, "SELECT recebido_dinheiro_centavos FROM turnos_caixa", r => r.GetInt64(0))).Single()));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'pagamento:registrar'"));
        Assert.Equal(1L, await CountAsync("outbox_eventos WHERE tipo = 'pagamento.registrado'"));

        // The same id on ANOTHER comanda is not a repeat: refused, nothing written.
        var outra = await OpenAsync(client, t.Garcom);
        await AssertErrorAsync(await PayAsync(client, t.Dono, outra.Id, 500, outra.Versao, pagamentoId), HttpStatusCode.BadRequest, "invalido");
        Assert.Equal(1L, await CountAsync("pagamentos"));
    }

    [Fact]
    public async Task With_the_option_on_cash_needs_the_operators_open_shift_and_with_it_off_the_payment_has_no_shift_and_counts_in_none()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2);

        // On (the default) and no shift: refused, nothing changes.
        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda, 500, versao), HttpStatusCode.Conflict, "caixa-fechado");
        Assert.Equal((0L, versao), (await CountAsync("pagamentos"), await VersionOfAsync(comanda)));

        // Off for the organization: the payment is accepted with no shift, and no shift's expected includes it.
        var configuracoes = api.Services.GetRequiredService<Configuracoes>();
        Assert.Equal(ResultadoConfiguracao.Ok, await configuracoes.AlterarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), new AlterarConfiguracao(Flow4Tech.Caixa.Application.CaixaSettings.ExigirCaixaAberto.Key, CamadaDeEscrita.Organizacao, "false", null, null, null)));
        var shiftOfDono = await OpenShiftAsync(client, t.Dono, 1000);
        var pago = await PayOkAsync(client, t.Dono, comanda, 500, versao);

        Assert.Null(pago.TurnoId);
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, $"SELECT recebido_dinheiro_centavos FROM turnos_caixa WHERE id = '{shiftOfDono.Id:D}'", r => r.GetInt64(0))).Single());
        Assert.Null((await QueryAsync(OrgA, UnitA, $"SELECT turno_id FROM pagamentos WHERE id = '{pago.Id:D}'", r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0))).Single());
    }

    [Fact]
    public async Task The_service_fee_rounds_half_up_and_is_included_and_removing_it_needs_its_permission_and_is_audited()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        // 10% of 1005 cents is 100.5: half up gives 101.
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1005, 1);
        var conta = await BillAsync(client, t.Dono, comanda);
        Assert.Equal((1005L, 101L, 1106L), (conta.ConsumoCentavos, conta.TaxaServicoCentavos, conta.TotalCentavos));

        // Without the permission: 403, empty body, the denial is audited (sensitive), nothing changes.
        foreach (var token in new[] { t.Garcom, t.Supervisor })
        {
            using var denied = await RemoveFeeAsync(client, token, comanda, versao);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(0, denied.Content.Headers.ContentLength ?? 0);
        }

        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'acesso:negar'"));
        Assert.Equal((versao, false), (await VersionOfAsync(comanda), (await BillAsync(client, t.Dono, comanda)).TaxaServicoRemovida));

        await AssertErrorAsync(await RemoveFeeAsync(client, t.Dono, comanda, versao - 1), HttpStatusCode.Conflict, "versao-desatualizada", versao);
        using var removed = await RemoveFeeAsync(client, t.Dono, comanda, versao);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var sem = (await removed.Content.ReadFromJsonAsync<ContaBody>())!;
        Assert.Equal((0L, 1005L, 1005L, true, versao + 1), (sem.TaxaServicoCentavos, sem.TotalCentavos, sem.SaldoCentavos, sem.TaxaServicoRemovida, sem.Versao));
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, depois->>'taxaServicoCentavos', depois->>'totalAntesCentavos', depois->>'totalDepoisCentavos' FROM auditoria_registros WHERE acao = 'comanda:remover-taxa-servico'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3))));
        Assert.Equal((DonoA, "101", "1106", "1005"), audit);
        await AssertErrorAsync(await RemoveFeeAsync(client, t.Dono, comanda, versao + 1), HttpStatusCode.Conflict, "taxa-ja-removida");
        var evento = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT payload->>'ComandaId', payload->>'TaxaServicoCentavos', payload->>'RemovidaPor' FROM outbox_eventos WHERE tipo = 'comanda.taxa-servico-removida'", r => (r.GetString(0), r.GetString(1), r.GetString(2))));
        Assert.Equal((comanda.ToString("D"), "101", DonoA.ToString("D")), evento);

        // The fee stays removed: the cash needed is the consumption alone.
        await OpenShiftAsync(client, t.Dono, 0);
        var pago = await PayOkAsync(client, t.Dono, comanda, 1005, versao + 1);
        Assert.Equal((1005L, 0L), (pago.AplicadoCentavos, pago.SaldoRestanteCentavos));
    }

    [Fact]
    public async Task A_cashier_without_the_permission_to_see_others_comandas_cannot_pay_close_or_remove_the_fee_of_one_they_cannot_read()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var cashier = await perfis.CriarAsync(request, "Caixa cego", [new RegraDeAcesso(RuleEffect.Allow, "comanda:ver"), new RegraDeAcesso(RuleEffect.Allow, "comanda:receber-pagamento"), new RegraDeAcesso(RuleEffect.Allow, "comanda:fechar"), new RegraDeAcesso(RuleEffect.Allow, "comanda:remover-taxa-servico"), new RegraDeAcesso(RuleEffect.Allow, "pagamento:registrar")]);
        Assert.Equal(ResultadoGestao.Ok, cashier.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, Mudo, cashier.Id!.Value));

        // Assigning a profile moves the credential version, so the cashier signs in again.
        var credencial = (await QueryAsync(OrgA, UnitA, $"SELECT versao_credencial FROM contas WHERE id = '{Mudo:D}'", r => r.GetInt32(0))).Single();
        var caixa = (await api.Services.GetRequiredService<Sessoes>().IniciarAsync(Mudo, OrgA, credencial, false, new ClienteDaSessao("127.0.0.1", "testes"))).AccessToken;
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);
        var inexistente = Guid.NewGuid();
        var antes = (await VersionOfAsync(comanda), await CountAsync("pagamentos"), await CountAsync("auditoria_registros WHERE acao LIKE 'comanda:%' OR acao = 'pagamento:registrar'"), await CountAsync("outbox_eventos"));

        // The answer for a comanda they cannot read is the same as for one that does not exist.
        foreach (var (real, falso) in new[]
        {
            (await PayAsync(client, caixa, comanda, 1100, versao), await PayAsync(client, caixa, inexistente, 1100, versao)),
            (await CloseAsync(client, caixa, comanda, versao), await CloseAsync(client, caixa, inexistente, versao)),
            (await RemoveFeeAsync(client, caixa, comanda, versao), await RemoveFeeAsync(client, caixa, inexistente, versao)),
        })
        {
            await AssertErrorAsync(real, HttpStatusCode.NotFound, "nao-encontrada");
            await AssertErrorAsync(falso, HttpStatusCode.NotFound, "nao-encontrada");
        }

        Assert.Equal(antes, (await VersionOfAsync(comanda), await CountAsync("pagamentos"), await CountAsync("auditoria_registros WHERE acao LIKE 'comanda:%' OR acao = 'pagamento:registrar'"), await CountAsync("outbox_eventos")));

        // With the permission (the owner here) the same comanda is paid and closed.
        var pago = await PayOkAsync(client, t.Dono, comanda, 1100, versao);
        using var closed = await CloseAsync(client, t.Dono, comanda, pago.ComandaVersao);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    [Fact]
    public async Task Closing_a_comanda_with_zero_consumption_is_allowed_and_leaves_a_closed_comanda_with_a_zero_total()
    {
        // Decision (F4-0044): allowed. There is no cancel-comanda route in this slice, so refusing would leave a comanda opened by mistake open forever.
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var comanda = await OpenAsync(client, t.Dono);

        using var closed = await CloseAsync(client, t.Dono, comanda.Id, comanda.Versao);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var conta = (await closed.Content.ReadFromJsonAsync<ContaBody>())!;
        Assert.Equal((0L, 0L, "fechada", comanda.Versao + 1), (conta.TotalCentavos, conta.SaldoCentavos, conta.Estado, conta.Versao));
        Assert.Equal(1L, await CountAsync("outbox_eventos WHERE tipo = 'comanda.fechada'"));
        Assert.Equal(0L, await CountAsync("pagamentos"));
    }

    [Fact]
    public async Task The_fee_percentage_is_frozen_when_the_comanda_opens_and_a_later_change_does_not_reach_the_bill()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (comanda, _, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);
        var configuracoes = api.Services.GetRequiredService<Configuracoes>();
        Assert.Equal(ResultadoConfiguracao.Ok, await configuracoes.AlterarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), new AlterarConfiguracao(VendaSettings.TaxaServicoPercentual.Key, CamadaDeEscrita.Organizacao, "15", null, null, null)));

        var conta = await BillAsync(client, t.Dono, comanda);

        Assert.Equal((10m, 100L), (conta.TaxaServicoPercentual, conta.TaxaServicoCentavos));
    }

    [Fact]
    public async Task A_stale_version_on_a_payment_close_or_fee_removal_returns_409_with_the_current_version_and_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);

        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda, 500, versao - 1), HttpStatusCode.Conflict, "versao-desatualizada", versao);
        await AssertErrorAsync(await RemoveFeeAsync(client, t.Dono, comanda, versao - 1), HttpStatusCode.Conflict, "versao-desatualizada", versao);
        var pago = await PayOkAsync(client, t.Dono, comanda, 1100, versao);
        await AssertErrorAsync(await CloseAsync(client, t.Dono, comanda, versao), HttpStatusCode.Conflict, "versao-desatualizada", pago.ComandaVersao);

        Assert.Equal((1L, pago.ComandaVersao), (await CountAsync("pagamentos"), await VersionOfAsync(comanda)));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'comanda:fechar'"));
    }

    [Fact]
    public async Task Nothing_to_pay_and_invalid_amounts_are_refused_without_writing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var vazia = await OpenAsync(client, t.Garcom);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);

        await AssertErrorAsync(await PayAsync(client, t.Dono, vazia.Id, 500, vazia.Versao), HttpStatusCode.Conflict, "sem-saldo");
        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda, 0, versao), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda, -5, versao), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda, 100_000_001, versao), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await PayAsync(client, t.Dono, comanda, 500, versao, Guid.Empty.ToString("D")), HttpStatusCode.BadRequest, "invalido");
        Assert.Equal((0L, versao), (await CountAsync("pagamentos"), await VersionOfAsync(comanda)));

        // An empty comanda (balance zero) closes.
        using var closed = await CloseAsync(client, t.Dono, vazia.Id, vazia.Versao);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_item_or_undoing_a_round_below_what_was_already_received_is_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, produto) = await ComandaWithConsumoAsync(client, t, 1000, 1);
        var item = (await ItemsAsync(client, t.Dono, comanda)).Itens.Single();
        var pago = await PayOkAsync(client, t.Dono, comanda, 1100, versao);

        await AssertErrorAsync(await CancelAsync(client, t.Garcom, comanda, item.Id, pago.ComandaVersao, "cliente-desistiu"), HttpStatusCode.Conflict, "valor-ja-recebido");
        await AssertErrorAsync(await UndoAsync(client, t.Garcom, comanda, item.RodadaId, pago.ComandaVersao), HttpStatusCode.Conflict, "valor-ja-recebido");
        Assert.Equal((pago.ComandaVersao, "lancado"), (await VersionOfAsync(comanda), (await ItemsAsync(client, t.Dono, comanda)).Itens.Single().Estado));

        // Removing the fee after paying all of it would also leave the bill below what was received.
        await AssertErrorAsync(await RemoveFeeAsync(client, t.Dono, comanda, pago.ComandaVersao), HttpStatusCode.Conflict, "valor-ja-recebido");
        Assert.NotEqual(Guid.Empty, produto.Id);
    }

    [Fact]
    public async Task Payments_are_immutable_for_the_application_credential()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);
        var pago = await PayOkAsync(client, t.Dono, comanda, 500, versao);

        foreach (var sql in new[] { $"UPDATE pagamentos SET aplicado_centavos = 1, recebido_centavos = 1 WHERE id = '{pago.Id:D}'", $"DELETE FROM pagamentos WHERE id = '{pago.Id:D}'", "TRUNCATE pagamentos" })
        {
            var erro = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
        }

        Assert.Equal(500L, (await QueryAsync(OrgA, UnitA, $"SELECT aplicado_centavos FROM pagamentos WHERE id = '{pago.Id:D}'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Two_cashiers_paying_with_the_same_version_at_the_same_time_apply_exactly_one_payment()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Fire()
        {
            await gate.Task;
            return await PayAsync(client, t.Dono, comanda, 200, versao);
        }

        var first = Fire();
        var second = Fire();
        gate.SetResult();
        var responses = await Task.WhenAll(first, second);

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order().ToArray());
        var erro = (await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<Erro>())!;
        Assert.Equal(("versao-desatualizada", versao + 1), (erro.Codigo, erro.VersaoAtual));
        Assert.Equal((1L, 200L), (await CountAsync("pagamentos"), (await QueryAsync(OrgA, UnitA, "SELECT recebido_dinheiro_centavos FROM turnos_caixa", r => r.GetInt64(0))).Single()));

        // The same payment id sent twice at once is one payment and two successful answers.
        var pagamentoId = NewRoundId();
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> FireSame()
        {
            await gate2.Task;
            return await PayAsync(client, t.Dono, comanda, 300, versao + 1, pagamentoId);
        }

        var a = FireSame();
        var b = FireSame();
        gate2.SetResult();
        var same = await Task.WhenAll(a, b);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], same.Select(r => r.StatusCode).Order().ToArray());
        Assert.Single((await Task.WhenAll(same.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
        Assert.Equal(2L, await CountAsync("pagamentos"));
    }

    [Fact]
    public async Task Each_payment_route_is_denied_without_its_permission_and_another_organization_reaches_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);

        // Waiter: no route permission. Supervisor: may receive but lacks pagamento:registrar. Mute: nothing.
        foreach (var token in new[] { t.Garcom, t.Supervisor, t.Mudo })
        {
            using var pay = await PayAsync(client, token, comanda, 500, versao);
            using var close = await CloseAsync(client, token, comanda, versao);
            Assert.Equal(HttpStatusCode.Forbidden, pay.StatusCode);
            Assert.Equal(0, pay.Content.Headers.ContentLength ?? 0);
            Assert.NotEqual(HttpStatusCode.OK, close.StatusCode);
        }

        using var list = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/pagamentos", t.Mudo);
        using var anonymous = await PayAsync(client, null!, comanda, 500, versao);
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized), (list.StatusCode, anonymous.StatusCode));

        // Another organization: not found, nothing written there or here.
        await AssertErrorAsync(await PayAsync(client, t.DonoB, comanda, 500, versao), HttpStatusCode.NotFound, "nao-encontrada");
        using var billB = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/conta", t.DonoB);
        await AssertErrorAsync(billB, HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal((0L, versao), (await CountAsync("pagamentos"), await VersionOfAsync(comanda)));

        // A waiter sees the bill of their own comanda but not another waiter's.
        _ = await BillAsync(client, t.Garcom, comanda);
        using var other = await SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/conta", t.Garcom2);
        await AssertErrorAsync(other, HttpStatusCode.NotFound, "nao-encontrada");
    }

    [Fact]
    public async Task When_the_unit_of_work_throws_after_the_payment_event_the_payment_the_shift_movement_the_audit_the_event_and_the_version_roll_back_together()
    {
        await using var api = new Api(_appConnection, configure: services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IEventosDePagamentos));
            services.Remove(real);
            services.AddSingleton<IEventosDePagamentos>(p => new PaymentEventsThatThrow(ActivatorUtilities.CreateInstance<OutboxEventosDePagamentos>(p)));
        });
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);
        var recebimento = api.Services.GetRequiredService<GestaoDoRecebimento>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => recebimento.ReceberAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), comanda, Guid.CreateVersion7(), 500, versao));

        Assert.Equal((0L, 0L, 0L), (await CountAsync("pagamentos"), await CountAsync("auditoria_registros WHERE acao = 'pagamento:registrar'"), await CountAsync("outbox_eventos WHERE tipo = 'pagamento.registrado'")));
        Assert.Equal((versao, 0L), (await VersionOfAsync(comanda), (await QueryAsync(OrgA, UnitA, "SELECT recebido_dinheiro_centavos FROM turnos_caixa", r => r.GetInt64(0))).Single()));
    }

    [Fact]
    public async Task The_payment_queries_have_the_expected_sql_with_no_organization_filter_and_the_EF_model_matches_the_database()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new PagamentosDbContext(tx);

        var soma = PostgresPagamentosStore.Soma(context, Guid.NewGuid()).ToQueryString();
        var lista = PostgresPagamentosStore.DaComanda(context, Guid.NewGuid()).ToQueryString();

        Assert.Contains("FROM pagamentos AS", soma, StringComparison.Ordinal);
        Assert.Contains("p.comanda_id = @", soma, StringComparison.Ordinal);
        Assert.Contains("ORDER BY p.registrado_em, p.id", lista, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", soma + lista, StringComparison.Ordinal);
        Assert.Empty(await SchemaDrift.FindAsync(context));
    }

    [Fact]
    public async Task Opening_or_closing_a_shift_without_the_amount_is_refused_and_not_read_as_zero()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var noFloat = await SendAsync(client, HttpMethod.Post, "/caixa/turnos", t.Dono, new { });
        Assert.Equal(HttpStatusCode.BadRequest, noFloat.StatusCode);
        Assert.Equal(0L, await CountAsync("turnos_caixa"));

        await OpenShiftAsync(client, t.Dono, 5000);
        using var noCount = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { });
        using var wrongName = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { contado = 5000 });
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (noCount.StatusCode, wrongName.StatusCode));
        Assert.Equal(1L, await CountAsync("turnos_caixa WHERE fechado_em IS NULL AND contado_centavos IS NULL"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'caixa:fechar'"));

        // A real zero count still closes: zero is a count, absence is not.
        using var zero = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { contadoCentavos = 0 });
        Assert.Equal(HttpStatusCode.OK, zero.StatusCode);
        Assert.Equal(-5000L, (await zero.Content.ReadFromJsonAsync<TurnoBody>())!.DiferencaCentavos);
    }

    [Fact]
    public async Task When_closing_a_shift_throws_after_the_outbox_the_closing_the_audit_and_the_event_roll_back_together()
    {
        await using var api = new Api(_appConnection, configure: services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(Flow4Tech.Caixa.Application.IEventosDoCaixa));
            services.Remove(real);
            services.AddSingleton<Flow4Tech.Caixa.Application.IEventosDoCaixa>(p => new ShiftEventsThatThrowOnClose(ActivatorUtilities.CreateInstance<Flow4Tech.Caixa.Infrastructure.OutboxEventosDoCaixa>(p)));
        });
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 5000);
        var caixa = api.Services.GetRequiredService<Flow4Tech.Caixa.Application.GestaoDoCaixa>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => caixa.FecharAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), 5000));

        Assert.Equal(1L, await CountAsync("turnos_caixa WHERE fechado_em IS NULL AND contado_centavos IS NULL AND esperado_centavos IS NULL"));
        Assert.Equal((0L, 0L), (await CountAsync("auditoria_registros WHERE acao = 'caixa:fechar'"), await CountAsync("outbox_eventos WHERE tipo = 'caixa.fechado'")));
    }

    /// <summary>Real events, but the closing event fails right after its outbox row is written (opening is untouched).</summary>
    private sealed class ShiftEventsThatThrowOnClose(Flow4Tech.Caixa.Application.IEventosDoCaixa inner) : Flow4Tech.Caixa.Application.IEventosDoCaixa
    {
        public async Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
        {
            await inner.PublicarAsync(organization, evento, cancellationToken);
            if (evento is Flow4Tech.Caixa.Application.CaixaFechado)
            {
                throw new InvalidOperationException("boom after outbox");
            }
        }
    }

    /// <summary>Real events, but fails right after writing the outbox row: the payment, the shift movement, the audit and the version must roll back together.</summary>
    private sealed class PaymentEventsThatThrow(IEventosDePagamentos inner) : IEventosDePagamentos
    {
        public async Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
        {
            await inner.PublicarAsync(organization, evento, cancellationToken);
            throw new InvalidOperationException("boom after outbox");
        }
    }
}

public sealed partial class ComandasEndpointsTests
{
    private sealed record PermissoesBody(List<string> Permissoes);

    [Fact]
    public async Task The_caller_learns_only_their_own_permissions_of_the_salao_so_the_app_can_hide_what_it_would_refuse()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        async Task<List<string>> OfAsync(string token)
        {
            using var response = await SendAsync(client, HttpMethod.Get, "/sessao/permissoes", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PermissoesBody>())!.Permissoes;
        }

        var garcom = await OfAsync(t.Garcom);
        var supervisor = await OfAsync(t.Supervisor);
        var dono = await OfAsync(t.Dono);

        Assert.Contains("comanda:abrir", garcom);
        Assert.Contains("comanda:lancar-item", garcom);
        Assert.DoesNotContain("comanda:receber-pagamento", garcom);
        Assert.DoesNotContain("caixa:abrir", garcom);
        Assert.Contains("comanda:ver-comandas-de-outros", supervisor);
        Assert.DoesNotContain("comanda:lancar-item", supervisor);
        Assert.Contains("caixa:abrir", dono);
        Assert.Contains("comanda:remover-taxa-servico", dono);
        // Only the salão: nothing about settings, profiles or accounts is offered to the phone app.
        Assert.All(dono, p => Assert.Matches("^(comanda|caixa|mesa|produto|pagamento):", p));
        Assert.Equal(dono.Order(StringComparer.Ordinal).ToList(), dono);

        using var anonymous = await SendAsync(client, HttpMethod.Get, "/sessao/permissoes", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
