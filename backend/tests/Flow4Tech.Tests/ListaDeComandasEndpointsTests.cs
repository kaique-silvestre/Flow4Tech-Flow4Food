using System.Net;
using System.Net.Http.Json;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0132: what the comanda list shows beside each row (the waiter's name, the consumption, the last launch) and the names of the app header, through the real authenticated routes (same fixture as the
/// comandas). Covers the values, the visibility of other people's comandas, another organization, denial and the session context.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record ComandaDaListaBody(Guid Id, string NumeroTexto, Guid GarcomId, string? GarcomNome, long? ConsumoCentavos, DateTimeOffset AbertaEm, DateTimeOffset? UltimaAtividadeEm);

    private sealed record ContextoBody(string Organizacao, string Unidade, string Usuario);

    private static async Task<List<ComandaDaListaBody>> ListWithSummaryAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/comandas", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ComandaDaListaBody>>())!;
    }

    [Fact]
    public async Task The_list_carries_the_waiter_name_the_consumption_and_the_last_launch_of_each_comanda()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var vazia = await OpenAsync(client, t.Garcom2);
        var comanda = await OpenAsync(client, t.Garcom);
        var rodada = await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id, quantidade: 3);
        var segunda = await LaunchOkAsync(client, t.Garcom, comanda.Id, rodada.ComandaVersao, produto.Id, quantidade: 2);
        using var cancelada = await CancelAsync(client, t.Garcom, comanda.Id, rodada.Itens[0].Id, segunda.ComandaVersao, "cliente-desistiu");
        Assert.Equal(HttpStatusCode.OK, cancelada.StatusCode);

        var lista = await ListWithSummaryAsync(client, t.Supervisor);

        var minha = lista.Single(c => c.Id == comanda.Id);
        Assert.Equal(("Garcom", 2000L), (minha.GarcomNome, minha.ConsumoCentavos));
        Assert.Equal(segunda.EnviadaEm, minha.UltimaAtividadeEm);
        var semItens = lista.Single(c => c.Id == vazia.Id);
        Assert.Equal(("Garcom Dois", 0L), (semItens.GarcomNome, semItens.ConsumoCentavos));
        Assert.Equal(semItens.AbertaEm, semItens.UltimaAtividadeEm);
    }

    [Fact]
    public async Task The_list_summary_follows_the_same_visibility_and_leaves_other_organizations_out()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var comanda = await OpenAsync(client, t.Garcom);
        await LaunchOkAsync(client, t.Garcom, comanda.Id, comanda.Versao, produto.Id);

        Assert.Empty(await ListWithSummaryAsync(client, t.Garcom2));
        Assert.Empty(await ListWithSummaryAsync(client, t.DonoB));
        Assert.Equal(1000L, Assert.Single(await ListWithSummaryAsync(client, t.Garcom)).ConsumoCentavos);
    }

    [Fact]
    public async Task The_session_context_names_the_organization_the_unit_and_the_person_of_the_session_and_nobody_else()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var garcom = await SendAsync(client, HttpMethod.Get, "/sessao/contexto", t.Garcom);
        using var donoB = await SendAsync(client, HttpMethod.Get, "/sessao/contexto", t.DonoB);
        using var anonymous = await SendAsync(client, HttpMethod.Get, "/sessao/contexto", null);

        Assert.Equal(HttpStatusCode.OK, garcom.StatusCode);
        Assert.Equal(new ContextoBody("Bar A", "Centro", "Garcom"), await garcom.Content.ReadFromJsonAsync<ContextoBody>());
        Assert.Equal(new ContextoBody("Bar B", "Centro", "Dono"), await donoB.Content.ReadFromJsonAsync<ContextoBody>());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
