using System.Net;
using System.Net.Http.Json;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0067: the pre-bill, the second copy of the receipt and the "bill requested" mark through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions). The owner holds
/// every permission; the waiter ("Garcom") holds <c>comanda:ver</c> but not <c>comanda:ver-comandas-de-outros</c> nor <c>comanda:reimprimir</c>; the supervisor sees other waiters' comandas but cannot reprint; "Mudo"
/// has no permission. Comanda of the tests: three items of R$ 10,00 and a 10% fee, so the bill is R$ 33,00.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record ItemDoComprovanteBody(string Descricao, int Quantidade, long UnitarioCentavos, long TotalCentavos, string Tipo, string? Observacao, List<string> Opcoes);

    private sealed record DescontoDoComprovanteBody(string Alvo, string Forma, decimal? Percentual, long AplicadoCentavos, string Motivo);

    private sealed record ContaDoComprovanteBody(long ConsumoCentavos, long DescontoCentavos, long TaxaServicoCentavos, bool TaxaServicoRemovida, decimal TaxaServicoPercentual, long TotalCentavos, long PagoCentavos, long SaldoCentavos);

    private sealed record PagamentoDoComprovanteBody(string MetodoNome, long AplicadoCentavos, long TrocoCentavos, long GorjetaCentavos, DateTimeOffset RegistradoEm, int? DivisaoParte);

    private sealed record ParteDoComprovanteBody(int Numero, long ConsumoCentavos, long TaxaServicoCentavos, long TotalCentavos, long GorjetaCentavos, long PagoCentavos);

    private sealed record ComprovanteBody(
        string Tipo,
        bool SegundaVia,
        string Aviso,
        int Numero,
        string NumeroTexto,
        string Identificacao,
        string DiaOperacional,
        DateTimeOffset AbertaEm,
        DateTimeOffset? FechadaEm,
        DateTimeOffset? ContaPedidaEm,
        List<ItemDoComprovanteBody> Itens,
        List<DescontoDoComprovanteBody> Descontos,
        ContaDoComprovanteBody Conta,
        List<PagamentoDoComprovanteBody> Pagamentos,
        List<ParteDoComprovanteBody>? Partes);

    private sealed record PedidaBody(Guid Id, string Estado, int Versao, DateTimeOffset? ContaPedidaEm);

    private static Task<HttpResponseMessage> PreBillAsync(HttpClient client, string? token, Guid comanda, bool texto = false) =>
        SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/pre-conta{(texto ? "/texto" : string.Empty)}", token);

    private static Task<HttpResponseMessage> ReceiptAsync(HttpClient client, string? token, Guid comanda, bool texto = false) =>
        SendAsync(client, HttpMethod.Get, $"/comandas/{comanda:D}/recibo{(texto ? "/texto" : string.Empty)}", token);

    private static Task<HttpResponseMessage> AskForBillAsync(HttpClient client, string? token, Guid comanda) =>
        SendAsync(client, HttpMethod.Post, $"/comandas/{comanda:D}/pedir-conta", token);

    private static async Task<string> StatusOfTableAsync(HttpClient client, string token, Guid mesa)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/mesas", token);
        return (await response.Content.ReadFromJsonAsync<List<MesaBody>>())!.Single(m => m.Id == mesa).Status;
    }

    [Fact]
    public async Task The_pre_bill_shows_the_frozen_items_discounts_fee_total_and_the_parts_of_a_split_and_writes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var c = await Bill3Async(client, t, "PRE1");
        var desconto = await BodyAsync<DescontoAplicadoBody>(await ValueAsync(client, t.Dono, c.ComandaId, c.Versao, 300, c.Itens[0].Id, "casa"), HttpStatusCode.Created);
        var dividida = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, Guid.CreateVersion7(), desconto.Conta.Versao, Equal(2, 100)), HttpStatusCode.Created);
        var versaoAntes = await VersionOfAsync(c.ComandaId);
        var auditoriasAntes = await CountAsync("auditoria_registros");

        // The waiter who opened it reads it (comanda:ver): 3 x R$ 10,00, R$ 3,00 off the first, fee on R$ 27,00 (R$ 2,70), total R$ 29,70, two parts.
        var pre = await BodyAsync<ComprovanteBody>(await PreBillAsync(client, t.Garcom, c.ComandaId), HttpStatusCode.OK);

        Assert.Equal(("pre-conta", false, "Sem valor fiscal", "#1", null), (pre.Tipo, pre.SegundaVia, pre.Aviso, pre.NumeroTexto, pre.ContaPedidaEm));
        Assert.Equal([1000L, 1000L, 1000L], pre.Itens.Select(i => i.TotalCentavos));
        Assert.Equal(["Produto PRE1"], pre.Itens.Select(i => i.Descricao).Distinct());
        Assert.Equal((3000L, 300L, 270L, 2970L, 0L, 2970L), (pre.Conta.ConsumoCentavos, pre.Conta.DescontoCentavos, pre.Conta.TaxaServicoCentavos, pre.Conta.TotalCentavos, pre.Conta.PagoCentavos, pre.Conta.SaldoCentavos));
        var d = Assert.Single(pre.Descontos);
        Assert.Equal(("Produto PRE1", "valor", 300L, "casa"), (d.Alvo, d.Forma, d.AplicadoCentavos, d.Motivo));
        Assert.NotNull(pre.Partes);
        Assert.Equal(dividida.Partes.Select(p => (p.Numero, p.TotalCentavos, p.GorjetaCentavos)), pre.Partes.Select(p => (p.Numero, p.TotalCentavos, p.GorjetaCentavos)));
        Assert.Equal(pre.Conta.TotalCentavos, pre.Partes.Sum(p => p.TotalCentavos));

        // The plain document, for sharing, says the same and is text/plain.
        using var texto = await PreBillAsync(client, t.Garcom, c.ComandaId, texto: true);
        Assert.Equal(HttpStatusCode.OK, texto.StatusCode);
        Assert.StartsWith("text/plain", texto.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        var corpo = await texto.Content.ReadAsStringAsync();
        Assert.Contains("PRÉ-CONTA", corpo, StringComparison.Ordinal);
        Assert.Contains("Sem valor fiscal", corpo, StringComparison.Ordinal);
        Assert.Contains("R$ 29,70", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain(Garcom.ToString("D"), corpo, StringComparison.Ordinal);

        // Reading wrote nothing: no version bump, no audit record, no event; and it carries no account id.
        Assert.Equal((versaoAntes, auditoriasAntes), (await VersionOfAsync(c.ComandaId), await CountAsync("auditoria_registros")));
        Assert.DoesNotContain(Garcom.ToString("D"), await (await PreBillAsync(client, t.Garcom, c.ComandaId)).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Denied and other tenants: no session, no permission, another waiter's comanda, another organization.
        using (var anonima = await PreBillAsync(client, null, c.ComandaId))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonima.StatusCode);
        }

        using (var semPermissao = await PreBillAsync(client, t.Mudo, c.ComandaId))
        {
            await AssertForbiddenEmptyAsync(semPermissao);
        }

        await AssertErrorAsync(await PreBillAsync(client, t.Garcom2, c.ComandaId), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await PreBillAsync(client, t.DonoB, c.ComandaId), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await PreBillAsync(client, t.Dono, Guid.NewGuid()), HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal(HttpStatusCode.OK, (await PreBillAsync(client, t.Supervisor, c.ComandaId)).StatusCode);
    }

    [Fact]
    public async Task Asking_for_the_bill_is_idempotent_audited_once_puts_the_table_waiting_and_closing_or_cancelling_clears_it()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 1000);
        var mesa = await CreateTableAsync(client, t.Dono, "7");
        var comanda = await OpenAsync(client, t.Garcom, new { mesaId = mesa.Id });
        var produto = await CreateProductAsync(client, t.Dono, "PED1", "Produto PED1", 1000);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, 1);
        Assert.Equal("ocupada", await StatusOfTableAsync(client, t.Garcom, mesa.Id));

        var pedida = await BodyAsync<PedidaBody>(await AskForBillAsync(client, t.Garcom, comanda.Id), HttpStatusCode.Created);

        Assert.NotNull(pedida.ContaPedidaEm);
        Assert.Equal(("aberta", rodada.ComandaVersao), (pedida.Estado, pedida.Versao));
        Assert.Equal("aguardando-conta", await StatusOfTableAsync(client, t.Garcom, mesa.Id));
        Assert.Equal(1L, await CountAsync($"auditoria_registros WHERE acao = 'comanda:pedir-conta' AND objeto_id = '{comanda.Id:D}' AND usuario_id = '{Garcom:D}'"));
        Assert.Equal(1L, await CountAsync($"outbox_eventos WHERE tipo = 'comanda.conta-pedida' AND payload->>'ComandaId' = '{comanda.Id:D}'"));
        Assert.Equal(rodada.ComandaVersao, await VersionOfAsync(comanda.Id));

        // Again: the same mark, 200, no new record, event or version.
        var repetida = await BodyAsync<PedidaBody>(await AskForBillAsync(client, t.Garcom, comanda.Id), HttpStatusCode.OK);
        Assert.Equal(pedida.ContaPedidaEm, repetida.ContaPedidaEm);
        Assert.Equal((1L, 1L), (await CountAsync("auditoria_registros WHERE acao = 'comanda:pedir-conta'"), await CountAsync("outbox_eventos WHERE tipo = 'comanda.conta-pedida'")));

        // The pre-bill and the list carry the mark.
        var pre = await BodyAsync<ComprovanteBody>(await PreBillAsync(client, t.Garcom, comanda.Id), HttpStatusCode.OK);
        Assert.Equal(pedida.ContaPedidaEm, pre.ContaPedidaEm);
        using (var lista = await SendAsync(client, HttpMethod.Get, "/comandas", t.Garcom))
        {
            Assert.Contains("\"contaPedidaEm\":\"20", await lista.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Closing clears it (the table is free again).
        var paga = await PayOkAsync(client, t.Dono, comanda.Id, 1100, rodada.ComandaVersao);
        using (var fechada = await CloseAsync(client, t.Dono, comanda.Id, paga.ComandaVersao))
        {
            Assert.Equal(HttpStatusCode.OK, fechada.StatusCode);
        }

        Assert.Equal("livre", await StatusOfTableAsync(client, t.Garcom, mesa.Id));
        Assert.Equal(0L, await CountAsync($"comandas WHERE id = '{comanda.Id:D}' AND conta_pedida_em IS NOT NULL"));
        await AssertErrorAsync(await AskForBillAsync(client, t.Garcom, comanda.Id), HttpStatusCode.Conflict, "comanda-nao-aberta");

        // Cancelling clears it too.
        var outra = await OpenAsync(client, t.Garcom, new { mesaId = mesa.Id });
        await BodyAsync<PedidaBody>(await AskForBillAsync(client, t.Garcom, outra.Id), HttpStatusCode.Created);
        Assert.Equal("aguardando-conta", await StatusOfTableAsync(client, t.Garcom, mesa.Id));
        using (var cancelada = await CancelComandaAsync(client, t.Dono, outra.Id, outra.Versao))
        {
            Assert.Equal(HttpStatusCode.OK, cancelada.StatusCode);
        }

        Assert.Equal("livre", await StatusOfTableAsync(client, t.Garcom, mesa.Id));
        Assert.Equal(0L, await CountAsync("comandas WHERE conta_pedida_em IS NOT NULL"));
    }

    [Fact]
    public async Task Asking_for_the_bill_is_denied_without_the_session_or_the_permission_and_does_not_reach_another_waiter_or_organization()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var comanda = await OpenAsync(client, t.Garcom);

        using (var anonima = await AskForBillAsync(client, null, comanda.Id))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonima.StatusCode);
        }

        using (var semPermissao = await AskForBillAsync(client, t.Mudo, comanda.Id))
        {
            await AssertForbiddenEmptyAsync(semPermissao);
        }

        await AssertErrorAsync(await AskForBillAsync(client, t.Garcom2, comanda.Id), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErrorAsync(await AskForBillAsync(client, t.DonoB, comanda.Id), HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal(0L, await CountAsync("comandas WHERE conta_pedida_em IS NOT NULL"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'comanda:pedir-conta'"));

        // The database refuses a mark on a closed comanda, and a mark without its author.
        var erro = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'fechada', fechada_em = now(), conta_pedida_em = now(), conta_pedida_por = '{DonoA:D}' WHERE id = '{comanda.Id:D}'"));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, erro.SqlState);
        var semAutor = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET conta_pedida_em = now() WHERE id = '{comanda.Id:D}'"));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, semAutor.SqlState);
    }

    [Fact]
    public async Task Two_simultaneous_requests_for_the_bill_leave_one_mark_one_audit_record_and_one_event()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var comanda = await OpenAsync(client, t.Garcom);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AskForBillAsync(client, t.Garcom, comanda.Id)));

        Assert.All(respostas, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal((1L, 1L), (await CountAsync("auditoria_registros WHERE acao = 'comanda:pedir-conta'"), await CountAsync("outbox_eventos WHERE tipo = 'comanda.conta-pedida'")));
        foreach (var r in respostas)
        {
            r.Dispose();
        }
    }

    [Fact]
    public async Task The_second_copy_of_the_receipt_shows_the_payments_and_the_split_is_audited_per_copy_and_needs_comanda_reimprimir()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenShiftAsync(client, t.Dono, 1000);
        var c = await Bill3Async(client, t, "REC1");
        var divisaoId = Guid.CreateVersion7();
        var dividida = await BodyAsync<DivisaoBody>(await SplitAsync(client, t.Dono, c.ComandaId, divisaoId, c.Versao, Equal(2)), HttpStatusCode.Created);
        var metodos = await MethodsAsync(client, t.Dono);
        var parte1 = dividida.Partes[0].APagarCentavos;
        var parte2 = dividida.Partes[1].APagarCentavos;
        var primeira = await ReceiveOkAsync(client, t.Dono, c.ComandaId, dividida.ComandaVersao, Linha(MetodoId(metodos, "pix"), parte1, divisao: divisaoId, parte: 1));
        var segunda = await ReceiveOkAsync(client, t.Dono, c.ComandaId, primeira.Conta.Versao, Linha(MetodoId(metodos, "dinheiro"), parte2 + 500, divisao: divisaoId, parte: 2));

        // Open: no receipt yet.
        await AssertErrorAsync(await ReceiptAsync(client, t.Dono, c.ComandaId), HttpStatusCode.Conflict, "comanda-nao-fechada");
        using (var fechada = await CloseAsync(client, t.Dono, c.ComandaId, segunda.Conta.Versao))
        {
            Assert.Equal(HttpStatusCode.OK, fechada.StatusCode);
        }

        var recibo = await BodyAsync<ComprovanteBody>(await ReceiptAsync(client, t.Dono, c.ComandaId), HttpStatusCode.OK);

        Assert.Equal(("recibo", true, "Sem valor fiscal"), (recibo.Tipo, recibo.SegundaVia, recibo.Aviso));
        Assert.NotNull(recibo.FechadaEm);
        Assert.Equal((3000L, 300L, 3300L, 3300L, 0L), (recibo.Conta.ConsumoCentavos, recibo.Conta.TaxaServicoCentavos, recibo.Conta.TotalCentavos, recibo.Conta.PagoCentavos, recibo.Conta.SaldoCentavos));
        Assert.Equal([("Pix", parte1, 0L, 1), ("Dinheiro", parte2, 500L, 2)], recibo.Pagamentos.Select(p => (p.MetodoNome, p.AplicadoCentavos, p.TrocoCentavos, p.DivisaoParte)));
        Assert.Equal([(1, parte1, parte1), (2, parte2, parte2)], recibo.Partes!.Select(p => (p.Numero, p.TotalCentavos, p.PagoCentavos)));

        using var texto = await ReceiptAsync(client, t.Dono, c.ComandaId, texto: true);
        Assert.Equal(HttpStatusCode.OK, texto.StatusCode);
        var corpo = await texto.Content.ReadAsStringAsync();
        Assert.Contains("RECIBO - 2ª VIA", corpo, StringComparison.Ordinal);
        Assert.Contains("Pix", corpo, StringComparison.Ordinal);
        Assert.Contains("R$ 33,00", corpo, StringComparison.Ordinal);

        // Every copy is audited (two reads so far), by the one who asked; the first copy left no mark of its own.
        Assert.Equal(2L, await CountAsync($"auditoria_registros WHERE acao = 'comanda:reimprimir' AND objeto_id = '{c.ComandaId:D}' AND usuario_id = '{DonoA:D}'"));

        // Denied: the waiter (no permission), the supervisor (sees all but cannot reprint), the account with nothing, no session. Another organization sees nothing.
        foreach (var token in new[] { t.Garcom, t.Supervisor, t.Mudo })
        {
            foreach (var texto2 in new[] { false, true })
            {
                using var negada = await ReceiptAsync(client, token, c.ComandaId, texto2);
                await AssertForbiddenEmptyAsync(negada);
            }
        }

        using (var anonima = await ReceiptAsync(client, null, c.ComandaId))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonima.StatusCode);
        }

        await AssertErrorAsync(await ReceiptAsync(client, t.DonoB, c.ComandaId), HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'comanda:reimprimir'"));

        // A cancelled comanda has no receipt, and the pre-bill of a closed one is refused.
        var cancelada = await OpenAsync(client, t.Garcom);
        using (var cancelou = await CancelComandaAsync(client, t.Dono, cancelada.Id, cancelada.Versao))
        {
            Assert.Equal(HttpStatusCode.OK, cancelou.StatusCode);
        }

        await AssertErrorAsync(await ReceiptAsync(client, t.Dono, cancelada.Id), HttpStatusCode.Conflict, "comanda-nao-fechada");
        await AssertErrorAsync(await PreBillAsync(client, t.Dono, c.ComandaId), HttpStatusCode.Conflict, "comanda-nao-aberta");
    }
}
