using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0145: refunds (total and partial) of a payment and the cancellation of a paid comanda, through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions). The
/// owner refunds without limit; "Mudo" holds a profile limited to R$ 10,00 per refund; "Supervisor" can see payments but holds no refund rule; "Garcom" holds neither.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record EstornoLido(Guid Id, Guid PagamentoId, Guid ComandaId, Guid? TurnoId, string Metodo, long ValorCentavos, string Motivo, Guid OperadorId);

    private sealed record EstornoFeitoBody(EstornoLido Estorno, long SaldoEstornavelCentavos);

    private sealed record EstornosDoPagamentoBody(Guid PagamentoId, Guid ComandaId, long AplicadoCentavos, long EstornadoCentavos, long SaldoEstornavelCentavos, List<EstornoLido> Estornos);

    private sealed record EstornoErroBody(string Codigo, long? SaldoEstornavelCentavos);

    private sealed record CancelamentoBody(ComandaBody Comanda, int ItensCancelados);

    private static Task<HttpResponseMessage> RefundAsync(HttpClient client, string? token, Guid pagamento, long? valor, string? motivo = "cliente reclamou") =>
        SendAsync(client, HttpMethod.Post, $"/pagamentos/{pagamento:D}/estornos", token, new { valorCentavos = valor, motivo });

    private static async Task AssertRefundErrorAsync(HttpResponseMessage response, HttpStatusCode status, string codigo)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(codigo, (await response.Content.ReadFromJsonAsync<EstornoErroBody>())!.Codigo);
    }

    private static Task<HttpResponseMessage> CancelPaidAsync(HttpClient client, string? token, Guid comanda, int versao, string? motivoDoEstorno) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/cancelar", token, new { versao, motivo = "cliente-desistiu", motivoDoEstorno });

    /// <summary>Mudo may see comandas and payments and refund up to R$ 10,00 per refund; it also cancels comandas.</summary>
    private async Task<string> GiveRefundProfileAsync(Api api, Guid user, string nome, params RegraDeAcesso[] estorno)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var leitura = new[] { "comanda:ver", "comanda:ver-comandas-de-outros", "pagamento:ver", "comanda:cancelar" };
        var perfil = await perfis.CriarAsync(request, nome, [.. leitura.Select(p => new RegraDeAcesso(RuleEffect.Allow, p)), .. estorno]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, user, perfil.Id!.Value));
        return await FreshTokenAsync(api, user);
    }

    private Task<long> RefundRowsAsync() => CountAsync("estornos_pagamento");

    [Fact]
    public async Task A_partial_then_a_total_refund_write_negative_entries_leave_the_payment_intact_move_the_shift_and_are_audited()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var turno = await OpenShiftAsync(client, t.Dono, 10_000);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST1");
        var pago = await PayOkAsync(client, t.Dono, comanda, 2200, versao);
        var antes = await QueryAsync(OrgA, UnitA, $"SELECT aplicado_centavos, recebido_centavos, turno_id FROM pagamentos WHERE id = '{pago.Id:D}'", r => (r.GetInt64(0), r.GetInt64(1), r.GetGuid(2)));

        var parcial = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, t.Dono, pago.Id, 500, "  item errado  "), HttpStatusCode.Created);
        Assert.Equal((-500L, "item errado", pago.Id, comanda, (Guid?)turno.Id, "dinheiro", 1700L), (parcial.Estorno.ValorCentavos, parcial.Estorno.Motivo, parcial.Estorno.PagamentoId, parcial.Estorno.ComandaId, parcial.Estorno.TurnoId, parcial.Estorno.Metodo, parcial.SaldoEstornavelCentavos));
        Assert.Equal((2200L - 500L, 0L), ((await BillAsync(client, t.Dono, comanda)).PagoCentavos, 0L));

        var total = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, t.Dono, pago.Id, null, "restante"), HttpStatusCode.Created);
        Assert.Equal((-1700L, 0L), (total.Estorno.ValorCentavos, total.SaldoEstornavelCentavos));

        // The original payment is untouched and the negative entries sum to minus what it applied.
        Assert.Equal(antes, await QueryAsync(OrgA, UnitA, $"SELECT aplicado_centavos, recebido_centavos, turno_id FROM pagamentos WHERE id = '{pago.Id:D}'", r => (r.GetInt64(0), r.GetInt64(1), r.GetGuid(2))));
        Assert.Equal(-2200L, (await QueryAsync(OrgA, UnitA, $"SELECT SUM(valor_centavos)::bigint FROM estornos_pagamento WHERE pagamento_id = '{pago.Id:D}'", r => r.GetInt64(0))).Single());
        var lista = await BodyAsync<EstornosDoPagamentoBody>(await SendAsync(client, HttpMethod.Get, $"/pagamentos/{pago.Id:D}/estornos", t.Dono), HttpStatusCode.OK);
        Assert.Equal((2200L, 2200L, 0L), (lista.AplicadoCentavos, lista.EstornadoCentavos, lista.SaldoEstornavelCentavos));
        Assert.Equal([-500L, -1700L], lista.Estornos.Select(e => e.ValorCentavos));
        Assert.Equal(0L, (await BillAsync(client, t.Dono, comanda)).PagoCentavos);

        // Immutable: the application credential cannot change or delete a refund.
        await Assert.ThrowsAsync<PostgresException>(() => QueryAsync(OrgA, UnitA, "UPDATE estornos_pagamento SET motivo = 'x' RETURNING 1", r => r.GetInt32(0)));
        await Assert.ThrowsAsync<PostgresException>(() => QueryAsync(OrgA, UnitA, "DELETE FROM estornos_pagamento RETURNING 1", r => r.GetInt32(0)));

        // The cash went back out of the drawer: expected = float + received - refunded.
        using var fechamento = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { contadoCentavos = 10_000 });
        Assert.Equal(HttpStatusCode.OK, fechamento.StatusCode);
        var fechado = (await fechamento.Content.ReadFromJsonAsync<TurnoBody>())!;
        Assert.Equal((10_000L, 0L), (fechado.EsperadoCentavos, fechado.DiferencaCentavos));

        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'pagamento:estornar' AND resultado = 'sucesso'"));
        var audit = (await QueryAsync(OrgA, UnitA, $"SELECT usuario_id, objeto_tipo, objeto_id, depois->>'valorCentavos', depois->>'motivo' FROM auditoria_registros WHERE acao = 'pagamento:estornar' ORDER BY sequencia", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)))).First();
        Assert.Equal((DonoA, "pagamento", pago.Id.ToString("D"), "-500", "item errado"), audit);
        Assert.Equal(2L, await CountAsync("outbox_eventos WHERE tipo = 'pagamento.estornado'"));
    }

    [Fact]
    public async Task A_refund_above_the_refundable_balance_without_amount_or_without_a_reason_is_400_and_writes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1, "EST2");
        var pago = await PayOkAsync(client, t.Dono, comanda, 1100, versao);
        _ = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, t.Dono, pago.Id, 400), HttpStatusCode.Created);

        using var acima = await RefundAsync(client, t.Dono, pago.Id, 701);
        Assert.Equal(HttpStatusCode.BadRequest, acima.StatusCode);
        var erro = (await acima.Content.ReadFromJsonAsync<EstornoErroBody>())!;
        Assert.Equal(("excede-saldo-estornavel", 700L), (erro.Codigo, erro.SaldoEstornavelCentavos));
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, pago.Id, 0), HttpStatusCode.BadRequest, "invalido");
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, pago.Id, -5), HttpStatusCode.BadRequest, "invalido");
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, pago.Id, 100, "   "), HttpStatusCode.BadRequest, "invalido");
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, pago.Id, 100, null), HttpStatusCode.BadRequest, "invalido");
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, pago.Id, 100, new string('x', 201)), HttpStatusCode.BadRequest, "invalido");

        // Everything is refunded: a further total refund has nothing left.
        _ = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, t.Dono, pago.Id, null), HttpStatusCode.Created);
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, pago.Id, null), HttpStatusCode.BadRequest, "excede-saldo-estornavel");
        Assert.Equal((2L, 2L), (await RefundRowsAsync(), await CountAsync("auditoria_registros WHERE acao = 'pagamento:estornar'")));
    }

    [Fact]
    public async Task A_refund_above_the_limit_of_the_profile_is_403_typed_writes_nothing_and_is_audited_while_one_within_it_passes()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var gerente = await GiveRefundProfileAsync(api, Mudo, "Gerente de estorno", new RegraDeAcesso(RuleEffect.Allow, "pagamento:estornar", MaxValueCents: 1000));
        var turno = await OpenShiftAsync(client, t.Dono, 5000);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST3");
        var pago = await PayOkAsync(client, t.Dono, comanda, 2200, versao);

        using var acima = await RefundAsync(client, gerente, pago.Id, 1001);
        await AssertRefundErrorAsync(acima, HttpStatusCode.Forbidden, "negado");
        // A total refund of 2200 is measured on the server too: the client never says the amount.
        await AssertRefundErrorAsync(await RefundAsync(client, gerente, pago.Id, null), HttpStatusCode.Forbidden, "negado");
        Assert.Equal((0L, 0L, 0L), (await RefundRowsAsync(), await CountAsync("auditoria_registros WHERE acao = 'pagamento:estornar'"), await CountAsync("outbox_eventos WHERE tipo = 'pagamento.estornado'")));
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, $"SELECT estornado_dinheiro_centavos FROM turnos_caixa WHERE id = '{turno.Id:D}'", r => r.GetInt64(0))).Single());
        var negacoes = await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_id, depois->>'permissao' FROM auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2)));
        Assert.Equal([(Mudo, pago.Id.ToString("D"), "pagamento:estornar"), (Mudo, pago.Id.ToString("D"), "pagamento:estornar")], negacoes);

        var dentro = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, gerente, pago.Id, 1000), HttpStatusCode.Created);
        Assert.Equal((-1000L, Mudo, (Guid?)turno.Id), (dentro.Estorno.ValorCentavos, dentro.Estorno.OperadorId, dentro.Estorno.TurnoId));
        Assert.Equal(1L, await RefundRowsAsync());
    }

    [Fact]
    public async Task Without_a_session_401_without_the_payment_permission_403_with_it_but_no_refund_rule_403_typed_and_the_owner_always_may()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var semRegra = await GiveRefundProfileAsync(api, Mudo, "So ve pagamentos");
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1, "EST4");
        var pago = await PayOkAsync(client, t.Dono, comanda, 1100, versao);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefundAsync(client, null, pago.Id, 100)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, $"/pagamentos/{pago.Id:D}/estornos", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RefundAsync(client, t.Garcom, pago.Id, 100)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, $"/pagamentos/{pago.Id:D}/estornos", t.Garcom)).StatusCode);
        await AssertRefundErrorAsync(await RefundAsync(client, semRegra, pago.Id, 100), HttpStatusCode.Forbidden, "negado");
        Assert.Equal(0L, await RefundRowsAsync());

        _ = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, t.Dono, pago.Id, 1000), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Another_organization_neither_sees_nor_refunds_the_payment()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1, "EST5");
        var pago = await PayOkAsync(client, t.Dono, comanda, 1100, versao);

        await AssertRefundErrorAsync(await RefundAsync(client, t.DonoB, pago.Id, 100), HttpStatusCode.NotFound, "nao-encontrado");
        await AssertRefundErrorAsync(await SendAsync(client, HttpMethod.Get, $"/pagamentos/{pago.Id:D}/estornos", t.DonoB), HttpStatusCode.NotFound, "nao-encontrado");
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, Guid.NewGuid(), 100), HttpStatusCode.NotFound, "nao-encontrado");
        Assert.Equal(0L, await RefundRowsAsync());
        Assert.Empty(await QueryAsync(OrgB, UnitB, "SELECT 1 FROM estornos_pagamento", r => r.GetInt32(0)));
    }

    [Fact]
    public async Task The_refund_goes_to_the_open_shift_of_the_payment_else_to_the_operators_current_one_and_is_refused_with_none_or_with_too_little_cash_in_the_drawer()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var antigo = await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST6");
        var pago = await PayOkAsync(client, t.Dono, comanda, 2200, versao);
        using var fechamento = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { contadoCentavos = 2200 });
        Assert.Equal(HttpStatusCode.OK, fechamento.StatusCode);

        // The payment's shift is closed and the operator has none open.
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, pago.Id, 100), HttpStatusCode.Conflict, "sem-turno-aberto");

        // A new shift with R$ 5,00 in the drawer cannot hand back R$ 22,00 in cash, but can hand back R$ 5,00.
        var atual = await OpenShiftAsync(client, t.Dono, 500);
        await AssertRefundErrorAsync(await RefundAsync(client, t.Dono, pago.Id, 2200), HttpStatusCode.Conflict, "caixa-sem-dinheiro");
        Assert.Equal(0L, await RefundRowsAsync());
        var feito = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, t.Dono, pago.Id, 500), HttpStatusCode.Created);
        Assert.Equal((Guid?)atual.Id, feito.Estorno.TurnoId);
        Assert.NotEqual(antigo.Id, atual.Id);
        Assert.Equal((500L, 0L), (await QueryAsync(OrgA, UnitA, $"SELECT estornado_dinheiro_centavos, recebido_dinheiro_centavos FROM turnos_caixa WHERE id = '{atual.Id:D}'", r => (r.GetInt64(0), r.GetInt64(1)))).Single());
        Assert.Equal((0L, 2200L), (await QueryAsync(OrgA, UnitA, $"SELECT estornado_dinheiro_centavos, recebido_dinheiro_centavos FROM turnos_caixa WHERE id = '{antigo.Id:D}'", r => (r.GetInt64(0), r.GetInt64(1)))).Single());
    }

    [Fact]
    public async Task Cancelling_a_paid_comanda_needs_the_refund_first_or_together_and_does_it_all_in_one_unit_of_work()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var turno = await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST7");
        var pago = await PayOkAsync(client, t.Dono, comanda, 1000, versao);

        // Money was received and no refund comes with it: refused as before, nothing changes.
        await AssertErrorAsync(await CancelPaidAsync(client, t.Dono, comanda, pago.ComandaVersao, null), HttpStatusCode.Conflict, "com-pagamentos");
        Assert.Equal(("aberta", 0L), ((await ListAsync(client, t.Garcom)).Single().Estado, await RefundRowsAsync()));

        // A reason that is not acceptable is refused with the whole operation rolled back.
        await AssertErrorAsync(await CancelPaidAsync(client, t.Dono, comanda, pago.ComandaVersao, "  "), HttpStatusCode.BadRequest, "invalido");
        Assert.Equal((pago.ComandaVersao, 0L), (await VersionOfAsync(comanda), await RefundRowsAsync()));

        var cancelado = await BodyAsync<CancelamentoBody>(await CancelPaidAsync(client, t.Dono, comanda, pago.ComandaVersao, "cancelamento da comanda"), HttpStatusCode.OK);
        Assert.Equal("cancelada", cancelado.Comanda.Estado);
        var estorno = (await QueryAsync(OrgA, UnitA, $"SELECT valor_centavos, motivo, turno_id FROM estornos_pagamento WHERE comanda_id = '{comanda:D}'", r => (r.GetInt64(0), r.GetString(1), r.GetGuid(2)))).Single();
        Assert.Equal((-1000L, "cancelamento da comanda", turno.Id), estorno);
        Assert.Equal(1000L, (await QueryAsync(OrgA, UnitA, $"SELECT estornado_dinheiro_centavos FROM turnos_caixa WHERE id = '{turno.Id:D}'", r => r.GetInt64(0))).Single());
        Assert.Equal(("1000", "1"), (await QueryAsync(OrgA, UnitA, $"SELECT depois->>'estornadoCentavos', depois->>'itensCancelados' FROM auditoria_registros WHERE acao = 'comanda:cancelar' AND objeto_id = '{comanda:D}'", r => (r.GetString(0), r.GetString(1)))).Single());
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'pagamento:estornar'"));

        // A prior refund of everything is also enough: the cancellation then needs no reason of its own for it.
        var (outra, versaoOutra, _) = await ComandaWithConsumoAsync(client, t, 1000, 1, "EST8");
        var pagoOutra = await PayOkAsync(client, t.Dono, outra, 500, versaoOutra);
        _ = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, t.Dono, pagoOutra.Id, null), HttpStatusCode.Created);
        _ = await BodyAsync<CancelamentoBody>(await CancelPaidAsync(client, t.Dono, outra, pagoOutra.ComandaVersao, null), HttpStatusCode.OK);
        Assert.Equal(2L, await RefundRowsAsync());
    }

    [Fact]
    public async Task Cancelling_a_paid_comanda_beyond_the_refund_limit_is_403_typed_and_the_comanda_stays_open_with_nothing_written()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var gerente = await GiveRefundProfileAsync(api, Mudo, "Gerente de estorno", new RegraDeAcesso(RuleEffect.Allow, "pagamento:estornar", MaxValueCents: 1000));
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST9");
        var pago = await PayOkAsync(client, t.Dono, comanda, 1500, versao);

        using var negado = await CancelPaidAsync(client, gerente, comanda, pago.ComandaVersao, "cancelamento");
        Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
        Assert.Equal("negado", (await negado.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        Assert.Equal((pago.ComandaVersao, 0L, "aberta"), (await VersionOfAsync(comanda), await RefundRowsAsync(), (await ListAsync(client, t.Garcom)).Single().Estado));
        Assert.Equal(1L, await CountAsync($"auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' AND objeto_id = '{comanda:D}'"));
        Assert.Equal(0L, await CountAsync($"auditoria_registros WHERE acao = 'comanda:cancelar' AND objeto_id = '{comanda:D}'"));

        // Without a shift to refund into, the same cancellation is 409 and rolls back the version bump too.
        using var fechamento = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { contadoCentavos = 1000 });
        Assert.Equal(HttpStatusCode.OK, fechamento.StatusCode);
        await AssertErrorAsync(await CancelPaidAsync(client, t.Dono, comanda, pago.ComandaVersao, "cancelamento"), HttpStatusCode.Conflict, "sem-turno-aberto");
        Assert.Equal((pago.ComandaVersao, 0L), (await VersionOfAsync(comanda), await RefundRowsAsync()));
    }

    [Fact]
    public async Task Whoever_holds_only_pagamento_ver_gets_403_before_any_validation_so_nothing_leaks_and_the_denial_is_audited()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var semRegra = await GiveRefundProfileAsync(api, Mudo, "So ve pagamentos");
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1, "EST10");
        var pago = await PayOkAsync(client, t.Dono, comanda, 1100, versao);

        // Above the balance, empty reason, missing payment: the owner would get 400/404, this profile gets 403 each time.
        await AssertRefundErrorAsync(await RefundAsync(client, semRegra, pago.Id, 999_999), HttpStatusCode.Forbidden, "negado");
        await AssertRefundErrorAsync(await RefundAsync(client, semRegra, pago.Id, 100, " "), HttpStatusCode.Forbidden, "negado");
        await AssertRefundErrorAsync(await RefundAsync(client, semRegra, Guid.NewGuid(), 100), HttpStatusCode.Forbidden, "negado");
        Assert.Equal((0L, 0L), (await RefundRowsAsync(), await CountAsync("limites_consumo")));
        Assert.Equal(3L, await CountAsync("auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado'"));
    }

    [Fact]
    public async Task Eight_parallel_partial_refunds_of_one_payment_never_pass_what_it_applied_and_keep_the_drawer_coherent()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var turno = await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST11");
        var pago = await PayOkAsync(client, t.Dono, comanda, 2200, versao);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RefundAsync(client, t.Dono, pago.Id, 500)));
        var codigos = respostas.Select(r => r.StatusCode).ToList();
        foreach (var r in respostas.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await AssertRefundErrorAsync(r, HttpStatusCode.BadRequest, "excede-saldo-estornavel");
        }

        // 2200 / 500: exactly four fit, the other four exceed what is left (200).
        Assert.Equal((4, 4), (codigos.Count(c => c == HttpStatusCode.Created), codigos.Count(c => c == HttpStatusCode.BadRequest)));
        Assert.Equal((-2000L, 4L), (await QueryAsync(OrgA, UnitA, $"SELECT SUM(valor_centavos)::bigint, COUNT(*) FROM estornos_pagamento WHERE pagamento_id = '{pago.Id:D}'", r => (r.GetInt64(0), r.GetInt64(1)))).Single());
        Assert.Equal(2000L, (await QueryAsync(OrgA, UnitA, $"SELECT estornado_dinheiro_centavos FROM turnos_caixa WHERE id = '{turno.Id:D}'", r => r.GetInt64(0))).Single());
        Assert.Equal(4L, await CountAsync("auditoria_registros WHERE acao = 'pagamento:estornar' AND resultado = 'sucesso'"));

        // fundo 0 + received 2200 - refunded 2000 = 200 in the drawer.
        using var fechamento = await SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", t.Dono, new { contadoCentavos = 200 });
        Assert.Equal(HttpStatusCode.OK, fechamento.StatusCode);
        var fechado = (await fechamento.Content.ReadFromJsonAsync<TurnoBody>())!;
        Assert.Equal((200L, 0L), (fechado.EsperadoCentavos, fechado.DiferencaCentavos));
    }

    [Fact]
    public async Task Parallel_partial_refunds_racing_the_cancellation_of_the_comanda_never_refund_more_than_was_paid()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var turno = await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST12");
        var pago = await PayOkAsync(client, t.Dono, comanda, 2200, versao);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => i < 5 ? RefundAsync(client, t.Dono, pago.Id, 500) : CancelPaidAsync(client, t.Dono, comanda, pago.ComandaVersao, "cancelamento junto")));

        // The cancellation always wins its own right to the rest: it ends 200 and the comanda is cancelled.
        Assert.Equal(HttpStatusCode.OK, respostas[^1].StatusCode);
        foreach (var r in respostas.Take(5).Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await AssertRefundErrorAsync(r, HttpStatusCode.BadRequest, "excede-saldo-estornavel");
        }

        Assert.Equal("cancelada", (await QueryAsync(OrgA, UnitA, $"SELECT estado FROM comandas WHERE id = '{comanda:D}'", r => r.GetString(0))).Single());
        var soma = (await QueryAsync(OrgA, UnitA, $"SELECT SUM(valor_centavos)::bigint FROM estornos_pagamento WHERE pagamento_id = '{pago.Id:D}'", r => r.GetInt64(0))).Single();
        Assert.Equal(-2200L, soma);
        Assert.Equal(2200L, (await QueryAsync(OrgA, UnitA, $"SELECT estornado_dinheiro_centavos FROM turnos_caixa WHERE id = '{turno.Id:D}'", r => r.GetInt64(0))).Single());
        Assert.Equal(await RefundRowsAsync(), await CountAsync("auditoria_registros WHERE acao = 'pagamento:estornar' AND resultado = 'sucesso'"));

        // One cancellation moves the version by exactly one and leaves a single audit record, whatever the refunds around it did.
        Assert.Equal(pago.ComandaVersao + 1, await VersionOfAsync(comanda));
        Assert.Equal(1L, await CountAsync($"auditoria_registros WHERE acao = 'comanda:cancelar' AND objeto_id = '{comanda:D}'"));
    }

    [Fact]
    public async Task A_total_refund_that_drains_one_payment_while_the_comanda_is_being_cancelled_is_skipped_by_the_cancellation_which_still_succeeds()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var turno = await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST13");
        var primeiro = await PayOkAsync(client, t.Dono, comanda, 1100, versao);
        var segundo = await PayOkAsync(client, t.Dono, comanda, 1100, primeiro.ComandaVersao);

        // A competing session holds the lock of the FIRST payment (the cancellation lists both, then waits for it), refunds it whole and commits.
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var concorrente = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using (var trava = new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('estorno-pagamento:{primeiro.Id:D}', 0))", concorrente.Connection, concorrente.Transaction))
        {
            await trava.ExecuteNonQueryAsync();
        }

        var cancelamento = Task.Run(async () =>
        {
            using var r = await CancelPaidAsync(client, t.Dono, comanda, segundo.ComandaVersao, "cancelamento junto");
            return r.StatusCode;
        });
        await Task.Delay(1500);
        await using (var estorno = new NpgsqlCommand(
            $"""
            INSERT INTO estornos_pagamento (id, organizacao_id, unidade_id, pagamento_id, comanda_id, turno_id, metodo, valor_centavos, motivo, operador_id, registrado_em)
            VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', '{UnitA:D}', '{primeiro.Id:D}', '{comanda:D}', '{turno.Id:D}', 'dinheiro', -1100, 'estorno concorrente', '{DonoA:D}', now());
            UPDATE turnos_caixa SET estornado_dinheiro_centavos = estornado_dinheiro_centavos + 1100 WHERE id = '{turno.Id:D}';
            """,
            concorrente.Connection,
            concorrente.Transaction))
        {
            await estorno.ExecuteNonQueryAsync();
        }

        await concorrente.CommitAsync();
        Assert.Equal(HttpStatusCode.OK, await cancelamento);

        Assert.Equal("cancelada", (await QueryAsync(OrgA, UnitA, $"SELECT estado FROM comandas WHERE id = '{comanda:D}'", r => r.GetString(0))).Single());
        Assert.Equal((-2200L, 2L), (await QueryAsync(OrgA, UnitA, $"SELECT SUM(valor_centavos)::bigint, COUNT(*) FROM estornos_pagamento WHERE comanda_id = '{comanda:D}'", r => (r.GetInt64(0), r.GetInt64(1)))).Single());
        Assert.Equal(1L, await CountAsync($"estornos_pagamento WHERE pagamento_id = '{segundo.Id:D}'"));
        Assert.Equal(2200L, (await QueryAsync(OrgA, UnitA, $"SELECT estornado_dinheiro_centavos FROM turnos_caixa WHERE id = '{turno.Id:D}'", r => r.GetInt64(0))).Single());
        Assert.Equal(segundo.ComandaVersao + 1, await VersionOfAsync(comanda));
        Assert.Equal(1L, await CountAsync($"auditoria_registros WHERE acao = 'comanda:cancelar' AND objeto_id = '{comanda:D}'"));
    }

    [Fact]
    public async Task The_daily_cap_counts_only_the_cents_really_refunded_never_the_zero_cent_probe_nor_a_refund_refused_by_validation()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var capado = await GiveRefundProfileAsync(api, Mudo, "Estorno com teto", new RegraDeAcesso(RuleEffect.Allow, "pagamento:estornar", DailyCapCents: 1800));
        await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "EST14");
        var pago = await PayOkAsync(client, t.Dono, comanda, 2200, versao);

        Task<List<long>> ConsumidoAsync() => QueryAsync(OrgA, UnitA, "SELECT consumido_centavos FROM limites_consumo WHERE permissao = 'pagamento:estornar'", r => r.GetInt64(0));

        // Refused by validation (balance, then reason): the probe and nothing else ran, so no counter row exists.
        await AssertRefundErrorAsync(await RefundAsync(client, capado, pago.Id, 999_999), HttpStatusCode.BadRequest, "excede-saldo-estornavel");
        await AssertRefundErrorAsync(await RefundAsync(client, capado, pago.Id, 100, " "), HttpStatusCode.BadRequest, "invalido");
        Assert.Empty(await ConsumidoAsync());

        // A real refund reserves exactly its cents, once.
        _ = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, capado, pago.Id, 1000), HttpStatusCode.Created);
        Assert.Equal([1000L], await ConsumidoAsync());

        // A later validation refusal and the probe leave it where it was.
        await AssertRefundErrorAsync(await RefundAsync(client, capado, pago.Id, 999_999), HttpStatusCode.BadRequest, "excede-saldo-estornavel");
        await AssertRefundErrorAsync(await RefundAsync(client, capado, pago.Id, 100, ""), HttpStatusCode.BadRequest, "invalido");
        Assert.Equal([1000L], await ConsumidoAsync());

        _ = await BodyAsync<EstornoFeitoBody>(await RefundAsync(client, capado, pago.Id, 500), HttpStatusCode.Created);
        Assert.Equal([1500L], await ConsumidoAsync());

        // Past the cap: 403 and the reservation rolled back with the refund.
        await AssertRefundErrorAsync(await RefundAsync(client, capado, pago.Id, 400), HttpStatusCode.Forbidden, "negado");
        Assert.Equal([1500L], await ConsumidoAsync());
        Assert.Equal(2L, await RefundRowsAsync());
    }
}
