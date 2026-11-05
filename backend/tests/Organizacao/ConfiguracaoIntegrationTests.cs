using System.Text.Json.Nodes;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Permissoes.Application;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;
using static Flow4Tech.Organizacao.Tests.ConfiguracaoEnv;

namespace Flow4Tech.Organizacao.Tests;

/// <summary>Layered settings on a real PostgreSQL (ticket 10, spec 5.7): inheritance, lock, history, restore, start instant, permission, audit, RLS.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class ConfiguracaoIntegrationTests(PostgresFixture postgres)
{
    private static AlterarConfiguracao Org(string valor, bool? travado = null, DateTimeOffset? desde = null, string? motivo = null) =>
        new(Limite.Key, CamadaDeEscrita.Organizacao, valor, travado, desde, motivo);

    private static AlterarConfiguracao Unidade(string? valor, DateTimeOffset? desde = null) =>
        new(Limite.Key, CamadaDeEscrita.Unidade, valor, null, desde, null);

    [Fact]
    public async Task The_value_comes_from_the_system_then_the_organization_then_the_unit()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);

        Assert.Equal(("10", SettingLayer.Sistema), Par(await env.ResolverAsync(Limite, OrgA, UnitA)));
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Org("20")));
        Assert.Equal(("20", SettingLayer.Organizacao), Par(await env.ResolverAsync(Limite, OrgA, UnitA)));
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Unidade("30")));
        Assert.Equal(("30", SettingLayer.Unidade), Par(await env.ResolverAsync(Limite, OrgA, UnitA)));
        Assert.Equal(("20", SettingLayer.Organizacao), Par(await env.ResolverAsync(Limite, OrgA, null))); // no unit asked: the organization value

        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Unidade(null))); // inherit again
        Assert.Equal(("20", SettingLayer.Organizacao), Par(await env.ResolverAsync(Limite, OrgA, UnitA)));
    }

    [Fact]
    public async Task The_system_layer_can_come_from_configuration_and_the_other_tenant_is_untouched()
    {
        await using var env = await CreateAsync(postgres, new() { ["Flow4Tech:Configuracoes:teste.limite"] = "15" });

        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(ComoDonoA(), Org("20")));

        Assert.Equal(("20", SettingLayer.Organizacao), Par(await env.ResolverAsync(Limite, OrgA, null)));
        Assert.Equal(("15", SettingLayer.Sistema), Par(await env.ResolverAsync(Limite, OrgB, null)));
        Assert.Equal(("7", SettingLayer.Sistema), Par(await env.ResolverAsync(SoSistema, OrgA, UnitA)));
    }

    [Fact]
    public async Task A_lock_in_the_organization_stops_the_unit_from_overriding_and_unlocking_brings_its_value_back()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20"));
        _ = await env.Configuracoes.AlterarAsync(pedido, Unidade("30"));

        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Org("20", travado: true)));
        var travada = await env.ResolverAsync(Limite, OrgA, UnitA);
        Assert.Equal(("20", SettingLayer.Organizacao, true), (travada.Value, travada.Source, travada.LockedByOrganization)); // the earlier unit value is inert
        Assert.Equal(ResultadoConfiguracao.Travada, await env.Configuracoes.AlterarAsync(pedido, Unidade("40")));
        Assert.Equal(ResultadoConfiguracao.Travada, await env.Configuracoes.AlterarAsync(pedido, Unidade(null)));

        // Changing the organization value without a word on the lock keeps it locked; saying false unlocks.
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Org("25")));
        Assert.True((await env.ResolverAsync(Limite, OrgA, UnitA)).LockedByOrganization);
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Org("25", travado: false)));
        Assert.Equal(("30", SettingLayer.Unidade), Par(await env.ResolverAsync(Limite, OrgA, UnitA)));
    }

    [Fact]
    public async Task A_lock_scheduled_for_later_does_not_block_a_unit_change_made_before_it_starts()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        var amanha = env.Clock.UtcNow.AddDays(1);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20", travado: true, desde: amanha));

        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Unidade("30")));
        Assert.Equal("30", (await env.ResolverAsync(Limite, OrgA, UnitA)).Value);
        Assert.Equal(("20", SettingLayer.Organizacao), Par(await env.ResolverAsync(Limite, OrgA, UnitA, amanha))); // once the lock starts, the unit value is inert
        Assert.Equal(ResultadoConfiguracao.Travada, await env.Configuracoes.AlterarAsync(pedido, Unidade("40", desde: amanha.AddHours(1))));
    }

    [Fact]
    public async Task History_lists_who_when_and_what_and_a_restore_is_a_new_version_that_copies_the_old_one()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20", motivo: "inicio"));
        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(1);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("50"));
        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(1);

        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.RestaurarAsync(pedido, Limite.Key, CamadaDeEscrita.Organizacao, 1, null, "voltou"));

        Assert.Equal("20", (await env.ResolverAsync(Limite, OrgA, null)).Value);
        var (resultado, linhas) = await env.Configuracoes.HistoricoAsync(pedido, Limite.Key, CamadaDeEscrita.Organizacao);
        Assert.Equal(ResultadoConfiguracao.Ok, resultado);
        Assert.Equal([3, 2, 1], linhas.Select(l => l.Versao.Versao));
        Assert.Equal(["20", "50", "20"], linhas.Select(l => l.Versao.Valor));
        Assert.Equal([true, false, false], linhas.Select(l => l.EmVigor));
        Assert.Equal([false, true, true], linhas.Select(l => l.Substituida));
        Assert.Equal(1, linhas[0].Versao.RestauraVersao);
        Assert.All(linhas, l => Assert.Equal(DonoA, l.Versao.CriadoPor));
        Assert.Equal(["voltou", null, "inicio"], linhas.Select(l => l.Versao.Motivo));
        Assert.Equal([env.Clock.UtcNow, env.Clock.UtcNow.AddHours(-1), env.Clock.UtcNow.AddHours(-2)], linhas.Select(l => l.Versao.CriadoEm));
    }

    [Fact]
    public async Task Restoring_an_unknown_version_or_into_a_locked_unit_is_refused_and_a_restore_to_inherit_works()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        _ = await env.Configuracoes.AlterarAsync(pedido, Unidade("30")); // v1 unit
        _ = await env.Configuracoes.AlterarAsync(pedido, Unidade(null)); // v2 unit: inherit
        Assert.Equal(ResultadoConfiguracao.VersaoInexistente, await env.Configuracoes.RestaurarAsync(pedido, Limite.Key, CamadaDeEscrita.Unidade, 9, null, null));

        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.RestaurarAsync(pedido, Limite.Key, CamadaDeEscrita.Unidade, 1, null, null));
        Assert.Equal(("30", SettingLayer.Unidade), Par(await env.ResolverAsync(Limite, OrgA, UnitA)));
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.RestaurarAsync(pedido, Limite.Key, CamadaDeEscrita.Unidade, 2, null, null)); // restore of "inherit"
        Assert.Equal(("10", SettingLayer.Sistema), Par(await env.ResolverAsync(Limite, OrgA, UnitA)));

        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20", travado: true));
        Assert.Equal(ResultadoConfiguracao.Travada, await env.Configuracoes.RestaurarAsync(pedido, Limite.Key, CamadaDeEscrita.Unidade, 1, null, null));
    }

    [Fact]
    public async Task A_change_applies_from_its_instant_and_never_reaches_back()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        var inicio = env.Clock.UtcNow;
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20"));
        var daqui2h = inicio.AddHours(2);
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Org("40", desde: daqui2h)));

        Assert.Equal("20", (await env.ResolverAsync(Limite, OrgA, null)).Value);
        Assert.Equal("20", (await env.ResolverAsync(Limite, OrgA, null, daqui2h.AddTicks(-1))).Value);
        Assert.Equal("40", (await env.ResolverAsync(Limite, OrgA, null, daqui2h)).Value);
        env.Clock.UtcNow = daqui2h.AddMinutes(5);
        Assert.Equal("40", (await env.ResolverAsync(Limite, OrgA, null)).Value);
        Assert.Equal("20", (await env.ResolverAsync(Limite, OrgA, null, inicio.AddHours(1))).Value); // the past still reads what applied then
        Assert.Equal("10", (await env.ResolverAsync(Limite, OrgA, null, inicio.AddMinutes(-1))).Value); // before any version: the system value
    }

    [Fact]
    public async Task A_new_change_cancels_a_scheduled_one_that_has_not_started()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20"));
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("40", desde: env.Clock.UtcNow.AddHours(2)));
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20")); // written later, from now: replaces the scheduled one

        Assert.Equal("20", (await env.ResolverAsync(Limite, OrgA, null, env.Clock.UtcNow.AddHours(3))).Value);
        var (_, linhas) = await env.Configuracoes.HistoricoAsync(pedido, Limite.Key, CamadaDeEscrita.Organizacao);
        Assert.Equal([true, false, false], linhas.Select(l => l.EmVigor));
        Assert.Equal([false, true, true], linhas.Select(l => l.Substituida)); // v2 was scheduled and never started; v3 stands in force
        Assert.DoesNotContain(linhas, l => l.Agendada);
    }

    [Fact]
    public async Task An_instant_in_the_past_is_refused_and_an_option_that_needs_an_instant_demands_one()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);

        Assert.Equal(ResultadoConfiguracao.VigenciaInvalida, await env.Configuracoes.AlterarAsync(pedido, Org("20", desde: env.Clock.UtcNow.AddTicks(-1))));
        Assert.Equal(ResultadoConfiguracao.VigenciaInvalida, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao(Virada.Key, CamadaDeEscrita.Unidade, "04:00", null, null, null)));
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao(Virada.Key, CamadaDeEscrita.Unidade, "04:00", null, env.Clock.UtcNow, null)));
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, Org("20", desde: env.Clock.UtcNow)));
        Assert.Equal("04:00", (await env.ResolverAsync(Virada, OrgA, UnitA)).Value);
    }

    [Fact]
    public async Task The_database_refuses_a_version_that_starts_before_it_was_written()
    {
        await using var env = await CreateAsync(postgres);
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            tx,
            "INSERT INTO configuracoes_versoes (id, organizacao_id, chave, versao, valor, vigente_a_partir_de, criado_em) VALUES (gen_random_uuid(), @o, 'teste.limite', 1, '5', @v, @c)",
            ("o", OrgA), ("v", env.Clock.UtcNow.AddMinutes(-1)), ("c", env.Clock.UtcNow)));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Invalid_requests_are_refused_and_leave_no_version_and_no_audit_record()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        var semUnidade = ComoDonoA();

        Assert.Equal(ResultadoConfiguracao.ChaveDesconhecida, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao("nao.existe", CamadaDeEscrita.Organizacao, "1", null, null, null)));
        Assert.Equal(ResultadoConfiguracao.ValorInvalido, await env.Configuracoes.AlterarAsync(pedido, Org("abc")));
        Assert.Equal(ResultadoConfiguracao.ValorInvalido, await env.Configuracoes.AlterarAsync(pedido, Org("101")));
        Assert.Equal(ResultadoConfiguracao.ValorInvalido, await env.Configuracoes.AlterarAsync(pedido, Org("5", motivo: new string('x', 501))));
        Assert.Equal(ResultadoConfiguracao.CamadaInvalida, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao(SoSistema.Key, CamadaDeEscrita.Organizacao, "1", null, null, null)));
        Assert.Equal(ResultadoConfiguracao.CamadaInvalida, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao(SoOrganizacao.Key, CamadaDeEscrita.Unidade, "x", null, null, null)));
        Assert.Equal(ResultadoConfiguracao.CamadaInvalida, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao(Limite.Key, CamadaDeEscrita.Unidade, "5", true, null, null))); // a unit cannot lock
        Assert.Equal(ResultadoConfiguracao.SemUnidadeAtiva, await env.Configuracoes.AlterarAsync(semUnidade, Unidade("5")));

        Assert.Empty(await env.AuditoriaAsync(OrgA));
        Assert.Equal(("10", SettingLayer.Sistema), Par(await env.ResolverAsync(Limite, OrgA, UnitA)));
    }

    [Fact]
    public async Task Changing_needs_the_permission_and_a_denied_person_changes_and_audits_nothing()
    {
        await using var env = await CreateAsync(postgres);
        var semDireito = new PedidoDeTeste(SemPermissao, Ctx(OrgA));

        Assert.Equal(ResultadoConfiguracao.Negado, await env.Configuracoes.AlterarAsync(semDireito, Org("20")));
        Assert.Equal(ResultadoConfiguracao.Negado, await env.Configuracoes.RestaurarAsync(semDireito, Limite.Key, CamadaDeEscrita.Organizacao, 1, null, null));
        Assert.Null(await env.Configuracoes.ListarAsync(semDireito));
        Assert.Equal(ResultadoConfiguracao.Negado, (await env.Configuracoes.HistoricoAsync(semDireito, Limite.Key, CamadaDeEscrita.Organizacao)).Resultado);

        Assert.Equal(("10", SettingLayer.Sistema), Par(await env.ResolverAsync(Limite, OrgA, null)));
        Assert.Empty(await env.AuditoriaAsync(OrgA));
    }

    [Fact]
    public async Task Every_change_and_restore_is_audited_with_the_value_before_and_after()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20"));
        env.Clock.UtcNow = env.Clock.UtcNow.AddMinutes(1);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("50"));
        env.Clock.UtcNow = env.Clock.UtcNow.AddMinutes(1);
        _ = await env.Configuracoes.RestaurarAsync(pedido, Limite.Key, CamadaDeEscrita.Organizacao, 1, null, null);

        Assert.Equal(["configuracao:alterar|sucesso", "configuracao:alterar|sucesso", "configuracao:restaurar|sucesso"], await env.AuditoriaAsync(OrgA));
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        await using var command = new NpgsqlCommand("SELECT antes::text, depois::text, usuario_id, objeto_id FROM auditoria_registros WHERE acao = 'configuracao:alterar' ORDER BY sequencia DESC LIMIT 1", tx.Connection, tx.Transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var antes = JsonNode.Parse(reader.GetString(0))!;
        var depois = JsonNode.Parse(reader.GetString(1))!;
        Assert.Equal("20", (string?)antes["valor"]);
        Assert.Equal("50", (string?)depois["valor"]);
        Assert.Equal(2, (int?)depois["versao"]);
        Assert.Equal("organizacao", (string?)depois["camada"]);
        Assert.Equal(DonoA, reader.GetGuid(2));
        Assert.Equal(Limite.Key, reader.GetString(3));
    }

    [Fact]
    public async Task A_failure_after_the_write_leaves_neither_version_nor_audit_record()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20"));

        // Same transaction: if the unit of work throws after the change, both go away.
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.UnitOfWork.ExecuteAsync(pedido.Organization, async ct =>
        {
            _ = await env.Configuracoes.AlterarAsync(pedido, Org("60"), ct);
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162
            return 0;
#pragma warning restore CS0162
        }));

        Assert.Equal("20", (await env.ResolverAsync(Limite, OrgA, null)).Value);
        Assert.Single(await env.AuditoriaAsync(OrgA));
    }

    [Fact]
    public async Task List_shows_the_effective_value_its_origin_and_what_each_layer_holds()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20", travado: true));

        var lista = await env.Configuracoes.ListarAsync(pedido);

        var limite = Assert.Single(lista!, c => c.Definicao.Key == Limite.Key);
        Assert.Equal(("20", SettingLayer.Organizacao, true, "20", null, "10"), (limite.Valor, limite.Origem, limite.TravadaPelaOrganizacao, limite.ValorDaOrganizacao, limite.ValorDaUnidade, limite.Padrao));
        var soSistema = Assert.Single(lista!, c => c.Definicao.Key == SoSistema.Key);
        Assert.Equal(("7", SettingLayer.Sistema, null), (soSistema.Valor, soSistema.Origem, soSistema.ValorDaOrganizacao));
    }

    [Fact]
    public async Task The_other_tenant_neither_sees_nor_writes_the_versions_of_this_one()
    {
        await using var env = await CreateAsync(postgres);
        _ = await env.Configuracoes.AlterarAsync(ComoDonoA(UnitA), Org("20"));
        _ = await env.Configuracoes.AlterarAsync(new PedidoDeTeste(DonoB, Ctx(OrgB)), Org("33"));

        Assert.Equal("20", (await env.ResolverAsync(Limite, OrgA, null)).Value);
        Assert.Equal("33", (await env.ResolverAsync(Limite, OrgB, null)).Value);

        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgB));
        await using var count = new NpgsqlCommand("SELECT count(*) FROM configuracoes_versoes WHERE valor = '20'", tx.Connection, tx.Transaction);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            tx,
            "INSERT INTO configuracoes_versoes (id, organizacao_id, chave, versao, valor, vigente_a_partir_de, criado_em) VALUES (gen_random_uuid(), @o, 'teste.limite', 9, '1', now(), now())",
            ("o", OrgA)));
        Assert.Equal("42501", ex.SqlState);

        // The history of the other tenant's request is its own too.
        var (_, linhas) = await env.Configuracoes.HistoricoAsync(new PedidoDeTeste(DonoB, Ctx(OrgB)), Limite.Key, CamadaDeEscrita.Organizacao);
        Assert.Equal(["33"], linhas.Select(l => l.Versao.Valor));
    }

    [Fact]
    public async Task The_application_credential_can_only_read_and_append_the_history()
    {
        await using var env = await CreateAsync(postgres);
        _ = await env.Configuracoes.AlterarAsync(ComoDonoA(UnitA), Org("20"));

        foreach (var sql in new[] { "UPDATE configuracoes_versoes SET valor = '99'", "DELETE FROM configuracoes_versoes", "TRUNCATE configuracoes_versoes" })
        {
            await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(tx, sql));
            Assert.Equal("42501", ex.SqlState);
        }
    }

    [Fact]
    public async Task Two_simultaneous_writers_of_the_same_option_get_distinct_versions()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);

        var resultados = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => env.Configuracoes.AlterarAsync(pedido, Org((10 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)))));

        Assert.All(resultados, r => Assert.Equal(ResultadoConfiguracao.Ok, r));
        var (_, linhas) = await env.Configuracoes.HistoricoAsync(pedido, Limite.Key, CamadaDeEscrita.Organizacao);
        Assert.Equal(Enumerable.Range(1, 8).Reverse(), linhas.Select(l => l.Versao.Versao));
        Assert.Equal(8, (await env.AuditoriaAsync(OrgA)).Count);
    }

    [Fact]
    public async Task A_scheduled_change_is_listed_as_scheduled_until_it_starts()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("20"));
        _ = await env.Configuracoes.AlterarAsync(pedido, Org("40", desde: env.Clock.UtcNow.AddHours(1)));

        var (_, antes) = await env.Configuracoes.HistoricoAsync(pedido, Limite.Key, CamadaDeEscrita.Organizacao);
        Assert.Equal([(false, true), (true, false)], antes.Select(l => (l.EmVigor, l.Agendada)));

        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(1);
        var (_, depois) = await env.Configuracoes.HistoricoAsync(pedido, Limite.Key, CamadaDeEscrita.Organizacao);
        Assert.Equal([(true, false), (false, false)], depois.Select(l => (l.EmVigor, l.Agendada)));
    }

    [Fact]
    public async Task The_operating_day_options_are_stored_but_the_resolver_keeps_reading_its_own_default()
    {
        await using var env = await CreateAsync(postgres);
        var pedido = ComoDonoA(UnitA);
        env.Clock.UtcNow = new DateTimeOffset(2026, 11, 5, 7, 30, 0, TimeSpan.Zero); // 04:30 in Sao Paulo: before the 05:00 rollover, still "yesterday"
        var resolvedor = new ConfiguredOperatingDayResolver(env.Clock);
        var antes = await resolvedor.CurrentAsync(OrgA, UnitA, CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 11, 4), antes);

        var desde = env.Clock.UtcNow;
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao(OrganizacaoSettings.FusoHorario.Key, CamadaDeEscrita.Unidade, "Asia/Tokyo", null, desde, null)));
        Assert.Equal(ResultadoConfiguracao.Ok, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao(OrganizacaoSettings.ViradaDoDia.Key, CamadaDeEscrita.Unidade, "00:00", null, desde, null)));
        Assert.Equal(ResultadoConfiguracao.ValorInvalido, await env.Configuracoes.AlterarAsync(pedido, new AlterarConfiguracao(OrganizacaoSettings.FusoHorario.Key, CamadaDeEscrita.Unidade, "Marte/Olimpo", null, desde, null)));

        Assert.Equal("Asia/Tokyo", (await env.ResolverAsync(OrganizacaoSettings.FusoHorario, OrgA, UnitA)).Value); // the layered value is persisted and readable...
        Assert.Equal(antes, await resolvedor.CurrentAsync(OrgA, UnitA, CancellationToken.None)); // ...and the resolver of the operating day does not read it yet (roadmap 2.10b)
    }

    private static (string, SettingLayer) Par(ResolvedSetting r) => (r.Value, r.Source);
}
