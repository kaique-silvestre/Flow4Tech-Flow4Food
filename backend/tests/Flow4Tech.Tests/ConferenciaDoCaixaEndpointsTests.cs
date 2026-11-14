using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Caixa.Application;
using Flow4Tech.Caixa.Infrastructure;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Events;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0065: the complete cash register through the real authenticated routes (same fixture as the comandas: real PostgreSQL, application credential with RLS, real sessions). Supply and withdrawal,
/// the count per method, the tolerance, the approval of a difference limited by amount through the engine, separation of duties, the blind count, the history, append-only tables, concurrency
/// and isolation. The owner is unlimited and plays the manager/cashier where the limit is not the subject; <c>Garcom2</c> is the limited cashier, <c>Supervisor</c> the limited approver.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record MovimentoDeCaixaBody(Guid Id, Guid TurnoId, string Tipo, long ValorCentavos, string Motivo, string? Destino, Guid OperadorId, DateTimeOffset CriadoEm);

    private sealed record ContagemBody(Guid MetodoId, string MetodoNome, long ContadoCentavos, long EsperadoCentavos, long DiferencaCentavos);

    private sealed record TurnoCompletoBody(
        Guid Id,
        Guid OperadorId,
        long FundoCentavos,
        DateTimeOffset? FechadoEm,
        long? ContadoCentavos,
        long? EsperadoCentavos,
        long? DiferencaCentavos,
        long RecebidoDinheiroCentavos,
        string? Situacao,
        long? SuprimentosCentavos,
        long? SangriasCentavos,
        bool EsperadoOculto,
        List<ContagemBody>? Contagens);

    private sealed record EsperadoMetodoBody(Guid MetodoId, string MetodoNome, long? EsperadoCentavos);

    private sealed record ResumoBody(TurnoCompletoBody Turno, bool Cego, long SuprimentosCentavos, long SangriasCentavos, long? EsperadoCentavos, List<EsperadoMetodoBody> PorMetodo);

    private sealed record AprovacaoBody(Guid Id, string Tipo, Guid AprovadorId, string Motivo, long DiferencaAntesCentavos, long DiferencaDepoisCentavos, DateTimeOffset CriadoEm);

    private sealed record DetalheBody(TurnoCompletoBody Turno, List<ContagemBody> Contagens, List<MovimentoDeCaixaBody> Movimentos, List<AprovacaoBody> Aprovacoes);

    private sealed record CodigoBody(string Codigo);

    private static Task<HttpResponseMessage> SupplyAsync(HttpClient client, string? token, long? valor, string? motivo = "Troco do banco", string? destino = null) =>
        SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/suprimentos", token, new { valorCentavos = valor, motivo, destino });

    private static Task<HttpResponseMessage> WithdrawAsync(HttpClient client, string? token, long? valor, string? motivo = "Deposito no cofre", string? destino = "Cofre") =>
        SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/sangrias", token, new { valorCentavos = valor, motivo, destino });

    private static Task<HttpResponseMessage> CloseShiftAsync(HttpClient client, string token, long contado, params object[] contagens) =>
        SendAsync(client, HttpMethod.Post, "/caixa/turnos/atual/fechar", token, new { contadoCentavos = contado, contagens });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, string? token, Guid turno, string? motivo = "Quebra de troco") =>
        SendAsync(client, HttpMethod.Post, $"/caixa/turnos/{turno:D}/diferenca/aprovar", token, new { motivo });

    private static Task<HttpResponseMessage> RecountAsync(HttpClient client, string? token, Guid turno, long? contado, string? motivo = "Contei de novo", params object[] contagens) =>
        SendAsync(client, HttpMethod.Post, $"/caixa/turnos/{turno:D}/diferenca/recontar", token, new { contadoCentavos = contado, motivo, contagens });

    private static async Task<string> BodyTextAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return await response.Content.ReadAsStringAsync();
        }
    }

    private static async Task<(HttpStatusCode Status, string? Codigo)> ErrorOfAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, text.Length == 0 ? null : System.Text.Json.JsonSerializer.Deserialize<CodigoBody>(text, System.Text.Json.JsonSerializerOptions.Web)!.Codigo);
        }
    }

    private static async Task<TurnoCompletoBody> OpenedAsync(HttpClient client, string token, long fundo)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/caixa/turnos", token, new { fundoCentavos = fundo });
        return await BodyAsync<TurnoCompletoBody>(response, HttpStatusCode.Created);
    }

    private static async Task<TurnoCompletoBody> ClosedAsync(HttpClient client, string token, long contado, params object[] contagens)
    {
        using var response = await CloseShiftAsync(client, token, contado, contagens);
        return await BodyAsync<TurnoCompletoBody>(response, HttpStatusCode.OK);
    }

    /// <summary>The operator's shift closed with a difference: opens with <paramref name="fundo"/> and counts <paramref name="contado"/>, so the difference is the gap between them.</summary>
    private static async Task<TurnoCompletoBody> ClosedWithDifferenceAsync(HttpClient client, string token, long fundo, long contado)
    {
        await OpenedAsync(client, token, fundo);
        return await ClosedAsync(client, token, contado);
    }

    private async Task<string> GiveCaixaProfileAsync(Api api, Guid user, string nome, params RegraDeAcesso[] regras)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfil = await perfis.CriarAsync(request, nome, regras);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, user, perfil.Id!.Value));
        return await FreshTokenAsync(api, user);
    }

    private static RegraDeAcesso Allow(string permission, long? max = null, long? daily = null) => new(RuleEffect.Allow, permission, MaxValueCents: max, DailyCapCents: daily);

    /// <summary>The limited cashier (Garcom2: supply and withdrawal up to R$ 100,00 each) and the limited approver (Supervisor: approves differences up to R$ 30,00).</summary>
    private async Task<(string Caixa, string Gerente)> CashierAndManagerAsync(Api api) =>
        (
            await GiveCaixaProfileAsync(api, Garcom2, "Caixa", Allow("caixa:ver"), Allow("caixa:abrir"), Allow("caixa:fechar"), Allow("caixa:suprimento", 10_000), Allow("caixa:sangria", 10_000), Allow("caixa:fechar-cego")),
            await GiveCaixaProfileAsync(api, Supervisor, "Gerente de caixa", Allow("caixa:ver"), Allow("caixa:ver-todos"), Allow("caixa:aprovar-diferenca", 3_000)));

    private static async Task SetOptionAsync(HttpClient client, string token, string key, string value)
    {
        using var response = await SendAsync(client, HttpMethod.Put, $"/configuracoes/{key}", token, new { camada = "organizacao", valor = value, motivo = "teste" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Supply_and_withdrawal_are_stored_audited_published_and_enter_the_expected_cash_with_the_cash_received()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var turno = await OpenedAsync(client, t.Dono, 10_000);
        var suprimento = await BodyAsync<MovimentoDeCaixaBody>(await SupplyAsync(client, t.Dono, 5_000, "  Troco do banco  "), HttpStatusCode.Created);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2);
        await PayOkAsync(client, t.Dono, comanda, 3_000, versao);
        var sangria = await BodyAsync<MovimentoDeCaixaBody>(await WithdrawAsync(client, t.Dono, 4_000, "Deposito no cofre", "  Cofre  "), HttpStatusCode.Created);

        Assert.Equal(("suprimento", 5_000L, "Troco do banco", null, DonoA, turno.Id), (suprimento.Tipo, suprimento.ValorCentavos, suprimento.Motivo, suprimento.Destino, suprimento.OperadorId, suprimento.TurnoId));
        Assert.Equal(("sangria", 4_000L, "Cofre"), (sangria.Tipo, sangria.ValorCentavos, sangria.Destino));
        using var movimentos = await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual/movimentos", t.Dono);
        Assert.Equal([suprimento.Id, sangria.Id], (await BodyAsync<List<MovimentoDeCaixaBody>>(movimentos, HttpStatusCode.OK)).Select(m => m.Id));

        // Expected cash now: float 10.000 + cash applied 2.750 (the change stays out) + supply 5.000 - withdrawal 4.000.
        var resumo = await BodyAsync<ResumoBody>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual/resumo", t.Dono), HttpStatusCode.OK);
        Assert.Equal((false, 13_750L, 5_000L, 4_000L), (resumo.Cego, resumo.EsperadoCentavos, resumo.SuprimentosCentavos, resumo.SangriasCentavos));

        var fechado = await ClosedAsync(client, t.Dono, 13_750);
        Assert.Equal((13_750L, 13_750L, 0L, "concluida", 5_000L, 4_000L), (fechado.ContadoCentavos, fechado.EsperadoCentavos, fechado.DiferencaCentavos, fechado.Situacao, fechado.SuprimentosCentavos, fechado.SangriasCentavos));
        Assert.Equal((13_750L, 5_000L, 4_000L), (await Single64Async("SELECT esperado_centavos FROM turnos_caixa"), await Single64Async("SELECT suprimentos_centavos FROM turnos_caixa"), await Single64Async("SELECT sangrias_centavos FROM turnos_caixa")));

        // Each movement: one row, one audit record, one event, all carrying the amount and the reason.
        Assert.Equal((2L, 1L, 1L), (await CountAsync("movimentos_caixa"), await CountAsync("auditoria_registros WHERE acao = 'caixa:suprimento'"), await CountAsync("auditoria_registros WHERE acao = 'caixa:sangria'")));
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_id, depois->>'valorCentavos', depois->>'destino', depois->>'saldoApos' FROM auditoria_registros WHERE acao = 'caixa:sangria'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
        Assert.Equal((DonoA, turno.Id.ToString("D"), "4000", "Cofre", "13750"), audit);
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'caixa.suprimento' AND payload->>'MovimentoId' = '{suprimento.Id:D}' AND payload->>'ValorCentavos' = '5000'"));
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'caixa.sangria' AND payload->>'MovimentoId' = '{sangria.Id:D}' AND payload->>'Destino' = 'Cofre'"));
        Assert.Equal(1L, await CountAsync("outbox_eventos WHERE tipo = 'caixa.fechado' AND payload->>'Situacao' = 'concluida'"));
        var detalhe = await BodyAsync<DetalheBody>(await SendAsync(client, HttpMethod.Get, $"/caixa/turnos/{turno.Id:D}", t.Dono), HttpStatusCode.OK);
        Assert.Equal([suprimento.Id, sangria.Id], detalhe.Movimentos.Select(m => m.Id));
    }

    [Fact]
    public async Task Bad_movements_are_refused_and_write_nothing_and_a_withdrawal_never_takes_more_than_the_drawer_holds()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        Assert.Equal((HttpStatusCode.NotFound, "sem-turno-aberto"), await ErrorOfAsync(await SupplyAsync(client, t.Dono, 100)));
        Assert.Equal((HttpStatusCode.NotFound, "sem-turno-aberto"), await ErrorOfAsync(await WithdrawAsync(client, t.Dono, 100)));
        await OpenedAsync(client, t.Dono, 1_000);

        foreach (var bad in new[]
        {
            await ErrorOfAsync(await SupplyAsync(client, t.Dono, 0)),
            await ErrorOfAsync(await SupplyAsync(client, t.Dono, -5)),
            await ErrorOfAsync(await SupplyAsync(client, t.Dono, null)),
            await ErrorOfAsync(await SupplyAsync(client, t.Dono, 100, null)),
            await ErrorOfAsync(await SupplyAsync(client, t.Dono, 100, "   ")),
            await ErrorOfAsync(await SupplyAsync(client, t.Dono, 100, new string('x', 201))),
            await ErrorOfAsync(await SupplyAsync(client, t.Dono, 100, "ok", new string('x', 81))),
            await ErrorOfAsync(await SupplyAsync(client, t.Dono, RegrasDeValor.Acima)),
            await ErrorOfAsync(await WithdrawAsync(client, t.Dono, 100, "ok", null)),
            await ErrorOfAsync(await WithdrawAsync(client, t.Dono, 100, "ok", "   ")),
            await ErrorOfAsync(await WithdrawAsync(client, t.Dono, 0)),
            await ErrorOfAsync(await WithdrawAsync(client, t.Dono, 100, null)),
        })
        {
            Assert.Equal((HttpStatusCode.BadRequest, "invalido"), bad);
        }

        Assert.Equal((HttpStatusCode.Conflict, "saldo-insuficiente"), await ErrorOfAsync(await WithdrawAsync(client, t.Dono, 1_001)));
        Assert.Equal((0L, 0L, 0L), (await CountAsync("movimentos_caixa"), await CountAsync("auditoria_registros WHERE acao IN ('caixa:suprimento', 'caixa:sangria', 'acesso:negar')"), await CountAsync("outbox_eventos WHERE tipo IN ('caixa.suprimento', 'caixa.sangria')")));

        // The whole drawer can go (the limit is inclusive); one cent more cannot.
        Assert.Equal(HttpStatusCode.Created, (await WithdrawAsync(client, t.Dono, 1_000)).StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "saldo-insuficiente"), await ErrorOfAsync(await WithdrawAsync(client, t.Dono, 1)));
        Assert.Equal(1L, await CountAsync("movimentos_caixa"));
    }

    [Fact]
    public async Task Supply_and_withdrawal_above_the_limit_of_the_profile_are_a_silent_403_the_withdrawal_denial_is_audited_and_nothing_is_written()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, _) = await CashierAndManagerAsync(api);
        await OpenedAsync(client, caixa, 50_000);

        using var supplyOver = await SupplyAsync(client, caixa, 10_001);
        Assert.Equal((HttpStatusCode.Forbidden, string.Empty), (supplyOver.StatusCode, await supplyOver.Content.ReadAsStringAsync()));
        using var withdrawOver = await WithdrawAsync(client, caixa, 10_001);
        Assert.Equal((HttpStatusCode.Forbidden, string.Empty), (withdrawOver.StatusCode, await withdrawOver.Content.ReadAsStringAsync()));
        Assert.Equal((0L, 0L, 0L), (await CountAsync("movimentos_caixa"), await CountAsync("outbox_eventos WHERE tipo IN ('caixa.suprimento', 'caixa.sangria')"), await CountAsync("auditoria_registros WHERE acao IN ('caixa:suprimento', 'caixa:sangria')")));
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' AND depois->>'permissao' = 'caixa:sangria'"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'acesso:negar' AND depois->>'permissao' = 'caixa:suprimento'"));

        // At the limit it goes through, for each.
        Assert.Equal(HttpStatusCode.Created, (await SupplyAsync(client, caixa, 10_000)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await WithdrawAsync(client, caixa, 10_000)).StatusCode);
        Assert.Equal(2L, await CountAsync("movimentos_caixa"));

        // The owner has no cap.
        await OpenedAsync(client, t.Dono, 500_000);
        Assert.Equal(HttpStatusCode.Created, (await WithdrawAsync(client, t.Dono, 300_000)).StatusCode);
    }

    [Fact]
    public async Task A_daily_cap_on_withdrawals_counts_only_what_was_granted_and_a_refusal_reserves_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        await SeedAsync(api);
        var capado = await GiveCaixaProfileAsync(api, Mudo, "Sangria com teto", Allow("caixa:ver"), Allow("caixa:abrir"), Allow("caixa:sangria", daily: 5_000));
        await OpenedAsync(client, capado, 20_000);

        Assert.Equal(HttpStatusCode.Created, (await WithdrawAsync(client, capado, 3_000)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await WithdrawAsync(client, capado, 3_000)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await WithdrawAsync(client, capado, 2_000)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await WithdrawAsync(client, capado, 1)).StatusCode);

        Assert.Equal((2L, 5_000L), (await CountAsync("movimentos_caixa"), await Single64Async("SELECT sum(valor_centavos) FROM movimentos_caixa")));
        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'acesso:negar' AND depois->>'permissao' = 'caixa:sangria'"));
    }

    [Fact]
    public async Task Without_the_permission_or_the_session_supply_and_withdrawal_are_refused_and_move_no_money()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var seeOnly = await GiveCaixaProfileAsync(api, Mudo, "So ve o caixa", Allow("caixa:ver"), Allow("caixa:abrir"));
        await OpenedAsync(client, seeOnly, 5_000);

        // The route asks for caixa:ver; the limited permission is the engine's question in the handler. The waiter has no caixa:ver at all, the other has caixa:ver but not the movement.
        foreach (var token in new[] { t.Garcom, seeOnly })
        {
            using var supply = await SupplyAsync(client, token, 100);
            using var withdraw = await WithdrawAsync(client, token, 100);
            Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (supply.StatusCode, withdraw.StatusCode));
            Assert.Equal((string.Empty, string.Empty), (await supply.Content.ReadAsStringAsync(), await withdraw.Content.ReadAsStringAsync()));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SupplyAsync(client, null, 100)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithdrawAsync(client, null, 100)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual/resumo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/caixa/historico", null)).StatusCode);
        Assert.Equal(0L, await CountAsync("movimentos_caixa"));
    }

    [Fact]
    public async Task Closing_inside_the_tolerance_concludes_alone_with_a_count_per_method_and_above_it_waits_for_approval()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var metodos = await MethodsAsync(client, t.Dono);
        var (pix, credito, dinheiro) = (MetodoId(metodos, "pix"), MetodoId(metodos, "credito"), MetodoId(metodos, "dinheiro"));
        await OpenedAsync(client, t.Dono, 10_000);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1250, 2);
        await ReceiveOkAsync(client, t.Dono, comanda, versao, Linha(pix, 1_000), Linha(credito, 500, 100), Linha(dinheiro, 1_250));

        // Before counting, the open shift shows what each non-cash method should add up to (the tip belongs to the machine's total, the cash is the drawer's).
        var resumo = await BodyAsync<ResumoBody>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual/resumo", t.Dono), HttpStatusCode.OK);
        Assert.Equal(11_250L, resumo.EsperadoCentavos);
        Assert.Equal([("Crédito", 600L), ("Pix", 1_000L)], resumo.PorMetodo.Select(m => (m.MetodoNome, m.EsperadoCentavos!.Value)));

        // Counts that cannot be accepted leave the shift open and write nothing: the cash method, an unknown one, a repeated one, a negative one.
        foreach (var contagens in new[]
        {
            new object[] { new { metodoId = dinheiro, contadoCentavos = 100 } },
            [new { metodoId = Guid.NewGuid(), contadoCentavos = 100 }],
            [new { metodoId = pix, contadoCentavos = 100 }, new { metodoId = pix, contadoCentavos = 200 }],
            [new { metodoId = pix, contadoCentavos = -1 }],
        })
        {
            Assert.Equal((HttpStatusCode.BadRequest, "invalido"), await ErrorOfAsync(await CloseShiftAsync(client, t.Dono, 11_250, contagens)));
        }

        Assert.Equal((1L, 0L), (await CountAsync("turnos_caixa WHERE fechado_em IS NULL"), await CountAsync("contagens_turno")));

        // R$ 0,50 short on the Crédito machine, the rest exact: total difference 50, inside the default tolerance of 500.
        var fechado = await ClosedAsync(client, t.Dono, 11_250, new { metodoId = pix, contadoCentavos = 1_000 }, new { metodoId = credito, contadoCentavos = 550 });
        Assert.Equal(("concluida", 0L, 11_250L), (fechado.Situacao, fechado.DiferencaCentavos, fechado.EsperadoCentavos));
        Assert.Equal([("Crédito", 550L, 600L, -50L), ("Pix", 1_000L, 1_000L, 0L)], fechado.Contagens!.Select(c => (c.MetodoNome, c.ContadoCentavos, c.EsperadoCentavos, c.DiferencaCentavos)));
        Assert.Equal(2L, await CountAsync("contagens_turno WHERE sequencia = 1"));
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT depois->>'diferencaTotalCentavos', depois->>'toleranciaCentavos', depois->>'situacao' FROM auditoria_registros WHERE acao = 'caixa:fechar'", r => (r.GetString(0), r.GetString(1), r.GetString(2))));
        Assert.Equal(("50", "500", "concluida"), audit);

        // R$ 5,01 short in the cash is one cent above the tolerance: the shift closes, pending. A difference never blocks the closing.
        var pendente = await ClosedWithDifferenceAsync(client, t.Dono, 1_000, 499);
        Assert.Equal(("pendente", -501L), (pendente.Situacao, pendente.DiferencaCentavos));
        Assert.Equal(1L, await CountAsync("outbox_eventos WHERE tipo = 'caixa.fechado' AND payload->>'Situacao' = 'pendente'"));
        Assert.Equal([pendente.Id], (await BodyAsync<List<TurnoCompletoBody>>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/pendentes", t.Dono), HttpStatusCode.OK)).Select(x => x.Id));

        // The tolerance is a layered option: with it at R$ 20,00 a R$ 15,00 shortage concludes alone.
        await SetOptionAsync(client, t.Dono, "caixa.tolerancia-diferenca-centavos", "2000");
        Assert.Equal("concluida", (await ClosedWithDifferenceAsync(client, t.Dono, 5_000, 3_500)).Situacao);
    }

    [Fact]
    public async Task A_manager_inside_the_limit_approves_the_difference_audited_with_an_event_and_it_cannot_be_approved_twice()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, gerente) = await CashierAndManagerAsync(api);
        var pendente = await ClosedWithDifferenceAsync(client, caixa, 10_000, 8_000);
        Assert.Equal(("pendente", -2_000L), (pendente.Situacao, pendente.DiferencaCentavos));

        // The cashier has no caixa:ver-todos (route 403); an unknown shift and an open one are not pending.
        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(client, caixa, pendente.Id)).StatusCode);
        Assert.Equal((HttpStatusCode.NotFound, "nao-encontrado"), await ErrorOfAsync(await ApproveAsync(client, gerente, Guid.NewGuid())));
        Assert.Equal((HttpStatusCode.BadRequest, "invalido"), await ErrorOfAsync(await ApproveAsync(client, gerente, pendente.Id, null)));
        Assert.Equal((HttpStatusCode.BadRequest, "invalido"), await ErrorOfAsync(await ApproveAsync(client, gerente, pendente.Id, new string('x', 201))));
        Assert.Equal(0L, await CountAsync("aprovacoes_diferenca"));
        var aberto = await OpenedAsync(client, t.Dono, 100);
        Assert.Equal((HttpStatusCode.Conflict, "nao-pendente"), await ErrorOfAsync(await ApproveAsync(client, gerente, aberto.Id)));

        var aprovado = await BodyAsync<TurnoCompletoBody>(await ApproveAsync(client, gerente, pendente.Id, "  Quebra de troco  "), HttpStatusCode.OK);

        Assert.Equal(("aprovada", -2_000L), (aprovado.Situacao, aprovado.DiferencaCentavos));
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT tipo, aprovador_id, motivo, diferenca_antes_centavos, diferenca_depois_centavos FROM aprovacoes_diferenca", r => (r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4))));
        Assert.Equal(("aprovacao", Supervisor, "Quebra de troco", 2_000L, 2_000L), row);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_id, depois->>'motivo', depois->>'operadorId', depois->>'situacao' FROM auditoria_registros WHERE acao = 'caixa:aprovar-diferenca'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
        Assert.Equal((Supervisor, pendente.Id.ToString("D"), "Quebra de troco", Garcom2.ToString("D"), "aprovada"), audit);
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'caixa.diferenca-aprovada' AND payload->>'TurnoId' = '{pendente.Id:D}' AND payload->>'AprovadorId' = '{Supervisor:D}' AND payload->>'DiferencaCentavos' = '2000'"));
        Assert.Empty(await BodyAsync<List<TurnoCompletoBody>>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/pendentes", t.Dono), HttpStatusCode.OK));

        Assert.Equal((HttpStatusCode.Conflict, "nao-pendente"), await ErrorOfAsync(await ApproveAsync(client, gerente, pendente.Id)));
        Assert.Equal(1L, await CountAsync("aprovacoes_diferenca"));
        var detalhe = await BodyAsync<DetalheBody>(await SendAsync(client, HttpMethod.Get, $"/caixa/turnos/{pendente.Id:D}", t.Dono), HttpStatusCode.OK);
        Assert.Equal(["aprovacao"], detalhe.Aprovacoes.Select(a => a.Tipo));
    }

    [Fact]
    public async Task A_difference_above_the_limit_of_the_approver_or_without_the_permission_is_a_403_audited_and_rolled_back_the_owner_decides()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, gerente) = await CashierAndManagerAsync(api);
        var apenasVer = await GiveCaixaProfileAsync(api, Mudo, "Ve todos mas nao aprova", Allow("caixa:ver"), Allow("caixa:ver-todos"));
        var grande = await ClosedWithDifferenceAsync(client, caixa, 10_000, 5_000);
        Assert.Equal(-5_000L, grande.DiferencaCentavos);

        foreach (var token in new[] { gerente, apenasVer })
        {
            using var denied = await ApproveAsync(client, token, grande.Id);
            Assert.Equal((HttpStatusCode.Forbidden, string.Empty), (denied.StatusCode, await denied.Content.ReadAsStringAsync()));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await RecountAsync(client, apenasVer, grande.Id, 9_900)).StatusCode);

        // Rolled back: still pending, nothing recorded, no event; the denial is audited on its own.
        Assert.Equal(("pendente", 0L, 0L), (await TextAsync("SELECT situacao_conferencia FROM turnos_caixa"), await CountAsync("aprovacoes_diferenca"), await CountAsync("outbox_eventos WHERE tipo = 'caixa.diferenca-aprovada'")));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao IN ('caixa:aprovar-diferenca', 'caixa:recontar-diferenca')"));
        Assert.True(await CountAsync("auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' AND depois->>'permissao' = 'caixa:aprovar-diferenca'") >= 2);

        // The owner has no cap and holds the permission.
        Assert.Equal("aprovada", (await BodyAsync<TurnoCompletoBody>(await ApproveAsync(client, t.Dono, grande.Id), HttpStatusCode.OK)).Situacao);
        Assert.Equal([DonoA], await QueryAsync(OrgA, UnitA, "SELECT aprovador_id FROM aprovacoes_diferenca", r => r.GetGuid(0)));
    }

    [Fact]
    public async Task Whoever_closed_the_shift_cannot_approve_its_difference_unless_self_approval_is_switched_on()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var proprio = await ClosedWithDifferenceAsync(client, t.Dono, 10_000, 9_000);
        Assert.Equal(("pendente", -1_000L), (proprio.Situacao, proprio.DiferencaCentavos));

        Assert.Equal((HttpStatusCode.Conflict, "autoaprovacao-nao-permitida"), await ErrorOfAsync(await ApproveAsync(client, t.Dono, proprio.Id)));
        Assert.Equal((HttpStatusCode.Conflict, "autoaprovacao-nao-permitida"), await ErrorOfAsync(await RecountAsync(client, t.Dono, proprio.Id, 9_900)));
        Assert.Equal((0L, 0L), (await CountAsync("aprovacoes_diferenca"), await CountAsync("auditoria_registros WHERE acao IN ('caixa:aprovar-diferenca', 'caixa:recontar-diferenca')")));
        Assert.Equal(2L, await CountAsync($"auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' AND usuario_id = '{DonoA:D}' AND depois->>'permissao' = 'caixa:aprovar-diferenca' AND objeto_id = '{proprio.Id:D}'"));

        await SetOptionAsync(client, t.Dono, "caixa.permitir-autoaprovacao", "true");

        Assert.Equal("aprovada", (await BodyAsync<TurnoCompletoBody>(await ApproveAsync(client, t.Dono, proprio.Id), HttpStatusCode.OK)).Situacao);
        Assert.Equal((1L, 1L), (await CountAsync("aprovacoes_diferenca"), await CountAsync("auditoria_registros WHERE acao = 'caixa:aprovar-diferenca' AND usuario_id = '" + DonoA.ToString("D") + "'")));
    }

    [Fact]
    public async Task Recounting_within_the_tolerance_concludes_the_shift_above_it_keeps_it_pending_and_both_are_append_only_history()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, gerente) = await CashierAndManagerAsync(api);
        var pendente = await ClosedWithDifferenceAsync(client, caixa, 10_000, 7_000);
        Assert.Equal(-3_000L, pendente.DiferencaCentavos);

        Assert.Equal((HttpStatusCode.BadRequest, "invalido"), await ErrorOfAsync(await RecountAsync(client, gerente, pendente.Id, null)));
        Assert.Equal((HttpStatusCode.BadRequest, "invalido"), await ErrorOfAsync(await RecountAsync(client, gerente, pendente.Id, -1)));

        // Still R$ 40,00 short: recorded, still pending, and the difference is the new one.
        var ainda = await BodyAsync<TurnoCompletoBody>(await RecountAsync(client, gerente, pendente.Id, 6_000), HttpStatusCode.OK);
        Assert.Equal(("pendente", -4_000L, 6_000L), (ainda.Situacao, ainda.DiferencaCentavos, ainda.ContadoCentavos));

        // The limit is measured on the difference the shift had when the approver acted: R$ 40,00 is above his R$ 30,00, the owner recounts.
        Assert.Equal(HttpStatusCode.Forbidden, (await RecountAsync(client, gerente, pendente.Id, 9_800)).StatusCode);
        var resolvido = await BodyAsync<TurnoCompletoBody>(await RecountAsync(client, t.Dono, pendente.Id, 9_800, "Achei o dinheiro na gaveta"), HttpStatusCode.OK);

        Assert.Equal(("concluida", -200L, 9_800L, 10_000L), (resolvido.Situacao, resolvido.DiferencaCentavos, resolvido.ContadoCentavos, resolvido.EsperadoCentavos));
        Assert.Equal(
            [("recontagem", Supervisor, 3_000L, 4_000L), ("recontagem", DonoA, 4_000L, 200L)],
            (await QueryAsync(OrgA, UnitA, "SELECT tipo, aprovador_id, diferenca_antes_centavos, diferenca_depois_centavos FROM aprovacoes_diferenca ORDER BY criado_em, id", r => (r.GetString(0), r.GetGuid(1), r.GetInt64(2), r.GetInt64(3)))));
        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'caixa:recontar-diferenca'"));
        Assert.Equal(0L, await CountAsync("outbox_eventos WHERE tipo = 'caixa.diferenca-aprovada'"));
        Assert.Empty(await BodyAsync<List<TurnoCompletoBody>>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/pendentes", t.Dono), HttpStatusCode.OK));
        Assert.Equal((HttpStatusCode.Conflict, "nao-pendente"), await ErrorOfAsync(await RecountAsync(client, t.Dono, pendente.Id, 10_000)));
    }

    [Fact]
    public async Task Recounting_a_method_adds_a_new_sequence_and_keeps_the_first_count_in_the_database()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, _) = await CashierAndManagerAsync(api);
        var pix = MetodoId(await MethodsAsync(client, t.Dono), "pix");
        await OpenedAsync(client, caixa, 1_000);
        var pendente = await ClosedAsync(client, caixa, 1_000, new { metodoId = pix, contadoCentavos = 900 });
        Assert.Equal("pendente", pendente.Situacao);

        var depois = await BodyAsync<TurnoCompletoBody>(await RecountAsync(client, t.Dono, pendente.Id, 1_000, "Pix era 0", new { metodoId = pix, contadoCentavos = 0 }), HttpStatusCode.OK);

        Assert.Equal("concluida", depois.Situacao);
        Assert.Equal([(1, 900L), (2, 0L)], await QueryAsync(OrgA, UnitA, "SELECT sequencia, contado_centavos FROM contagens_turno ORDER BY sequencia", r => (r.GetInt32(0), r.GetInt64(1))));
        var detalhe = await BodyAsync<DetalheBody>(await SendAsync(client, HttpMethod.Get, $"/caixa/turnos/{pendente.Id:D}", t.Dono), HttpStatusCode.OK);
        Assert.Equal([0L], detalhe.Contagens.Select(c => c.ContadoCentavos));
    }

    [Fact]
    public async Task With_the_blind_option_on_whoever_holds_the_blind_permission_does_not_see_the_expected_value_until_the_count_is_submitted()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        string[] recebe = ["comanda:ver", "comanda:ver-comandas-de-outros", "comanda:receber-pagamento", "pagamento:registrar"];
        var cego = await GiveCaixaProfileAsync(api, Garcom2, "Caixa cego", [Allow("caixa:ver"), Allow("caixa:abrir"), Allow("caixa:fechar"), Allow("caixa:fechar-cego"), .. recebe.Select(p => Allow(p))]);
        var aberto = await GiveCaixaProfileAsync(api, Mudo, "Caixa sem conferencia cega", Allow("caixa:ver"), Allow("caixa:abrir"), Allow("caixa:fechar"));
        await OpenedAsync(client, cego, 10_000);
        await OpenedAsync(client, aberto, 2_000);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1);
        await PayOkAsync(client, cego, comanda, 1_100, versao);

        // Option off (the default): nobody is blind.
        var antes = await BodyAsync<ResumoBody>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual/resumo", cego), HttpStatusCode.OK);
        Assert.Equal((false, 11_100L, 1_100L), (antes.Cego, antes.EsperadoCentavos, antes.Turno.RecebidoDinheiroCentavos));
        await SetOptionAsync(client, t.Dono, "caixa.conferencia-cega", "true");

        var resumo = await BodyAsync<ResumoBody>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual/resumo", cego), HttpStatusCode.OK);
        Assert.Equal((true, null, true, 0L), (resumo.Cego, resumo.EsperadoCentavos, resumo.Turno.EsperadoOculto, resumo.Turno.RecebidoDinheiroCentavos));
        var atual = await BodyAsync<TurnoCompletoBody>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual", cego), HttpStatusCode.OK);
        Assert.Equal((true, 0L), (atual.EsperadoOculto, atual.RecebidoDinheiroCentavos));
        var propria = (await BodyAsync<List<TurnoCompletoBody>>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos", cego), HttpStatusCode.OK)).Single();
        Assert.Equal((true, 0L), (propria.EsperadoOculto, propria.RecebidoDinheiroCentavos));

        // Without the blind permission the same option changes nothing for that person.
        var vista = await BodyAsync<ResumoBody>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual/resumo", aberto), HttpStatusCode.OK);
        Assert.Equal((false, 2_000L, false), (vista.Cego, vista.EsperadoCentavos, vista.Turno.EsperadoOculto));

        // Submitting the count reveals the expected value of the closed shift.
        var fechado = await ClosedAsync(client, cego, 11_000);
        Assert.Equal((11_100L, -100L), (fechado.EsperadoCentavos, fechado.DiferencaCentavos));
    }

    [Fact]
    public async Task The_history_filters_by_operator_and_period_and_a_cashier_without_see_all_only_gets_the_own_shifts()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, gerente) = await CashierAndManagerAsync(api);
        var doCaixa = await ClosedWithDifferenceAsync(client, caixa, 1_000, 1_000);
        var doDono = await ClosedWithDifferenceAsync(client, t.Dono, 2_000, 2_000);
        await RunSqlAsync(OrgA, UnitA, $"UPDATE turnos_caixa SET aberto_em = '2026-01-10T12:00:00Z' WHERE id = '{doCaixa.Id:D}'");
        await RunSqlAsync(OrgA, UnitA, $"UPDATE turnos_caixa SET aberto_em = '2026-01-20T12:00:00Z' WHERE id = '{doDono.Id:D}'");

        async Task<List<Guid>> HistoryAsync(string token, string query) =>
            (await BodyAsync<List<TurnoCompletoBody>>(await SendAsync(client, HttpMethod.Get, "/caixa/historico" + query, token), HttpStatusCode.OK)).Select(x => x.Id).ToList();

        Assert.Equal([doDono.Id, doCaixa.Id], await HistoryAsync(gerente, string.Empty));
        Assert.Equal([doCaixa.Id], await HistoryAsync(gerente, $"?operadorId={Garcom2:D}"));
        Assert.Equal([doDono.Id], await HistoryAsync(gerente, "?de=2026-01-15T00:00:00Z"));
        Assert.Equal([doCaixa.Id], await HistoryAsync(gerente, "?ate=2026-01-15T00:00:00Z"));
        Assert.Equal([doDono.Id], await HistoryAsync(gerente, $"?operadorId={DonoA:D}&de=2026-01-01T00:00:00Z&ate=2026-02-01T00:00:00Z"));
        Assert.Empty(await HistoryAsync(gerente, "?de=2026-01-11T00:00:00Z&ate=2026-01-19T00:00:00Z"));

        // Without caixa:ver-todos the operator filter is ignored: only the own shifts, whoever was asked for.
        Assert.Equal([doCaixa.Id], await HistoryAsync(caixa, $"?operadorId={DonoA:D}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Get, "/caixa/historico?de=2026-02-01T00:00:00Z&ate=2026-01-01T00:00:00Z", gerente)).StatusCode);

        // The detail of someone else's shift is also behind caixa:ver-todos (404, not 403: it does not confirm the shift exists).
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, $"/caixa/turnos/{doDono.Id:D}", caixa)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, $"/caixa/turnos/{doDono.Id:D}", gerente)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/caixa/turnos/pendentes", caixa)).StatusCode);
    }

    [Fact]
    public async Task Another_organization_never_sees_moves_or_approves_the_shifts_movements_and_differences_of_this_one()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, _) = await CashierAndManagerAsync(api);
        await OpenedAsync(client, caixa, 10_000);
        await SupplyAsync(client, caixa, 100);
        var pendente = await ClosedAsync(client, caixa, 1_000);
        Assert.Equal("pendente", pendente.Situacao);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, $"/caixa/turnos/{pendente.Id:D}", t.DonoB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(client, t.DonoB, pendente.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RecountAsync(client, t.DonoB, pendente.Id, 10_000)).StatusCode);
        Assert.Empty(await BodyAsync<List<TurnoCompletoBody>>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/pendentes", t.DonoB), HttpStatusCode.OK));
        Assert.Empty(await BodyAsync<List<MovimentoDeCaixaBody>>(await SendAsync(client, HttpMethod.Get, "/caixa/turnos/atual/movimentos", t.DonoB), HttpStatusCode.OK));
        Assert.Equal((HttpStatusCode.NotFound, "sem-turno-aberto"), await ErrorOfAsync(await WithdrawAsync(client, t.DonoB, 1)));
        Assert.Equal((0L, 0L, 0L), (
            (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM movimentos_caixa", r => r.GetInt64(0))).Single(),
            (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM contagens_turno", r => r.GetInt64(0))).Single(),
            (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM aprovacoes_diferenca", r => r.GetInt64(0))).Single()));
        Assert.Equal("pendente", await TextAsync("SELECT situacao_conferencia FROM turnos_caixa"));
    }

    [Fact]
    public async Task The_movement_count_and_approval_tables_are_append_only_for_the_application_credential_and_the_closed_shift_needs_its_figures()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, gerente) = await CashierAndManagerAsync(api);
        var pix = MetodoId(await MethodsAsync(client, t.Dono), "pix");
        await OpenedAsync(client, caixa, 10_000);
        await SupplyAsync(client, caixa, 500);
        var pendente = await ClosedAsync(client, caixa, 9_000, new { metodoId = pix, contadoCentavos = 10 });
        await ApproveAsync(client, gerente, pendente.Id);

        foreach (var table in new[] { "movimentos_caixa", "contagens_turno", "aprovacoes_diferenca" })
        {
            Assert.Equal(1L, await CountAsync(table));
            var update = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE {table} SET criado_em = now()"));
            var delete = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"DELETE FROM {table}"));
            Assert.Equal(("42501", "42501"), (update.SqlState, delete.SqlState));
            Assert.Equal(1L, await CountAsync(table));
        }

        // The database itself refuses a withdrawal without destination and a figure on an open shift.
        var semDestino = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"INSERT INTO movimentos_caixa (id, organizacao_id, unidade_id, turno_id, tipo, valor_centavos, motivo, operador_id, criado_em) VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', '{UnitA:D}', '{pendente.Id:D}', 'sangria', 1, 'x', '{Garcom2:D}', now())"));
        Assert.Equal("23514", semDestino.SqlState);
        await OpenedAsync(client, t.Dono, 1);
        var aberto = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE turnos_caixa SET situacao_conferencia = 'pendente' WHERE operador_id = '{DonoA:D}' AND fechado_em IS NULL"));
        Assert.Equal("23514", aberto.SqlState);
    }

    [Fact]
    public async Task Two_approvers_racing_on_the_same_difference_leave_one_approval_and_two_withdrawals_never_overdraw_the_drawer()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, gerente) = await CashierAndManagerAsync(api);
        var pendente = await ClosedWithDifferenceAsync(client, caixa, 10_000, 9_000);

        var approvals = await Task.WhenAll(new[] { gerente, t.Dono, gerente, t.Dono }.Select(async token => (await ErrorOfAsync(await ApproveAsync(client, token, pendente.Id))).Status));

        Assert.Equal(1, approvals.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(3, approvals.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal((1L, 1L, 1L), (await CountAsync("aprovacoes_diferenca"), await CountAsync("auditoria_registros WHERE acao = 'caixa:aprovar-diferenca'"), await CountAsync("outbox_eventos WHERE tipo = 'caixa.diferenca-aprovada'")));

        await OpenedAsync(client, t.Dono, 10_000);
        var withdrawals = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => (await ErrorOfAsync(await WithdrawAsync(client, t.Dono, 6_000))).Status));

        Assert.Equal(1, withdrawals.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(3, withdrawals.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(6_000L, await Single64Async($"SELECT sum(valor_centavos) FROM movimentos_caixa WHERE operador_id = '{DonoA:D}'"));
    }

    [Fact]
    public async Task Two_recounts_racing_on_the_same_shift_are_decided_one_after_the_other_each_seeing_what_the_other_left()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var (caixa, _) = await CashierAndManagerAsync(api);
        var outro = await GiveCaixaProfileAsync(api, Mudo, "Aprovador sem teto", Allow("caixa:ver"), Allow("caixa:ver-todos"), Allow("caixa:aprovar-diferenca"));
        var pendente = await ClosedWithDifferenceAsync(client, caixa, 10_000, 7_000);

        var statuses = await Task.WhenAll(
            Task.Run(async () => (await ErrorOfAsync(await RecountAsync(client, t.Dono, pendente.Id, 6_000))).Status),
            Task.Run(async () => (await ErrorOfAsync(await RecountAsync(client, outro, pendente.Id, 5_000))).Status));

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        var rows = await QueryAsync(OrgA, UnitA, "SELECT diferenca_antes_centavos, diferenca_depois_centavos FROM aprovacoes_diferenca ORDER BY criado_em, id", r => (r.GetInt64(0), r.GetInt64(1)));
        Assert.Equal(2, rows.Count);
        Assert.Equal(3_000L, rows[0].Item1);
        Assert.Equal(rows[0].Item2, rows[1].Item1);
    }

    [Fact]
    public async Task A_pix_payment_racing_the_closing_is_never_lost_from_the_expected_value_of_the_method()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var pix = MetodoId(await MethodsAsync(client, t.Dono), "pix");
        await OpenedAsync(client, t.Dono, 1_000);
        var comandas = new List<(Guid Id, int Versao)>();
        for (var i = 0; i < 4; i++)
        {
            var (id, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 1, "PIX" + i);
            comandas.Add((id, versao));
        }

        var tarefas = comandas.Select(c => Task.Run(async () => (await ErrorOfAsync(await ReceiveAsync(client, t.Dono, c.Id, c.Versao, Linha(pix, 1_100)))).Status)).ToList();
        tarefas.Add(Task.Run(async () => (await ErrorOfAsync(await CloseShiftAsync(client, t.Dono, 1_000, new { metodoId = pix, contadoCentavos = 0 }))).Status));
        var status = await Task.WhenAll(tarefas);

        Assert.Equal(HttpStatusCode.OK, status[^1]);
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, status);
        // Whatever won the race, a payment recorded in the shift is in the Pix expectation of its count, and one refused is in none of them.
        var gravado = await Single64Async("SELECT COALESCE(sum(aplicado_centavos + gorjeta_centavos), 0)::bigint FROM pagamentos WHERE metodo = 'pix'");
        var esperado = await Single64Async("SELECT COALESCE(sum(esperado_centavos), 0)::bigint FROM contagens_turno");
        Assert.Equal(gravado, esperado);
        Assert.Equal(1L, await CountAsync("contagens_turno"));
    }

    [Fact]
    public async Task The_database_refuses_a_movement_or_an_approval_whose_unit_is_not_the_unit_of_the_shift()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenedAsync(client, t.Dono, 1_000);
        var turno = (await QueryAsync(OrgA, UnitA, "SELECT id FROM turnos_caixa", r => r.GetGuid(0))).Single();

        var movimento = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"INSERT INTO movimentos_caixa (id, organizacao_id, unidade_id, turno_id, tipo, valor_centavos, motivo, operador_id, criado_em) VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', '{UnitA2:D}', '{turno:D}', 'suprimento', 1, 'x', '{DonoA:D}', now())"));
        var aprovacao = await Assert.ThrowsAsync<PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"INSERT INTO aprovacoes_diferenca (id, organizacao_id, unidade_id, turno_id, tipo, aprovador_id, motivo, diferenca_antes_centavos, diferenca_depois_centavos, criado_em) VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', '{UnitA2:D}', '{turno:D}', 'aprovacao', '{DonoA:D}', 'x', 1, 1, now())"));

        Assert.Equal(("23503", "23503"), (movimento.SqlState, aprovacao.SqlState));
    }

    [Fact]
    public async Task A_withdrawal_whose_event_fails_rolls_back_the_movement_and_the_audit_record_together()
    {
        await using var api = new Api(_appConnection, configure: services =>
        {
            services.Remove(services.Single(d => d.ServiceType == typeof(IEventosDoCaixa)));
            services.AddSingleton<IEventosDoCaixa>(p => new CaixaEventsThatThrow(ActivatorUtilities.CreateInstance<OutboxEventosDoCaixa>(p)));
        });
        using var client = api.CreateClient();
        await SeedAsync(api);
        await RunSqlAsync(OrgA, UnitA, $"INSERT INTO turnos_caixa (id, organizacao_id, unidade_id, operador_id, aberto_em, fundo_centavos) VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', '{UnitA:D}', '{DonoA:D}', now(), 5000)");
        var gestao = api.Services.GetRequiredService<GestaoDeMovimentosDoCaixa>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gestao.SangrarAsync(new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA)), 1_000, "Cofre", "Cofre"));

        Assert.Equal((0L, 0L, 0L), (await CountAsync("movimentos_caixa"), await CountAsync("auditoria_registros WHERE acao = 'caixa:sangria'"), await CountAsync("outbox_eventos WHERE tipo = 'caixa.sangria'")));
    }

    private sealed class CaixaEventsThatThrow(IEventosDoCaixa inner) : IEventosDoCaixa
    {
        public async Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
        {
            await inner.PublicarAsync(organization, evento, cancellationToken);
            throw new InvalidOperationException("boom after outbox");
        }
    }

    private static class RegrasDeValor
    {
        public const long Acima = Flow4Tech.Caixa.Domain.RegrasDoCaixa.ValorMaximoCentavos + 1;
    }

    private async Task<long> Single64Async(string sql) => (await QueryAsync(OrgA, UnitA, sql, r => r.GetInt64(0))).Single();

    private async Task<string> TextAsync(string sql) => (await QueryAsync(OrgA, UnitA, sql, r => r.GetString(0))).Single();
}
