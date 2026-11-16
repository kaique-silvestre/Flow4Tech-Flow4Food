using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0143: the opening agenda of the unit (weekly default, exceptions, "extend today") through the real authenticated routes, read from the layered dia-operacional.* options, and the refusal of
/// launching outside it. The clock is injected (sessions are minted AFTER each move, so they stay valid); the zone is the default America/Sao_Paulo (UTC-3) and the rollover 05:00.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    // Monday 2026-11-16, 12:00 local.
    private static readonly DateTimeOffset Segunda = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }

    private sealed record JanelaBody(string Abre, string Fecha);

    private sealed record ExcecaoBody(DateOnly Dia, JanelaBody? Janela);

    private sealed record AgendaBody(DateOnly DiaOperacional, string Fuso, string Virada, List<JanelaBody?> Semana, List<ExcecaoBody> Excecoes, string? EstendidoAte, JanelaBody? JanelaDeHoje, bool AbertoAgora);

    private static object?[] Semana(string abre, string fecha) => [.. Enumerable.Repeat<object?>(new { abre, fecha }, 7)];

    private Api AgendaApi(MovableClock clock) => new(_appConnection, configure: services =>
    {
        services.RemoveAll<IClock>();
        services.AddSingleton<IClock>(clock);
    });

    private async Task<string> MintAsync(Api api, Guid user, Guid org)
    {
        var versao = (await QueryAsync(org, org == OrgA ? UnitA : UnitB, $"SELECT versao_credencial FROM contas WHERE id = '{user:D}'", r => r.GetInt32(0))).Single();
        return (await api.Services.GetRequiredService<Sessoes>().IniciarAsync(user, org, versao, false, new ClienteDaSessao("127.0.0.1", "testes"))).AccessToken;
    }

    private static async Task<AgendaBody> GetAgendaAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/agenda", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgendaBody>())!;
    }

    [Fact]
    public async Task The_weekly_default_blocks_opening_and_launching_outside_the_hours_and_allows_inside_them()
    {
        var clock = new MovableClock(Segunda.AddHours(-4)); // 08:00 local
        await using var api = AgendaApi(clock);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        // Nothing configured: open the whole operating day, so nothing is blocked (existing behavior).
        var padrao = await GetAgendaAsync(client, t.Dono);
        Assert.Equal((new DateOnly(2026, 11, 16), "America/Sao_Paulo", "05:00", true, 7), (padrao.DiaOperacional, padrao.Fuso, padrao.Virada, padrao.AbertoAgora, padrao.Semana.Count));
        var produto = await CreateProductAsync(client, t.Dono, "CHOPP", "Chopp", 1000);
        var cedo = await OpenAsync(client, t.Garcom);

        using var salvar = await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Semana("10:00", "23:00"), motivo = "horario novo" });
        Assert.Equal(HttpStatusCode.NoContent, salvar.StatusCode);
        var agenda = await GetAgendaAsync(client, t.Dono);
        Assert.Equal((false, new JanelaBody("10:00", "23:00")), (agenda.AbertoAgora, agenda.JanelaDeHoje));

        // 08:00: closed. Opening and launching are refused with a typed 409, and nothing is written.
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/comandas", t.Garcom, new { }), HttpStatusCode.Conflict, "fora-do-horario");
        await AssertErrorAsync(await LaunchAsync(client, t.Garcom, cedo.Id, cedo.Versao, produto.Id), HttpStatusCode.Conflict, "fora-do-horario");
        Assert.Equal((1L, 0L, 1), (await CountAsync("comandas"), await CountAsync("rodadas"), await VersionOfAsync(cedo.Id)));

        // 12:00: inside. 23:30 and 00:30 (still operating day Monday): outside again.
        clock.UtcNow = Segunda;
        var garcom = await MintAsync(api, Garcom, OrgA);
        var aberta = await OpenAsync(client, garcom);
        await LaunchOkAsync(client, garcom, aberta.Id, aberta.Versao, produto.Id);
        clock.UtcNow = Segunda.AddHours(11).AddMinutes(30);
        garcom = await MintAsync(api, Garcom, OrgA);
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/comandas", garcom, new { }), HttpStatusCode.Conflict, "fora-do-horario");
        clock.UtcNow = Segunda.AddHours(12).AddMinutes(30);
        garcom = await MintAsync(api, Garcom, OrgA);
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/comandas", garcom, new { }), HttpStatusCode.Conflict, "fora-do-horario");
    }

    [Fact]
    public async Task A_date_exception_beats_the_weekly_default_and_dates_already_past_are_dropped()
    {
        var clock = new MovableClock(Segunda);
        await using var api = AgendaApi(clock);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        (await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Semana("10:00", "23:00") })).Dispose();
        var excecoes = new[]
        {
            new { dia = "2026-11-15", janela = (object?)null },
            new { dia = "2026-11-17", janela = (object?)null },
            new { dia = "2026-11-18", janela = (object?)new { abre = "20:00", fecha = "23:00" } },
        };
        using var salvar = await SendAsync(client, HttpMethod.Put, "/agenda/excecoes", t.Dono, new { excecoes });
        Assert.Equal(HttpStatusCode.NoContent, salvar.StatusCode);
        var lida = await GetAgendaAsync(client, t.Dono);
        Assert.Equal([new ExcecaoBody(new DateOnly(2026, 11, 17), null), new ExcecaoBody(new DateOnly(2026, 11, 18), new JanelaBody("20:00", "23:00"))], lida.Excecoes);

        // Monday follows the week; Tuesday is closed by the exception; Wednesday opens only at 20:00.
        await OpenAsync(client, t.Garcom);
        clock.UtcNow = Segunda.AddDays(1);
        var terca = await MintAsync(api, Garcom, OrgA);
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/comandas", terca, new { }), HttpStatusCode.Conflict, "fora-do-horario");
        clock.UtcNow = Segunda.AddDays(2);
        var quarta = await MintAsync(api, Garcom, OrgA);
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/comandas", quarta, new { }), HttpStatusCode.Conflict, "fora-do-horario");
        clock.UtcNow = Segunda.AddDays(2).AddHours(8).AddMinutes(30); // 20:30 local
        await OpenAsync(client, await MintAsync(api, Garcom, OrgA));

        // More than the room allows, and a repeated date, are refused.
        var demais = Enumerable.Range(0, 11).Select(i => new { dia = new DateOnly(2026, 12, 1).AddDays(i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), janela = (object?)null }).ToArray();
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, "/agenda/excecoes", await MintAsync(api, DonoA, OrgA), new { excecoes = demais }), HttpStatusCode.BadRequest, "excecoes-demais");
        var repetida = new[] { new { dia = "2026-12-01", janela = (object?)null }, new { dia = "2026-12-01", janela = (object?)null } };
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, "/agenda/excecoes", await MintAsync(api, DonoA, OrgA), new { excecoes = repetida }), HttpStatusCode.BadRequest, "invalido");
    }

    [Fact]
    public async Task Extending_today_moves_only_the_closing_of_the_current_operating_day()
    {
        var clock = new MovableClock(Segunda.AddHours(6).AddMinutes(30)); // 18:30 local, week closes at 18:00
        await using var api = AgendaApi(clock);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        (await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Semana("10:00", "18:00") })).Dispose();
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/comandas", t.Garcom, new { }), HttpStatusCode.Conflict, "fora-do-horario");

        // Not later than the current closing, not later than now, or past the end of the operating day (05:00 of the next one): refused.
        foreach (var fecha in new[] { "17:00", "18:15", "06:00" })
        {
            await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/agenda/estender-hoje", t.Dono, new { fecha }), HttpStatusCode.BadRequest, "extensao-invalida");
        }

        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/agenda/estender-hoje", t.Dono, new { fecha = "25:00" }), HttpStatusCode.BadRequest, "invalido");
        using var estender = await SendAsync(client, HttpMethod.Post, "/agenda/estender-hoje", t.Dono, new { fecha = "20:00", motivo = "evento" });
        Assert.Equal(HttpStatusCode.NoContent, estender.StatusCode);
        var agenda = await GetAgendaAsync(client, t.Dono);
        Assert.Equal(("20:00", new JanelaBody("10:00", "20:00"), true), (agenda.EstendidoAte, agenda.JanelaDeHoje, agenda.AbertoAgora));
        await OpenAsync(client, t.Garcom);

        // After the extension, 20:30 is closed again; next day (Tuesday 18:30) the extension no longer applies, and there is nothing to extend past the normal closing.
        clock.UtcNow = Segunda.AddHours(8).AddMinutes(30);
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/comandas", await MintAsync(api, Garcom, OrgA), new { }), HttpStatusCode.Conflict, "fora-do-horario");
        clock.UtcNow = Segunda.AddDays(1).AddHours(6).AddMinutes(30);
        var terca = await GetAgendaAsync(client, await MintAsync(api, DonoA, OrgA));
        Assert.Equal((new DateOnly(2026, 11, 17), null, new JanelaBody("10:00", "18:00"), false), (terca.DiaOperacional, terca.EstendidoAte, terca.JanelaDeHoje, terca.AbertoAgora));

        // A day open the whole time (or closed) has no closing to move.
        (await SendAsync(client, HttpMethod.Put, "/agenda/semana", await MintAsync(api, DonoA, OrgA), new { semana = Semana("00:00", "00:00") })).Dispose();
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/agenda/estender-hoje", await MintAsync(api, DonoA, OrgA), new { fecha = "20:00" }), HttpStatusCode.Conflict, "sem-horario-para-estender");
    }

    [Fact]
    public async Task Another_organization_neither_sees_nor_changes_the_agenda_and_the_write_is_audited()
    {
        var clock = new MovableClock(Segunda);
        await using var api = AgendaApi(clock);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        using var salvar = await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Semana("10:00", "23:00"), motivo = "horario novo" });
        Assert.Equal(HttpStatusCode.NoContent, salvar.StatusCode);

        // The organization B owner closes every day for ITS unit: A keeps its week and keeps launching.
        (await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.DonoB, new { semana = new object?[7] })).Dispose();
        Assert.Equal((false, null), ((await GetAgendaAsync(client, t.DonoB)).AbertoAgora, (await GetAgendaAsync(client, t.DonoB)).JanelaDeHoje));
        Assert.Equal((true, new JanelaBody("10:00", "23:00")), ((await GetAgendaAsync(client, t.Dono)).AbertoAgora, (await GetAgendaAsync(client, t.Dono)).JanelaDeHoje));
        await OpenAsync(client, t.Garcom);

        // Each write is a layered-setting version and one audit record of the same unit of work, with who, what and the reason.
        var auditoria = await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_id, depois->>'valor', depois->>'camada' FROM auditoria_registros WHERE acao = 'configuracao:alterar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        Assert.Equal([(DonoA, "dia-operacional.semana", "1000-2300;1000-2300;1000-2300;1000-2300;1000-2300;1000-2300;1000-2300", "unidade")], auditoria);
        Assert.Equal(0L, (await QueryAsync(OrgB, UnitB, "SELECT count(*) FROM configuracoes_versoes WHERE unidade_id = '" + UnitA.ToString("D") + "'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Without_a_session_the_agenda_is_401_and_without_the_permission_it_is_403_and_changes_nothing()
    {
        var clock = new MovableClock(Segunda);
        await using var api = AgendaApi(clock);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        foreach (var (method, url, body) in new (HttpMethod, string, object?)[] { (HttpMethod.Get, "/agenda", null), (HttpMethod.Put, "/agenda/semana", new { semana = Semana("10:00", "23:00") }), (HttpMethod.Put, "/agenda/excecoes", new { excecoes = Array.Empty<object>() }), (HttpMethod.Post, "/agenda/estender-hoje", new { fecha = "20:00" }) })
        {
            using var anonimo = await SendAsync(client, method, url, null, body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonimo.StatusCode);
            foreach (var token in new[] { t.Garcom, t.Supervisor, t.Mudo })
            {
                using var negado = await SendAsync(client, method, url, token, body);
                Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
                Assert.Equal(string.Empty, await negado.Content.ReadAsStringAsync());
            }
        }

        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM configuracoes_versoes WHERE chave LIKE 'dia-operacional.%'", r => r.GetInt64(0))).Single());
        // A malformed body names what is wrong and writes nothing.
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Semana("10:00", "24:00") }), HttpStatusCode.BadRequest, "invalido");
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = new object?[3] }), HttpStatusCode.BadRequest, "invalido");
        Assert.Equal(0L, (await QueryAsync(OrgA, UnitA, "SELECT count(*) FROM configuracoes_versoes WHERE chave LIKE 'dia-operacional.%'", r => r.GetInt64(0))).Single());
    }

    [Fact]
    public async Task Exceptions_keep_the_century_and_windows_the_operating_day_cannot_honor_are_refused()
    {
        var clock = new MovableClock(Segunda);
        await using var api = AgendaApi(clock);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        // 2099 is accepted and read back as 2099; 2030 is not stored as 1930; outside 2000 to 2099 is refused.
        var longe = new[] { new { dia = "2030-05-01", janela = (object?)null }, new { dia = "2099-12-31", janela = (object?)null } };
        using var salvar = await SendAsync(client, HttpMethod.Put, "/agenda/excecoes", t.Dono, new { excecoes = longe });
        Assert.Equal(HttpStatusCode.NoContent, salvar.StatusCode);
        Assert.Equal([new DateOnly(2030, 5, 1), new DateOnly(2099, 12, 31)], (await GetAgendaAsync(client, t.Dono)).Excecoes.Select(e => e.Dia));
        foreach (var dia in new[] { "2100-01-01", "1999-12-31" })
        {
            await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, "/agenda/excecoes", t.Dono, new { excecoes = new[] { new { dia, janela = (object?)null } } }), HttpStatusCode.BadRequest, "invalido");
        }

        // Opening before the 05:00 rollover, or closing after midnight later than it, cannot work: refused in the week and in an exception.
        foreach (var (abre, fecha) in new[] { ("04:00", "23:00"), ("18:00", "06:00") })
        {
            await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Semana(abre, fecha) }), HttpStatusCode.BadRequest, "invalido");
            var excecao = new[] { new { dia = "2026-11-20", janela = (object?)new { abre, fecha } } };
            await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, "/agenda/excecoes", t.Dono, new { excecoes = excecao }), HttpStatusCode.BadRequest, "invalido");
        }

        using var cruza = await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Semana("18:00", "05:00") });
        Assert.Equal(HttpStatusCode.NoContent, cruza.StatusCode);
    }

    [Fact]
    public async Task Extending_today_is_409_travada_when_the_organization_locked_the_option()
    {
        var clock = new MovableClock(Segunda.AddHours(6).AddMinutes(30)); // 18:30 local
        await using var api = AgendaApi(clock);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        (await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Semana("10:00", "18:00") })).Dispose();
        using var travar = await SendAsync(client, HttpMethod.Put, "/configuracoes/dia-operacional.estender-hoje", t.Dono, new { camada = "organizacao", valor = "-", travado = true });
        Assert.Equal(HttpStatusCode.NoContent, travar.StatusCode);

        await AssertErrorAsync(await SendAsync(client, HttpMethod.Post, "/agenda/estender-hoje", t.Dono, new { fecha = "20:00" }), HttpStatusCode.Conflict, "travada");
        Assert.False((await GetAgendaAsync(client, t.Dono)).AbertoAgora);
    }
}
