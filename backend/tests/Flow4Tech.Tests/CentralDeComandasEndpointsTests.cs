using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0141: the central de comandas query (<c>GET /comandas/central</c>, read-only, no audit) through the real authenticated route, real PostgreSQL, application credential (RLS on). Covers the filters
/// (waiter, table, open time, number), the unit of the session, another organization, 401/403, a waiter limited to their own, and the total matching the rows.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record CentralBody(List<ComandaBody> Comandas, int Total);

    private static async Task<CentralBody> CentralAsync(HttpClient client, string token, string query = "")
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/comandas/central" + query, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CentralBody>())!;
    }

    /// <summary>The rows (in order) and the total the central answered.</summary>
    private static void Confere(CentralBody central, int total, params Guid[] ids)
    {
        Assert.Equal(ids, central.Comandas.Select(c => c.Id));
        Assert.Equal(total, central.Total);
    }

    private async Task SqlAsync(Guid org, Guid unit, string sql)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        await command.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    [Fact]
    public async Task The_central_filters_by_waiter_table_open_time_and_number_and_the_total_matches_the_rows()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await CreateTableAsync(client, t.Dono, "5");
        var velha = await OpenAsync(client, t.Garcom, new { mesaId = mesa.Id });
        var meiaHora = await OpenAsync(client, t.Garcom2);
        var nova = await OpenAsync(client, t.Garcom);
        await SqlAsync(OrgA, UnitA, $"UPDATE comandas SET aberta_em = now() - interval '2 hours' WHERE id = '{velha.Id:D}'");
        await SqlAsync(OrgA, UnitA, $"UPDATE comandas SET aberta_em = now() - interval '31 minutes' WHERE id = '{meiaHora.Id:D}'");

        var todas = await CentralAsync(client, t.Supervisor);
        var doGarcom = await CentralAsync(client, t.Supervisor, $"?garcomId={Garcom}");
        var daMesa = await CentralAsync(client, t.Supervisor, $"?mesaId={mesa.Id}");
        var abertasHaMuito = await CentralAsync(client, t.Supervisor, "?tempoMinimoMinutos=30");
        var haDuasHoras = await CentralAsync(client, t.Supervisor, "?tempoMinimoMinutos=120");
        var numero = await CentralAsync(client, t.Supervisor, $"?numero={nova.Numero}");
        var combinada = await CentralAsync(client, t.Supervisor, $"?garcomId={Garcom}&tempoMinimoMinutos=30");
        var nenhuma = await CentralAsync(client, t.Supervisor, $"?garcomId={Garcom2}&mesaId={mesa.Id}");

        Confere(todas, 3, nova.Id, meiaHora.Id, velha.Id);
        Confere(doGarcom, 2, nova.Id, velha.Id);
        Confere(daMesa, 1, velha.Id);
        Confere(abertasHaMuito, 2, meiaHora.Id, velha.Id);
        Confere(haDuasHoras, 1, velha.Id);
        Confere(numero, 1, nova.Id);
        Confere(combinada, 1, velha.Id);
        Confere(nenhuma, 0);
    }

    [Fact]
    public async Task The_central_never_shows_another_unit_or_another_organization()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var daqui = await OpenAsync(client, t.Garcom);
        await OpenAsync(client, t.DonoB);
        await SqlAsync(OrgA, UnitA, $"INSERT INTO comandas (id, organizacao_id, unidade_id, dia_operacional, numero, identificacao, garcom_id, aberta_em, taxa_servico_percentual) VALUES ('{Guid.NewGuid():D}', '{OrgA:D}', '{UnitA2:D}', current_date, 1, 'Praia', '{Garcom:D}', now() - interval '3 hours', 10)");

        var supervisor = await CentralAsync(client, t.Supervisor, "?tempoMinimoMinutos=0");
        var doGarcom = await CentralAsync(client, t.Supervisor, $"?garcomId={Garcom}");
        var daOutraOrg = await CentralAsync(client, t.DonoB);

        Confere(supervisor, 1, daqui.Id);
        Confere(doGarcom, 1, daqui.Id);
        Assert.Equal(1, daOutraOrg.Total);
        Assert.DoesNotContain(daqui.Id, daOutraOrg.Comandas.Select(c => c.Id));
    }

    [Fact]
    public async Task The_central_needs_a_session_and_the_view_permission_and_refuses_a_bad_filter()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var semSessao = await SendAsync(client, HttpMethod.Get, "/comandas/central", null);
        using var semPermissao = await SendAsync(client, HttpMethod.Get, "/comandas/central", t.Mudo);
        using var tempoNegativo = await SendAsync(client, HttpMethod.Get, "/comandas/central?tempoMinimoMinutos=-1", t.Supervisor);
        using var numeroZero = await SendAsync(client, HttpMethod.Get, "/comandas/central?numero=0", t.Supervisor);
        using var guidRuim = await SendAsync(client, HttpMethod.Get, "/comandas/central?garcomId=nao", t.Supervisor);
        using var numeroTexto = await SendAsync(client, HttpMethod.Get, "/comandas/central?numero=abc", t.Supervisor);
        using var numeroGrande = await SendAsync(client, HttpMethod.Get, "/comandas/central?numero=99999999999", t.Supervisor);

        Assert.Equal(HttpStatusCode.Unauthorized, semSessao.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, semPermissao.StatusCode);
        foreach (var ruim in new[] { tempoNegativo, numeroZero, guidRuim, numeroTexto, numeroGrande })
        {
            Assert.Equal(HttpStatusCode.BadRequest, ruim.StatusCode);
            Assert.Equal("invalido", (await ruim.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        }
    }

    [Fact]
    public async Task A_waiter_without_the_permission_to_see_others_gets_only_their_own_on_the_server_whatever_the_filter_says()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var minha = await OpenAsync(client, t.Garcom);
        var dele = await OpenAsync(client, t.Garcom2);

        var semFiltro = await CentralAsync(client, t.Garcom);
        var pedindoOutro = await CentralAsync(client, t.Garcom, $"?garcomId={Garcom2}");
        var pedindoEu = await CentralAsync(client, t.Garcom, $"?garcomId={Garcom}");
        var pelaNumero = await CentralAsync(client, t.Garcom, $"?numero={dele.Numero}");

        Confere(semFiltro, 1, minha.Id);
        Confere(pedindoOutro, 0);
        Confere(pedindoEu, 1, minha.Id);
        Confere(pelaNumero, 0);
    }

    [Fact]
    public async Task The_central_is_a_read_and_writes_no_audit()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await OpenAsync(client, t.Garcom);
        var antes = (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros", r => r.GetInt64(0))).Single();

        await CentralAsync(client, t.Supervisor);

        Assert.Equal(antes, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM auditoria_registros", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Past_the_row_limit_the_central_returns_200_rows_and_the_full_total()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        await SqlAsync(OrgA, UnitA, $"INSERT INTO comandas (id, organizacao_id, unidade_id, dia_operacional, numero, identificacao, garcom_id, aberta_em, taxa_servico_percentual) SELECT gen_random_uuid(), '{OrgA:D}', '{UnitA:D}', current_date, g, 'C' || g, '{Garcom:D}', now() - g * interval '1 minute', 10 FROM generate_series(1, 205) g");

        var central = await CentralAsync(client, t.Supervisor);
        var filtrada = await CentralAsync(client, t.Supervisor, "?tempoMinimoMinutos=10");

        Assert.Equal((200, 205), (central.Comandas.Count, central.Total));
        Assert.Equal((196, 196), (filtrada.Comandas.Count, filtrada.Total));
    }
}
