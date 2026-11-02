using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Ticket 04 (spec 5.3): sessions, rotating refresh with reuse detection, immediate revocation, listing. Real PostgreSQL, application credential (RLS on).</summary>
[Collection(PostgresDefinition.Name)]
public sealed class SessoesIntegrationTests(PostgresFixture postgres)
{
    private static readonly ClienteDaSessao Cliente = new("203.0.113.9", "Mozilla/5.0 (teste)");

    private sealed record Setup(Guid Dono, Guid Gerente, Guid Maria);

    private static async Task<Setup> SeedAsync(IdentidadeEnv env)
    {
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        await env.VincularAsync(OrgA, dono, UnitA);
        await env.VincularAsync(OrgA, maria, UnitA);
        return new Setup(dono, gerente, maria);
    }

    private static Task<SessaoEmitida> Entrar(IdentidadeEnv env, Guid conta, bool deveTrocar = false, int versao = 1) =>
        env.Sessoes.IniciarAsync(conta, OrgA, versao, deveTrocar, Cliente);

    // ---- start of a session ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_session_makes_the_only_linked_unit_active_and_stores_a_hash_of_the_refresh_never_the_token()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var emitida = await Entrar(env, s.Maria);

        Assert.Equal(UnitA, emitida.UnidadeAtivaId);
        Assert.False(emitida.DeveTrocarSenha);
        var autenticada = await env.Sessoes.AutenticarAsync(emitida.AccessToken);
        Assert.Equal(new SessaoAutenticada(emitida.SessaoId, s.Maria, OrgA, UnitA, false), autenticada);
        Assert.Empty(await env.TablesContainingAsync(emitida.RefreshToken));
        Assert.Empty(await env.TablesContainingAsync(emitida.RefreshToken.Split('.')[^1]));
        Assert.Empty(await env.TablesContainingAsync(emitida.AccessToken));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM sessao_refresh WHERE length(hash) = 32"));
    }

    [Fact]
    public async Task A_session_with_several_linked_units_or_none_starts_without_an_active_unit()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var outraUnidade = await env.AddUnitAsync(OrgA);
        await env.VincularAsync(OrgA, s.Maria, outraUnidade);

        var varias = await Entrar(env, s.Maria);
        var nenhuma = await Entrar(env, s.Gerente);

        Assert.Null(varias.UnidadeAtivaId);
        Assert.Null(nenhuma.UnidadeAtivaId);
        Assert.Null((await env.Sessoes.AutenticarAsync(varias.AccessToken))!.UnidadeAtivaId);
    }

    [Fact]
    public async Task Ip_and_agent_are_truncated_and_the_row_carries_the_clocks_and_the_credential_version()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var emitida = await env.Sessoes.IniciarAsync(s.Maria, OrgA, 1, false, new ClienteDaSessao(new string('1', 200), new string('x', 5000)));

        var row = (await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT length(ip), length(agente), versao_credencial, expira_em - criada_em, expira_inatividade_em - criada_em, familia_id FROM sessoes WHERE id = @i",
            r => (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetTimeSpan(3), r.GetTimeSpan(4), r.GetGuid(5)), ("i", emitida.SessaoId)))[0];
        Assert.True(row.Item1 <= 45);
        Assert.True(row.Item2 <= 200);
        Assert.Equal(1, row.Item3);
        Assert.Equal(TimeSpan.FromDays(14), row.Item4);
        Assert.Equal(TimeSpan.FromHours(12), row.Item5);
        Assert.NotEqual(Guid.Empty, row.Item6);
    }

    [Fact]
    public async Task Starting_a_session_is_audited_without_any_secret()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var emitida = await Entrar(env, s.Maria);

        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "sessao:iniciar", "sucesso"));
        var row = (await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT usuario_id, sessao_id, unidade_id FROM auditoria_registros WHERE acao = 'sessao:iniciar'",
            r => (r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2))))[0];
        Assert.Equal((s.Maria, emitida.SessaoId, (Guid?)UnitA), row);
        Assert.Empty(await env.TablesContainingAsync(emitida.RefreshToken)); // includes auditoria_registros
    }

    // ---- per-request check -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_of_the_control_realm_or_garbage_does_not_authenticate()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, o => o.Tokens.ControlKey = "chave-control-0123456789-0123456789-01234");
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);
        var control = env.Get<IEmissorDeTokens>().EmitirAcesso(RealmDeToken.Control, new ClaimsDeAcesso(s.Maria, OrgA, emitida.SessaoId, 1));

        Assert.Null(await env.Sessoes.AutenticarAsync(control));
        Assert.Null(await env.Sessoes.AutenticarAsync("lixo"));
    }

    [Fact]
    public async Task A_valid_token_for_a_session_that_does_not_exist_or_for_another_account_does_not_authenticate()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emissor = env.Get<IEmissorDeTokens>();
        var emitida = await Entrar(env, s.Maria);

        Assert.Null(await env.Sessoes.AutenticarAsync(emissor.EmitirAcesso(RealmDeToken.App, new ClaimsDeAcesso(s.Maria, OrgA, Guid.NewGuid(), 1))));
        Assert.Null(await env.Sessoes.AutenticarAsync(emissor.EmitirAcesso(RealmDeToken.App, new ClaimsDeAcesso(s.Dono, OrgA, emitida.SessaoId, 1)))); // right session, wrong subject
        Assert.Null(await env.Sessoes.AutenticarAsync(emissor.EmitirAcesso(RealmDeToken.App, new ClaimsDeAcesso(s.Maria, OrgB, emitida.SessaoId, 1)))); // right session, other organization (RLS hides it)
        Assert.Null(await env.Sessoes.AutenticarAsync(emissor.EmitirAcesso(RealmDeToken.App, new ClaimsDeAcesso(s.Maria, OrgA, emitida.SessaoId, 2)))); // version that does not match
    }

    [Fact]
    public async Task Activity_slides_the_inactivity_window_but_never_the_absolute_limit()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, o =>
        {
            o.Sessoes.Inatividade = TimeSpan.FromHours(2);
            o.Sessoes.Absoluta = TimeSpan.FromHours(5);
        });
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);

        foreach (var step in new[] { 1.5, 1.5, 1.5 }) // always inside the 2 h inactivity window, so the activity keeps sliding it: 4.5 h in
        {
            env.Clock.UtcNow = env.Clock.UtcNow.AddHours(step);
            Assert.NotNull(await env.Sessoes.AutenticarAsync(await NovoAcesso(env, emitida, s.Maria)));
        }

        // Past the 5 h absolute limit although the user is active (the last activity was 1 h ago).
        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(1);
        Assert.Null(await env.Sessoes.AutenticarAsync(await NovoAcesso(env, emitida, s.Maria)));
    }

    private static Task<string> NovoAcesso(IdentidadeEnv env, SessaoEmitida emitida, Guid conta) =>
        Task.FromResult(env.Get<IEmissorDeTokens>().EmitirAcesso(RealmDeToken.App, new ClaimsDeAcesso(conta, OrgA, emitida.SessaoId, 1)));

    [Fact]
    public async Task Inactivity_ends_the_session_and_the_refresh_of_an_inactive_session_does_not_work()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);

        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(12).AddMinutes(1);

        Assert.Null(await env.Sessoes.AutenticarAsync(await NovoAcesso(env, emitida, s.Maria)));
        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(emitida.RefreshToken, Cliente)).Status);
    }

    [Fact]
    public async Task The_clocks_are_configurable_by_option()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, o => o.Sessoes.Absoluta = TimeSpan.FromMinutes(30));
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);

        Assert.Equal(env.Clock.UtcNow.AddMinutes(30), emitida.SessaoExpiraEm);
        env.Clock.UtcNow = env.Clock.UtcNow.AddMinutes(31);
        Assert.Null(await env.Sessoes.AutenticarAsync(await NovoAcesso(env, emitida, s.Maria)));
    }

    // ---- immediate revocation: one test per trigger -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deactivating_the_account_ends_its_sessions_on_the_next_request()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);
        Assert.NotNull(await env.Sessoes.AutenticarAsync(emitida.AccessToken));

        Assert.True((await env.Contas.DesativarAsync(IdentidadeEnv.As(s.Dono, OrgA), s.Maria)).Permitido);

        Assert.Null(await env.Sessoes.AutenticarAsync(emitida.AccessToken));
        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(emitida.RefreshToken, Cliente)).Status);
    }

    [Fact]
    public async Task Changing_the_password_ends_every_session_of_the_account_and_the_new_one_works()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var a = await Entrar(env, s.Dono);
        var b = await Entrar(env, s.Dono);

        var troca = await env.Troca.TrocarAsync(OrgA, s.Dono, IdentidadeEnv.Senha, "outra-senha-boa-2026");
        Assert.Equal(StatusTrocaDeSenha.Trocada, troca.Status);

        Assert.Null(await env.Sessoes.AutenticarAsync(a.AccessToken));
        Assert.Null(await env.Sessoes.AutenticarAsync(b.AccessToken));
        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(a.RefreshToken, Cliente)).Status);
        var nova = await env.Sessoes.IniciarAposTrocaDeSenhaAsync(OrgA, s.Dono, Cliente);
        Assert.NotNull(nova);
        Assert.False(nova.DeveTrocarSenha);
        Assert.NotNull(await env.Sessoes.AutenticarAsync(nova.AccessToken));
        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT count(*) FROM sessoes WHERE conta_id = @c AND revogada_em IS NOT NULL AND revogada_motivo = 'senha-trocada'", ("c", s.Dono)));
    }

    [Fact]
    public async Task Changing_the_owner_flag_ends_the_sessions_of_that_account()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Gerente);
        Assert.NotNull(await env.Sessoes.AutenticarAsync(emitida.AccessToken));

        Assert.True((await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(s.Dono, OrgA), s.Gerente, true)).Permitido);

        Assert.Null(await env.Sessoes.AutenticarAsync(emitida.AccessToken));
    }

    [Fact]
    public async Task Removing_the_link_to_the_active_unit_revokes_that_session_but_not_sessions_in_other_units()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var outra = await env.AddUnitAsync(OrgA);
        await env.VincularAsync(OrgA, s.Maria, outra);
        var naUnidadeA = await Entrar(env, s.Maria);
        await DefinirUnidade(env, naUnidadeA, UnitA);
        var naOutra = await Entrar(env, s.Maria);
        await DefinirUnidade(env, naOutra, outra);

        await env.DesvincularAsync(OrgA, s.Maria, UnitA);

        Assert.Null(await env.Sessoes.AutenticarAsync(naUnidadeA.AccessToken));
        Assert.NotNull(await env.Sessoes.AutenticarAsync(naOutra.AccessToken));
        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(naUnidadeA.RefreshToken, Cliente)).Status);
    }

    private static Task DefinirUnidade(IdentidadeEnv env, SessaoEmitida emitida, Guid unidade) =>
        env.Uow.ExecuteAsync(IdentidadeEnv.Pre(OrgA), ct => env.Sessoes.DefinirUnidadeAtivaAsync(OrgA, emitida.SessaoId, unidade, ct));

    [Fact]
    public async Task Revoking_a_session_ends_it_immediately()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);

        var resultado = await env.Sessoes.RevogarAsync(new FakeRequest(s.Maria, emitida.SessaoId, Ctx(OrgA)), emitida.SessaoId);

        Assert.Equal(ResultadoRevogacao.Revogada, resultado);
        Assert.Null(await env.Sessoes.AutenticarAsync(emitida.AccessToken));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "sessao:revogar", "sucesso"));
    }

    // ---- unit of the session is part of the switch's unit of work -------------------------------------------------------------------------------------

    [Fact]
    public async Task Setting_the_active_unit_rolls_back_with_the_unit_of_work_that_failed()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var outra = await env.AddUnitAsync(OrgA);
        await env.VincularAsync(OrgA, s.Maria, outra);
        var emitida = await Entrar(env, s.Maria);
        Assert.Null(emitida.UnidadeAtivaId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Uow.ExecuteAsync(IdentidadeEnv.Pre(OrgA), async ct =>
        {
            await env.Sessoes.DefinirUnidadeAtivaAsync(OrgA, emitida.SessaoId, outra, ct);
            throw new InvalidOperationException("boom");
        }));

        Assert.Null((await env.Sessoes.AutenticarAsync(emitida.AccessToken))!.UnidadeAtivaId);
        await DefinirUnidade(env, emitida, outra);
        Assert.Equal(outra, (await env.Sessoes.AutenticarAsync(emitida.AccessToken))!.UnidadeAtivaId);
    }

    [Fact]
    public async Task Setting_the_active_unit_outside_a_unit_of_work_is_refused()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Sessoes.DefinirUnidadeAtivaAsync(OrgA, emitida.SessaoId, UnitA, CancellationToken.None));
    }

    // ---- rotating refresh ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_refresh_issues_a_new_token_and_retires_the_previous_one()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var primeira = await Entrar(env, s.Maria);

        var segunda = await env.Sessoes.RenovarAsync(primeira.RefreshToken, Cliente);
        var terceira = await env.Sessoes.RenovarAsync(segunda.Sessao!.RefreshToken, Cliente);

        Assert.Equal(StatusRenovacao.Renovada, segunda.Status);
        Assert.Equal(StatusRenovacao.Renovada, terceira.Status);
        Assert.Equal(3, new[] { primeira.RefreshToken, segunda.Sessao.RefreshToken, terceira.Sessao!.RefreshToken }.Distinct().Count());
        Assert.Equal(primeira.SessaoId, terceira.Sessao.SessaoId);
        Assert.NotNull(await env.Sessoes.AutenticarAsync(terceira.Sessao.AccessToken));
    }

    [Fact]
    public async Task The_refresh_token_is_256_bits_of_randomness_with_an_organization_locator()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var tokens = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            tokens.Add((await Entrar(env, s.Maria)).RefreshToken);
        }

        Assert.Equal(20, tokens.Distinct().Count());
        var parts = tokens[0].Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal(OrgA, Guid.ParseExact(parts[1], "N"));
        Assert.Equal(32, Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/') + "=").Length);
    }

    [Fact]
    public async Task Presenting_a_used_refresh_again_revokes_the_whole_family_audits_and_raises_the_security_event()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var primeira = await Entrar(env, s.Maria);
        var segunda = (await env.Sessoes.RenovarAsync(primeira.RefreshToken, Cliente)).Sessao!;

        var reuso = await env.Sessoes.RenovarAsync(primeira.RefreshToken, Cliente);

        Assert.Equal(StatusRenovacao.Reutilizada, reuso.Status);
        Assert.Null(reuso.Sessao);
        // The legitimate holder of the NEWEST token is cut off too: the whole chain is dead.
        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(segunda.RefreshToken, Cliente)).Status);
        Assert.Null(await env.Sessoes.AutenticarAsync(segunda.AccessToken));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM sessoes WHERE id = @i AND revogada_motivo = 'refresh-reutilizado' AND revogada_em IS NOT NULL", ("i", primeira.SessaoId)));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "sessao:refresh-reutilizado", "negado"));
        var evento = (await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT tipo, payload::text FROM outbox_eventos WHERE tipo = 'seguranca.refresh-reutilizado'", r => (r.GetString(0), r.GetString(1))));
        var linha = Assert.Single(evento);
        Assert.Contains(primeira.SessaoId.ToString("D"), linha.Item2, StringComparison.Ordinal);
        Assert.DoesNotContain(primeira.RefreshToken, linha.Item2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reuse_is_reported_once_per_family_and_a_second_reuse_attempt_does_not_pile_up_audit_records()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var primeira = await Entrar(env, s.Maria);
        await env.Sessoes.RenovarAsync(primeira.RefreshToken, Cliente);

        await env.Sessoes.RenovarAsync(primeira.RefreshToken, Cliente);
        await env.Sessoes.RenovarAsync(primeira.RefreshToken, Cliente);

        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "sessao:refresh-reutilizado"));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'seguranca.refresh-reutilizado'"));
    }

    [Fact]
    public async Task Two_simultaneous_refreshes_with_the_same_token_one_wins_and_the_other_is_reuse_which_revokes_the_session()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var primeira = await Entrar(env, s.Maria);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => env.Sessoes.RenovarAsync(primeira.RefreshToken, Cliente))));

        Assert.Equal(1, resultados.Count(r => r.Status == StatusRenovacao.Renovada));
        Assert.Equal(1, resultados.Count(r => r.Status == StatusRenovacao.Reutilizada));
        // Strictest safe choice (ADR): the loser is indistinguishable from a replay, so the family dies, the winner's new token included.
        var vencedor = resultados.Single(r => r.Status == StatusRenovacao.Renovada).Sessao!;
        Assert.Null(await env.Sessoes.AutenticarAsync(vencedor.AccessToken));
        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(vencedor.RefreshToken, Cliente)).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lixo")]
    [InlineData("r1.nao-e-guid.AAAA")]
    [InlineData("r1.0197a00000007000800000000000000a.curto")]
    public async Task A_malformed_refresh_is_invalid_without_touching_anything(string token)
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        await Entrar(env, s.Maria);

        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(token, Cliente)).Status);
        Assert.Equal(0L, await env.AuditCountAsync(OrgA, "sessao:refresh-reutilizado"));
    }

    [Fact]
    public async Task A_well_formed_refresh_that_was_never_issued_is_invalid_and_never_reuse()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);
        var invented = $"r1.{OrgA:N}.{Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(invented, Cliente)).Status);
        Assert.NotNull(await env.Sessoes.AutenticarAsync(emitida.AccessToken)); // the real session is untouched
    }

    [Fact]
    public async Task A_refresh_of_one_organization_presented_under_the_locator_of_another_finds_nothing()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);
        var parts = emitida.RefreshToken.Split('.');

        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync($"{parts[0]}.{OrgB:N}.{parts[2]}", Cliente)).Status);
        Assert.NotNull(await env.Sessoes.AutenticarAsync(emitida.AccessToken));
    }

    [Fact]
    public async Task Refresh_after_a_credential_change_is_invalid_and_does_not_count_as_reuse()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Dono);
        await env.Troca.TrocarAsync(OrgA, s.Dono, IdentidadeEnv.Senha, "outra-senha-boa-2026");

        Assert.Equal(StatusRenovacao.Invalida, (await env.Sessoes.RenovarAsync(emitida.RefreshToken, Cliente)).Status);
        Assert.Equal(0L, await env.AuditCountAsync(OrgA, "sessao:refresh-reutilizado"));
    }

    [Fact]
    public async Task Old_used_refresh_rows_are_pruned_so_the_table_does_not_grow_with_every_rotation()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, o =>
        {
            o.Sessoes.Inatividade = TimeSpan.FromDays(30);
            o.Sessoes.Absoluta = TimeSpan.FromDays(60);
        });
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);
        var atual = emitida.RefreshToken;

        for (var i = 0; i < 5; i++)
        {
            env.Clock.UtcNow = env.Clock.UtcNow.AddHours(25);
            atual = (await env.Sessoes.RenovarAsync(atual, Cliente)).Sessao!.RefreshToken;
        }

        Assert.True(await env.CountAsync(OrgA, "SELECT count(*) FROM sessao_refresh WHERE sessao_id = @s", ("s", emitida.SessaoId)) <= 2);
    }

    // ---- state "must change password" -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_session_started_for_a_temporary_credential_carries_the_pending_state()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var emitida = await Entrar(env, s.Maria, deveTrocar: true);

        Assert.True(emitida.DeveTrocarSenha);
        Assert.True((await env.Sessoes.AutenticarAsync(emitida.AccessToken))!.DeveTrocarSenha);
        // The state survives a refresh: it cannot be shed by refreshing.
        var renovada = (await env.Sessoes.RenovarAsync(emitida.RefreshToken, Cliente)).Sessao!;
        Assert.True(renovada.DeveTrocarSenha);
        Assert.True((await env.Sessoes.AutenticarAsync(renovada.AccessToken))!.DeveTrocarSenha);
    }

    // ---- logout ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Logout_by_refresh_revokes_the_session_audits_it_and_is_idempotent()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);

        await env.Sessoes.EncerrarPorRefreshAsync(emitida.RefreshToken);
        await env.Sessoes.EncerrarPorRefreshAsync(emitida.RefreshToken);
        await env.Sessoes.EncerrarPorRefreshAsync("lixo");

        Assert.Null(await env.Sessoes.AutenticarAsync(emitida.AccessToken));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "sessao:encerrar", "sucesso"));
    }

    // ---- listing and administration ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_user_lists_only_their_own_live_sessions_and_the_current_one_is_marked()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var a = await Entrar(env, s.Maria);
        var b = await Entrar(env, s.Maria);
        await Entrar(env, s.Dono);
        var encerrada = await Entrar(env, s.Maria);
        await env.Sessoes.EncerrarPorRefreshAsync(encerrada.RefreshToken);

        var lista = await env.Sessoes.ListarPropriasAsync(new FakeRequest(s.Maria, a.SessaoId, Ctx(OrgA)));

        Assert.Equal(new[] { a.SessaoId, b.SessaoId }.Order(), lista.Select(x => x.Id).Order());
        Assert.All(lista, x => Assert.Equal(s.Maria, x.ContaId));
        Assert.Equal(a.SessaoId, Assert.Single(lista, x => x.Atual).Id);
    }

    [Fact]
    public async Task Only_an_owner_lists_the_sessions_of_everybody_for_now_and_never_of_another_organization()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");
        var delaB = await env.Sessoes.IniciarAsync(donoB, OrgB, 1, false, Cliente);
        var maria = await Entrar(env, s.Maria);
        var doDono = await Entrar(env, s.Dono);

        Assert.Null(await env.Sessoes.ListarTodasAsync(new FakeRequest(s.Gerente, Ctx(OrgA))));
        Assert.Null(await env.Sessoes.ListarTodasAsync(new FakeRequest(s.Maria, maria.SessaoId, Ctx(OrgA))));
        var todas = await env.Sessoes.ListarTodasAsync(new FakeRequest(s.Dono, doDono.SessaoId, Ctx(OrgA)));
        Assert.Equal(new[] { maria.SessaoId, doDono.SessaoId }.Order(), todas!.Select(x => x.Id).Order());
        Assert.DoesNotContain(todas!, x => x.Id == delaB.SessaoId);
    }

    [Fact]
    public async Task Revoking_someone_elses_session_needs_an_owner_and_looks_like_not_found_otherwise()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var maria = await Entrar(env, s.Maria);
        var gerente = await Entrar(env, s.Gerente);
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");
        var delaB = await env.Sessoes.IniciarAsync(donoB, OrgB, 1, false, Cliente);

        Assert.Equal(ResultadoRevogacao.NaoEncontrada, await env.Sessoes.RevogarAsync(new FakeRequest(s.Gerente, gerente.SessaoId, Ctx(OrgA)), maria.SessaoId));
        Assert.NotNull(await env.Sessoes.AutenticarAsync(maria.AccessToken));
        Assert.Equal(ResultadoRevogacao.NaoEncontrada, await env.Sessoes.RevogarAsync(new FakeRequest(s.Gerente, gerente.SessaoId, Ctx(OrgA)), Guid.NewGuid()));

        var dono = new FakeRequest(s.Dono, Guid.NewGuid(), Ctx(OrgA));
        Assert.Equal(ResultadoRevogacao.NaoEncontrada, await env.Sessoes.RevogarAsync(dono, delaB.SessaoId)); // another organization: RLS
        Assert.NotNull(await env.Sessoes.AutenticarAsync(delaB.AccessToken));
        Assert.Equal(ResultadoRevogacao.Revogada, await env.Sessoes.RevogarAsync(dono, maria.SessaoId));
        Assert.Null(await env.Sessoes.AutenticarAsync(maria.AccessToken));
    }

    // ---- schema ---------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_session_cannot_point_at_an_account_or_unit_of_another_organization()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.InsertSessionRowAsync(OrgA, donoB, null));
        Assert.Equal("23503", ex.SqlState);
        var ex2 = await Assert.ThrowsAsync<PostgresException>(() => env.InsertSessionRowAsync(OrgA, s.Maria, UnitB));
        Assert.Equal("23503", ex2.SqlState);
    }

    [Fact]
    public async Task Sessions_of_one_organization_are_invisible_to_the_other_under_RLS()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        await Entrar(env, s.Maria);

        Assert.Equal(0L, await env.CountAsync(OrgB, "SELECT count(*) FROM sessoes"));
        Assert.Equal(0L, await env.CountAsync(OrgB, "SELECT count(*) FROM sessao_refresh"));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM sessoes"));
    }

    // ---- latency of the per-request check (ticket criterion: measured and recorded) -----------------------------------------------------------------------

    [Fact]
    public async Task The_per_request_revocation_check_latency_is_measured()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var emitida = await Entrar(env, s.Maria);
        await env.Sessoes.AutenticarAsync(emitida.AccessToken); // warm-up: connection, EF model

        var tempos = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.NotNull(await env.Sessoes.AutenticarAsync(emitida.AccessToken));
            tempos.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        tempos.Sort();
        var p50 = tempos[100];
        var p95 = tempos[190];
        Console.WriteLine($"[latencia-checagem-de-revogacao] n=200 p50={p50:F2} ms p95={p95:F2} ms max={tempos[^1]:F2} ms (PostgreSQL local em contêiner, uma transação por requisição, sem cache)");
        Assert.True(p95 < 100, $"p95 {p95:F1} ms is far above the expected few milliseconds");
    }
}
