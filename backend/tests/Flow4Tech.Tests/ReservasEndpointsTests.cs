using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Domain;
using Flow4Tech.Comandas.Infrastructure;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Migrator;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0153: reservations of a unit through the real authenticated routes. Real PostgreSQL, application credential (RLS on), real sessions, a movable clock. Covers allowed, denied (403 without reason,
/// 401 without session), other organization and other unit, the overlap refusal (also under a race), arriving (opens ONE comanda in the same unit of work, idempotent under repetition and a race),
/// the map status, the opening hours, the audit record without personal data, rollback and the EF model.
/// </summary>
public sealed class ReservasEndpointsTests : IAsyncLifetime
{
    private const string Key = "chave-de-teste-nao-usar-em-producao-0123456789";
    private const string Nome = "Ana Souza Pimentel";
    private const string Telefone = "11 91234-5678";

    // Monday 2026-11-16, 12:00 local (America/Sao_Paulo, UTC-3).
    private static readonly DateTimeOffset Segunda = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);

    private static readonly Guid OrgA = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid UnitA = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid UnitA2 = Guid.Parse("0197b000-0000-7000-8000-0000000000a2");
    private static readonly Guid DonoA = Guid.Parse("0197b000-0000-7000-8000-0000000000d1");
    private static readonly Guid Atendente = Guid.Parse("0197b000-0000-7000-8000-0000000000c1");
    private static readonly Guid SoVer = Guid.Parse("0197b000-0000-7000-8000-0000000000c2");
    private static readonly Guid SemAbrir = Guid.Parse("0197b000-0000-7000-8000-0000000000c3");
    private static readonly Guid SemNada = Guid.Parse("0197b000-0000-7000-8000-0000000000c4");
    private static readonly Guid OrgB = Guid.Parse("0197b000-0000-7000-8000-00000000000b");
    private static readonly Guid UnitB = Guid.Parse("0197b000-0000-7000-8000-0000000000b1");
    private static readonly Guid DonoB = Guid.Parse("0197b000-0000-7000-8000-0000000000e1");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private string _appConnection = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var owner = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        var app = new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Username = "flow4tech_app", Password = "app-secret-for-test" };
        _appConnection = app.ConnectionString;
        using var output = new StringWriter();
        var env = new Dictionary<string, string?>
        {
            ["MIGRATION_DATABASE_URL"] = $"postgres://{owner.Username}:{Uri.EscapeDataString(owner.Password!)}@{owner.Host}:{owner.Port}/{owner.Database}",
            ["APP_DB_PASSWORD"] = app.Password,
            ["ConnectionStrings__Default"] = app.ConnectionString,
            ["MIGRATIONS_DIR"] = MigrationsDirectory(),
        };
        Assert.True(await MigratorHost.RunAsync(name => env.GetValueOrDefault(name), output, CancellationToken.None) == 0, output.ToString());

        await using var ownerSource = NpgsqlDataSource.Create(owner.ConnectionString);
        foreach (var (org, unit, dono, email) in new[] { (OrgA, UnitA, DonoA, "dono@a.test"), (OrgB, UnitB, DonoB, "dono@b.test") })
        {
            await using var tx = await new TenantDatabase(ownerSource).BeginAsync(new OrganizationContext(org, unit));
            await Exec(tx, "INSERT INTO organizacoes (id, nome) VALUES (@o, 'Bar')", ("o", org));
            await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Centro')", ("u", unit), ("o", org));
            await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, email, dono, criada_em) VALUES (@i, @o, 'email', 'Dono', @e, true, now())", ("i", dono), ("o", org), ("e", email));
            await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", dono), ("n", unit));
            if (org == OrgA)
            {
                await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'Praia')", ("u", UnitA2), ("o", org));
                foreach (var (id, nome, usuario) in new[] { (Atendente, "Atendente", "atendente"), (SoVer, "So ver", "sover"), (SemAbrir, "Sem abrir", "semabrir"), (SemNada, "Sem nada", "semnada") })
                {
                    await Exec(tx, "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (@i, @o, 'operacional', @n, @s, now())", ("i", id), ("o", org), ("n", nome), ("s", usuario));
                    await Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)", ("o", org), ("u", id), ("n", unit));
                }
            }

            await tx.CommitAsync();
        }
    }

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static async Task Exec(TenantTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static string MigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "modules.dependencies.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("backend root not found"), "migrations");
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }

    private sealed class Api(string connection, Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connection,
                    ["Flow4Tech:Tokens:AppKey"] = Key,
                    ["Flow4Tech:Identidade:TempoMinimoDeLoginMs"] = "0",
                }));
            if (configure is not null)
            {
                builder.ConfigureTestServices(configure);
            }
        }
    }

    private static Api NewApi(string connection, MovableClock clock, Action<IServiceCollection>? more = null) => new(connection, services =>
    {
        services.RemoveAll<IClock>();
        services.AddSingleton<IClock>(clock);
        more?.Invoke(services);
    });

    private sealed class FakeRequest(Guid user, OrganizationContext organization) : IRequestContext
    {
        public Guid UserId => user;

        public Guid SessionId => Guid.NewGuid();

        public OrganizationContext Organization => organization;
    }

    private sealed record Tokens(string Dono, string Atendente, string SoVer, string SemAbrir, string SemNada, string DonoB);

    private sealed record ReservaBody(Guid Id, Guid? MesaId, string Nome, string? Telefone, int Pessoas, DateTimeOffset Horario, DateOnly DiaOperacional, string? Observacao, string Estado, Guid? ComandaId);

    private sealed record ChegouBody(ReservaBody Reserva, Guid ComandaId, bool Repetida);

    private sealed record MesaMapaBody(Guid Id, string Identificacao, string Status, int ComandasAbertas, DateTimeOffset? ReservadaPara);

    private sealed record AreaMapaBody(Guid Id, string Nome, List<MesaMapaBody> Mesas);

    private sealed record MapaBody(List<AreaMapaBody> Areas, List<MesaMapaBody> SemArea);

    private sealed record MesaListaBody(Guid Id, string Status);

    private sealed record Erro(string Codigo);

    private async Task<Tokens> SeedAsync(Api api)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var dono = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        async Task GiveAsync(Guid user, string nome, params string[] permissoes)
        {
            var perfil = await perfis.CriarAsync(dono, nome, [.. permissoes.Select(p => new RegraDeAcesso(RuleEffect.Allow, p))]);
            Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
            Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(dono, user, perfil.Id!.Value));
        }

        await GiveAsync(Atendente, "Atendente", "reserva:ver", "reserva:gerenciar", "reserva:chegou", "comanda:abrir", "comanda:ver", "mesa:ver");
        await GiveAsync(SoVer, "So ver reservas", "reserva:ver");
        await GiveAsync(SemAbrir, "Chegou sem abrir", "reserva:ver", "reserva:chegou");
        return new Tokens(
            await MintAsync(api, DonoA, OrgA),
            await MintAsync(api, Atendente, OrgA),
            await MintAsync(api, SoVer, OrgA),
            await MintAsync(api, SemAbrir, OrgA),
            await MintAsync(api, SemNada, OrgA),
            await MintAsync(api, DonoB, OrgB));
    }

    private async Task<string> MintAsync(Api api, Guid user, Guid org)
    {
        var versao = (await QueryAsync(org, org == OrgA ? UnitA : UnitB, $"SELECT versao_credencial FROM contas WHERE id = '{user:D}'", r => r.GetInt32(0))).Single();
        return (await api.Services.GetRequiredService<Sessoes>().IniciarAsync(user, org, versao, false, new ClienteDaSessao("127.0.0.1", "testes"))).AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private async Task<List<T>> QueryAsync<T>(Guid org, Guid unit, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org, unit));
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private async Task<long> CountAsync(string sql) => (await QueryAsync(OrgA, UnitA, sql, r => r.GetInt64(0))).Single();

    private async Task<Guid> MesaAsync(string nome, Guid? org = null, Guid? unit = null)
    {
        var id = Guid.CreateVersion7();
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(org ?? OrgA, unit ?? UnitA));
        await Exec(tx, "INSERT INTO mesas (id, organizacao_id, unidade_id, identificacao, ativa, criada_em) VALUES (@i, @o, @u, @n, true, now())", ("i", id), ("o", org ?? OrgA), ("u", unit ?? UnitA), ("n", nome));
        await tx.CommitAsync();
        return id;
    }

    private static object Corpo(DateTimeOffset horario, Guid? mesa = null, int pessoas = 4, string nome = Nome, string? telefone = Telefone, string? observacao = "aniversario") =>
        new { nome, telefone, pessoas, horario, mesaId = mesa, observacao };

    private static async Task<ReservaBody> CriarAsync(HttpClient client, string token, DateTimeOffset horario, Guid? mesa = null, int pessoas = 4)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/reservas", token, Corpo(horario, mesa, pessoas));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReservaBody>())!;
    }

    private static async Task AssertErroAsync(HttpResponseMessage response, HttpStatusCode status, string codigo)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(codigo, (await response.Content.ReadFromJsonAsync<Erro>())!.Codigo);
        }
    }

    private static async Task AssertVazioAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Booking_stores_the_reservation_in_the_active_unit_audits_it_without_personal_data_and_the_waiter_lists_it()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await MesaAsync("12");
        var horario = Segunda.AddHours(2);

        var reserva = await CriarAsync(client, t.Atendente, horario, mesa, 6);

        Assert.Equal(("reservada", Nome, Telefone, 6, mesa, new DateOnly(2026, 11, 16), null), (reserva.Estado, reserva.Nome, reserva.Telefone, reserva.Pessoas, reserva.MesaId, reserva.DiaOperacional, reserva.ComandaId));
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT unidade_id, nome, telefone, pessoas, estado, comanda_id, observacao FROM reservas", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.IsDBNull(5), r.GetString(6))));
        Assert.Equal((UnitA, Nome, Telefone, 6, "reservada", true, "aniversario"), row);
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_id, antes IS NULL, depois->>'estado', depois->>'pessoas', depois->>'mesaId' FROM auditoria_registros WHERE acao = 'reserva:criar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4), r.GetString(5), r.GetString(6))));
        Assert.Equal((Atendente, "sucesso", reserva.Id.ToString("D"), true, "reservada", "6", mesa.ToString("D")), audit);

        using var lista = await SendAsync(client, HttpMethod.Get, "/reservas", t.SoVer);
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        Assert.Equal(reserva.Id, Assert.Single((await lista.Content.ReadFromJsonAsync<List<ReservaBody>>())!).Id);
    }

    [Fact]
    public async Task Name_and_phone_never_reach_the_audit_trail_in_any_action()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await MesaAsync("1");
        var a = await CriarAsync(client, t.Atendente, Segunda.AddHours(1), mesa);
        var b = await CriarAsync(client, t.Atendente, Segunda.AddHours(5), mesa);
        var c = await CriarAsync(client, t.Atendente, Segunda.AddHours(9), mesa);
        (await SendAsync(client, HttpMethod.Post, $"/reservas/{a.Id}/chegou", t.Atendente)).Dispose();
        (await SendAsync(client, HttpMethod.Post, $"/reservas/{b.Id}/cancelar", t.Atendente)).Dispose();
        (await SendAsync(client, HttpMethod.Post, $"/reservas/{c.Id}/nao-compareceu", t.Atendente)).Dispose();

        var registros = await QueryAsync(OrgA, UnitA, "SELECT acao, coalesce(antes::text, '') || coalesce(depois::text, '') FROM auditoria_registros WHERE acao LIKE 'reserva:%'", r => (r.GetString(0), r.GetString(1)));

        Assert.Equal(["reserva:cancelar", "reserva:chegou", "reserva:criar", "reserva:criar", "reserva:criar", "reserva:nao-compareceu"], registros.Select(r => r.Item1).Order());
        Assert.DoesNotContain(registros, r => r.Item2.Contains("Ana", StringComparison.OrdinalIgnoreCase) || r.Item2.Contains("91234", StringComparison.Ordinal) || r.Item2.Contains("aniversario", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_permission_the_answer_is_an_empty_403_and_nothing_is_written_and_without_a_session_it_is_401()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var existente = await CriarAsync(client, t.Atendente, Segunda.AddHours(1));

        await AssertVazioAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.SoVer, Corpo(Segunda.AddHours(3))), HttpStatusCode.Forbidden);
        await AssertVazioAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.SemNada, Corpo(Segunda.AddHours(3))), HttpStatusCode.Forbidden);
        await AssertVazioAsync(await SendAsync(client, HttpMethod.Get, "/reservas", t.SemNada), HttpStatusCode.Forbidden);
        await AssertVazioAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{existente.Id}/chegou", t.SoVer), HttpStatusCode.Forbidden);
        await AssertVazioAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{existente.Id}/cancelar", t.SemAbrir), HttpStatusCode.Forbidden);
        await AssertVazioAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{existente.Id}/nao-compareceu", t.SoVer), HttpStatusCode.Forbidden);
        await AssertVazioAsync(await SendAsync(client, HttpMethod.Get, "/reservas", null), HttpStatusCode.Unauthorized);
        await AssertVazioAsync(await SendAsync(client, HttpMethod.Post, "/reservas", null, Corpo(Segunda.AddHours(3))), HttpStatusCode.Unauthorized);

        Assert.Equal((1L, 0L), (await CountAsync("SELECT count(*) FROM reservas WHERE estado = 'reservada'"), await CountAsync("SELECT count(*) FROM comandas")));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao LIKE 'reserva:%'"));
    }

    [Fact]
    public async Task Arriving_without_the_permission_to_open_a_comanda_is_an_empty_403_and_the_reservation_stays_waiting()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var reserva = await CriarAsync(client, t.Atendente, Segunda.AddHours(1), await MesaAsync("1"));

        await AssertVazioAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/chegou", t.SemAbrir), HttpStatusCode.Forbidden);

        Assert.Equal((0L, 1L), (await CountAsync("SELECT count(*) FROM comandas"), await CountAsync("SELECT count(*) FROM reservas WHERE estado = 'reservada'")));
    }

    [Fact]
    public async Task Another_organization_sees_nothing_and_cannot_move_or_book_on_what_it_does_not_own()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesaA = await MesaAsync("1");
        var reserva = await CriarAsync(client, t.Atendente, Segunda.AddHours(1), mesaA);

        using var lista = await SendAsync(client, HttpMethod.Get, "/reservas", t.DonoB);
        Assert.Empty((await lista.Content.ReadFromJsonAsync<List<ReservaBody>>())!);
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/chegou", t.DonoB), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/cancelar", t.DonoB), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/nao-compareceu", t.DonoB), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.DonoB, Corpo(Segunda.AddHours(3), mesaA)), HttpStatusCode.NotFound, "nao-encontrada");

        Assert.Equal((1L, 0L), (await CountAsync("SELECT count(*) FROM reservas WHERE estado = 'reservada'"), await CountAsync("SELECT count(*) FROM comandas")));
        Assert.Empty(await QueryAsync(OrgB, UnitB, "SELECT id FROM reservas", r => r.GetGuid(0)));
    }

    [Fact]
    public async Task A_reservation_of_a_sibling_unit_is_neither_listed_nor_movable_and_the_database_refuses_a_table_of_another_unit()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesaDaPraia = await MesaAsync("Praia 1", OrgA, UnitA2);
        var dePraia = Guid.CreateVersion7();
        await using (var source = NpgsqlDataSource.Create(_appConnection))
        await using (var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA2)))
        {
            await Exec(tx, "INSERT INTO reservas (id, organizacao_id, unidade_id, nome, pessoas, horario, dia_operacional, estado, criada_em, atualizada_em) VALUES (@i, @o, @u, 'Outra', 2, @h, '2026-11-16', 'reservada', now(), now())", ("i", dePraia), ("o", OrgA), ("u", UnitA2), ("h", Segunda.AddHours(1)));
            await tx.CommitAsync();
        }

        using var lista = await SendAsync(client, HttpMethod.Get, "/reservas", t.Atendente);
        Assert.Empty((await lista.Content.ReadFromJsonAsync<List<ReservaBody>>())!);
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{dePraia}/chegou", t.Atendente), HttpStatusCode.NotFound, "nao-encontrada");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.Atendente, Corpo(Segunda.AddHours(3), mesaDaPraia)), HttpStatusCode.NotFound, "nao-encontrada");

        await using var source2 = NpgsqlDataSource.Create(_appConnection);
        await using var tx2 = await new TenantDatabase(source2).BeginAsync(new OrganizationContext(OrgA, UnitA));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(tx2, "INSERT INTO reservas (id, organizacao_id, unidade_id, mesa_id, nome, pessoas, horario, dia_operacional, estado, criada_em, atualizada_em) VALUES (@i, @o, @u, @m, 'X', 2, now(), '2026-11-16', 'reservada', now(), now())", ("i", Guid.CreateVersion7()), ("o", OrgA), ("u", UnitA), ("m", mesaDaPraia)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Two_bookings_of_one_table_closer_than_the_duration_are_a_409_and_writing_nothing_and_a_free_slot_or_table_is_fine()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await MesaAsync("1");
        var outra = await MesaAsync("2");
        var base0 = Segunda.AddHours(3);
        var primeira = await CriarAsync(client, t.Atendente, base0, mesa);

        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.Atendente, Corpo(base0, mesa)), HttpStatusCode.Conflict, "sobreposta");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.Atendente, Corpo(base0.AddMinutes(119), mesa)), HttpStatusCode.Conflict, "sobreposta");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.Atendente, Corpo(base0.AddMinutes(-119), mesa)), HttpStatusCode.Conflict, "sobreposta");
        Assert.Equal((1L, 1L), (await CountAsync("SELECT count(*) FROM reservas"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'reserva:criar'")));

        await CriarAsync(client, t.Atendente, base0.AddMinutes(120), mesa);
        await CriarAsync(client, t.Atendente, base0, outra);
        await CriarAsync(client, t.Atendente, base0, null);
        (await SendAsync(client, HttpMethod.Post, $"/reservas/{primeira.Id}/cancelar", t.Atendente)).Dispose();
        await CriarAsync(client, t.Atendente, base0, mesa);
        Assert.Equal(5L, await CountAsync("SELECT count(*) FROM reservas"));
    }

    [Fact]
    public async Task Simultaneous_bookings_of_the_same_slot_leave_exactly_one_and_the_rest_get_409()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await MesaAsync("1");

        var statuses = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/reservas", t.Atendente, Corpo(Segunda.AddHours(3).AddMinutes(i), mesa));
            return response.StatusCode;
        }));

        Assert.Single(statuses, HttpStatusCode.Created);
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal((1L, 1L), (await CountAsync("SELECT count(*) FROM reservas"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'reserva:criar'")));
    }

    [Fact]
    public async Task Arriving_opens_one_comanda_on_the_table_with_the_head_count_marks_the_reservation_and_audits_both_once()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await MesaAsync("12");
        var reserva = await CriarAsync(client, t.Atendente, Segunda.AddHours(1), mesa, 5);

        using var response = await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/chegou", t.Atendente);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corpo = (await response.Content.ReadFromJsonAsync<ChegouBody>())!;
        Assert.Equal(("chegou", false, corpo.ComandaId), (corpo.Reserva.Estado, corpo.Repetida, corpo.Reserva.ComandaId));
        var comanda = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT id, unidade_id, mesa_id, pessoas, garcom_id, estado, identificacao FROM comandas", r => (r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetGuid(4), r.GetString(5), r.GetString(6))));
        Assert.Equal((corpo.ComandaId, UnitA, mesa, 5, Atendente, "aberta", "12"), comanda);
        Assert.Equal(("chegou", corpo.ComandaId), Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT estado, comanda_id FROM reservas", r => (r.GetString(0), r.GetGuid(1)))));
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT antes->>'estado', depois->>'estado', depois->>'comandaId' FROM auditoria_registros WHERE acao = 'reserva:chegou'", r => (r.GetString(0), r.GetString(1), r.GetString(2))));
        Assert.Equal(("reservada", "chegou", corpo.ComandaId.ToString("D")), audit);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:abrir'"));
    }

    [Fact]
    public async Task Arriving_without_a_table_opens_a_numbered_comanda_that_carries_no_guest_name()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var reserva = await CriarAsync(client, t.Atendente, Segunda.AddHours(1));

        using var response = await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/chegou", t.Atendente);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var comanda = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT mesa_id IS NULL, identificacao FROM comandas", r => (r.GetBoolean(0), r.GetString(1))));
        Assert.True(comanda.Item1);
        Assert.DoesNotContain("Ana", comanda.Item2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arriving_twice_returns_the_same_comanda_and_opens_nothing_more()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var reserva = await CriarAsync(client, t.Atendente, Segunda.AddHours(1), await MesaAsync("1"));
        using var primeira = await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/chegou", t.Atendente);
        var um = (await primeira.Content.ReadFromJsonAsync<ChegouBody>())!;

        using var segunda = await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/chegou", t.Atendente);

        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        var dois = (await segunda.Content.ReadFromJsonAsync<ChegouBody>())!;
        Assert.Equal((um.ComandaId, true), (dois.ComandaId, dois.Repetida));
        Assert.Equal((1L, 1L, 1L), (await CountAsync("SELECT count(*) FROM comandas"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'reserva:chegou'"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:abrir'")));
    }

    [Fact]
    public async Task Simultaneous_arrivals_of_one_reservation_open_exactly_one_comanda_and_all_get_the_same_one()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var reserva = await CriarAsync(client, t.Atendente, Segunda.AddHours(1), await MesaAsync("1"));

        var corpos = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var response = await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/chegou", t.Atendente);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ChegouBody>())!;
        }));

        Assert.Single(corpos.Select(c => c.ComandaId).Distinct());
        Assert.Equal(1, corpos.Count(c => !c.Repetida));
        Assert.Equal((1L, 1L, 1L), (await CountAsync("SELECT count(*) FROM comandas"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'reserva:chegou'"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'comanda:abrir'")));
    }

    [Fact]
    public async Task A_cancelled_or_no_show_reservation_cannot_arrive_and_a_final_one_cannot_move_again()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var cancelada = await CriarAsync(client, t.Atendente, Segunda.AddHours(1));
        var faltou = await CriarAsync(client, t.Atendente, Segunda.AddHours(4));
        var veio = await CriarAsync(client, t.Atendente, Segunda.AddHours(7));
        using (var ok = await SendAsync(client, HttpMethod.Post, $"/reservas/{cancelada.Id}/cancelar", t.Atendente))
        {
            Assert.Equal("cancelada", (await ok.Content.ReadFromJsonAsync<ReservaBody>())!.Estado);
        }

        using (var ok = await SendAsync(client, HttpMethod.Post, $"/reservas/{faltou.Id}/nao-compareceu", t.Atendente))
        {
            Assert.Equal("nao-compareceu", (await ok.Content.ReadFromJsonAsync<ReservaBody>())!.Estado);
        }

        (await SendAsync(client, HttpMethod.Post, $"/reservas/{veio.Id}/chegou", t.Atendente)).Dispose();

        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{cancelada.Id}/chegou", t.Atendente), HttpStatusCode.Conflict, "estado-invalido");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{faltou.Id}/chegou", t.Atendente), HttpStatusCode.Conflict, "estado-invalido");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{cancelada.Id}/nao-compareceu", t.Atendente), HttpStatusCode.Conflict, "estado-invalido");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{veio.Id}/cancelar", t.Atendente), HttpStatusCode.Conflict, "estado-invalido");
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{Guid.NewGuid()}/cancelar", t.Atendente), HttpStatusCode.NotFound, "nao-encontrada");

        using var repetida = await SendAsync(client, HttpMethod.Post, $"/reservas/{cancelada.Id}/cancelar", t.Atendente);
        Assert.Equal(HttpStatusCode.OK, repetida.StatusCode);
        Assert.Equal((1L, 1L, 1L), (await CountAsync("SELECT count(*) FROM comandas"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'reserva:cancelar'"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'reserva:nao-compareceu'")));
    }

    [Fact]
    public async Task Input_that_is_missing_blank_out_of_range_or_in_the_past_is_a_400_and_nothing_is_written()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var depois = Segunda.AddHours(2);

        foreach (var ruim in new object[]
        {
            new { },
            new { nome = Nome, pessoas = 2 },
            new { nome = Nome, horario = depois },
            new { nome = "   ", pessoas = 2, horario = depois },
            new { nome = new string('x', RegrasDeReserva.NomeMaximo + 1), pessoas = 2, horario = depois },
            new { nome = Nome, pessoas = 0, horario = depois },
            new { nome = Nome, pessoas = 1000, horario = depois },
            new { nome = Nome, pessoas = 2, horario = depois, telefone = "123" },
            new { nome = Nome, pessoas = 2, horario = depois, telefone = "abcdefghijk" },
            new { nome = Nome, pessoas = 2, horario = depois, observacao = new string('o', 201) },
            new { nome = Nome, pessoas = 2, horario = Segunda.AddMinutes(-1) },
        })
        {
            await AssertErroAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.Atendente, ruim), HttpStatusCode.BadRequest, "invalido");
        }

        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.Atendente, Corpo(depois, Guid.NewGuid())), HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal((0L, 0L), (await CountAsync("SELECT count(*) FROM reservas"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'reserva:criar'")));
    }

    [Fact]
    public async Task The_booking_time_must_fall_inside_the_opening_hours_and_arriving_is_refused_when_the_unit_closed_meanwhile()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var reserva = await CriarAsync(client, t.Atendente, Segunda.AddHours(1), await MesaAsync("1"));
        using (var salvar = await SendAsync(client, HttpMethod.Put, "/agenda/semana", t.Dono, new { semana = Enumerable.Repeat<object?>(new { abre = "19:00", fecha = "23:00" }, 7).ToArray() }))
        {
            Assert.Equal(HttpStatusCode.NoContent, salvar.StatusCode);
        }

        // Booked for 15:00 local (closed until 19:00) and for 20:00 local (open).
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, "/reservas", t.Atendente, Corpo(Segunda.AddHours(3))), HttpStatusCode.Conflict, "fora-do-horario");
        await CriarAsync(client, t.Atendente, Segunda.AddHours(8));
        // It is 12:00 local and the unit is closed: the guests cannot be seated now.
        await AssertErroAsync(await SendAsync(client, HttpMethod.Post, $"/reservas/{reserva.Id}/chegou", t.Atendente), HttpStatusCode.Conflict, "fora-do-horario");

        Assert.Equal((0L, 2L, 0L), (await CountAsync("SELECT count(*) FROM comandas"), await CountAsync("SELECT count(*) FROM reservas"), await CountAsync("SELECT count(*) FROM reservas WHERE estado = 'chegou'")));
    }

    [Fact]
    public async Task A_reservation_belongs_to_the_operating_day_of_its_time_and_the_list_defaults_to_the_current_day()
    {
        await using var api = NewApi(_appConnection, new MovableClock(Segunda));
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var hoje = await CriarAsync(client, t.Atendente, Segunda.AddHours(2));
        var cedoDeAmanha = await CriarAsync(client, t.Atendente, Segunda.AddHours(16)); // 04:00 local Tuesday: still Monday's operating day (rollover 05:00)
        var amanha = await CriarAsync(client, t.Atendente, Segunda.AddHours(24));

        Assert.Equal((new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17)), (hoje.DiaOperacional, cedoDeAmanha.DiaOperacional, amanha.DiaOperacional));
        using var padrao = await SendAsync(client, HttpMethod.Get, "/reservas", t.SoVer);
        Assert.Equal([hoje.Id, cedoDeAmanha.Id], (await padrao.Content.ReadFromJsonAsync<List<ReservaBody>>())!.Select(x => x.Id));
        using var outro = await SendAsync(client, HttpMethod.Get, "/reservas?dia=2026-11-17", t.SoVer);
        Assert.Equal([amanha.Id], (await outro.Content.ReadFromJsonAsync<List<ReservaBody>>())!.Select(x => x.Id));
        using var invalido = await SendAsync(client, HttpMethod.Get, "/reservas?dia=ontem", t.SoVer);
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
    }

    [Fact]
    public async Task The_table_map_and_the_table_list_show_a_free_table_as_reserved_only_while_a_waiting_booking_is_due()
    {
        var clock = new MovableClock(Segunda);
        await using var api = NewApi(_appConnection, clock);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mesa = await MesaAsync("1");
        var longe = await MesaAsync("2");
        var proxima = await CriarAsync(client, t.Atendente, Segunda.AddMinutes(90), mesa);
        await CriarAsync(client, t.Atendente, Segunda.AddHours(5), longe);

        var antes = await MapaAsync(client, t.Atendente);
        Assert.Equal(("reservada", (DateTimeOffset?)Segunda.AddMinutes(90), "livre", (DateTimeOffset?)null), (antes[mesa].Status, antes[mesa].ReservadaPara, antes[longe].Status, antes[longe].ReservadaPara));
        using (var lista = await SendAsync(client, HttpMethod.Get, "/mesas", t.Atendente))
        {
            var mesas = (await lista.Content.ReadFromJsonAsync<List<MesaListaBody>>())!.ToDictionary(m => m.Id, m => m.Status);
            Assert.Equal(("reservada", "livre"), (mesas[mesa], mesas[longe]));
        }

        // Cancelled: back to free. Booked again and arrived: occupied (the comanda wins over the reservation).
        (await SendAsync(client, HttpMethod.Post, $"/reservas/{proxima.Id}/cancelar", t.Atendente)).Dispose();
        Assert.Equal("livre", (await MapaAsync(client, t.Atendente))[mesa].Status);
        var segunda = await CriarAsync(client, t.Atendente, Segunda.AddMinutes(90), mesa);
        (await SendAsync(client, HttpMethod.Post, $"/reservas/{segunda.Id}/chegou", t.Atendente)).Dispose();
        var depois = await MapaAsync(client, t.Atendente);
        Assert.Equal(("ocupada", 1, (DateTimeOffset?)null), (depois[mesa].Status, depois[mesa].ComandasAbertas, depois[mesa].ReservadaPara));

        // A waiting booking that is 3 hours late no longer holds the table.
        clock.UtcNow = Segunda.AddHours(8);
        var t2 = await MintAsync(api, Atendente, OrgA);
        Assert.Equal("livre", (await MapaAsync(client, t2))[longe].Status);
    }

    [Fact]
    public async Task A_failure_after_the_audit_rolls_back_the_reservation_and_the_arrival_with_its_comanda()
    {
        var clock = new MovableClock(Segunda);
        await using var api = NewApi(_appConnection, clock, services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IAuditLog));
            services.Remove(real);
            services.AddSingleton<IAuditLog>(p => new SwitchableAudit(ActivatorUtilities.CreateInstance<Flow4Tech.Infrastructure.Audit.PostgresAuditLog>(p)));
        });
        var gestao = api.Services.GetRequiredService<GestaoDeReservas>();
        var audit = (SwitchableAudit)api.Services.GetRequiredService<IAuditLog>();
        var dono = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var mesa = await MesaAsync("1");

        audit.Throws = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => gestao.CriarAsync(dono, Nome, Telefone, 2, Segunda.AddHours(1), mesa, null));
        Assert.Equal((0L, 0L), (await CountAsync("SELECT count(*) FROM reservas"), await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao = 'reserva:criar'")));

        audit.Throws = false;
        var criada = await gestao.CriarAsync(dono, Nome, Telefone, 2, Segunda.AddHours(1), mesa, null);
        Assert.Equal(ResultadoDeReserva.Ok, criada.Resultado);
        audit.Throws = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => gestao.ChegouAsync(dono, criada.Reserva!.Id));

        Assert.Equal((0L, 1L), (await CountAsync("SELECT count(*) FROM comandas"), await CountAsync("SELECT count(*) FROM reservas WHERE estado = 'reservada' AND comanda_id IS NULL")));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM auditoria_registros WHERE acao IN ('comanda:abrir', 'reserva:chegou')"));
    }

    /// <summary>Real audit that can be told to fail right after writing.</summary>
    private sealed class SwitchableAudit(IAuditLog inner) : IAuditLog
    {
        public bool Throws { get; set; }

        public async Task RecordAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default)
        {
            await inner.RecordAsync(organization, entry, cancellationToken);
            if (Throws)
            {
                throw new InvalidOperationException("boom after audit");
            }
        }

        public Task RecordDetachedAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default) =>
            inner.RecordDetachedAsync(organization, entry, cancellationToken);
    }

    [Fact]
    public async Task The_reservation_queries_have_the_expected_sql_with_no_organization_filter_and_the_EF_model_matches_the_database()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new MesasDbContext(tx);

        var dia = PostgresReservasStore.DoDia(context, UnitA, new DateOnly(2026, 11, 16)).ToQueryString();
        var espera = PostgresReservasStore.EmEspera(context, UnitA).ToQueryString();

        Assert.Contains("FROM reservas AS", dia, StringComparison.Ordinal);
        Assert.Contains("r.dia_operacional = @", dia, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", dia, StringComparison.Ordinal);
        Assert.Contains("r.estado = 'reservada'", espera, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", dia + espera, StringComparison.Ordinal);
        Assert.Empty(await SchemaDrift.FindAsync(context));
    }

    private static async Task<Dictionary<Guid, MesaMapaBody>> MapaAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/mesas/mapa", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mapa = (await response.Content.ReadFromJsonAsync<MapaBody>())!;
        return mapa.Areas.SelectMany(a => a.Mesas).Concat(mapa.SemArea).ToDictionary(m => m.Id);
    }
}
