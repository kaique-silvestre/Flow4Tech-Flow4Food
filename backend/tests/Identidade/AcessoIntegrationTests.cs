using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>
/// Ticket 09 against real PostgreSQL (app credential, RLS on): invitations, recovery, operational credential reset and the Google link, each through the real stores and the real unit of work.
/// Class name has "Integration": it runs in the identidade-tests job with Testcontainers.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class AcessoIntegrationTests(PostgresFixture postgres)
{
    private const string Senha = "uma-senha-bem-longa-2026";
    private const string NovaSenha = "outra-senha-bem-longa-2027";

    private static string TokenDe(IdentidadeEnv env, string assunto) =>
        env.Get<EnviadorDeEmailDeDesenvolvimento>().Enviadas.Last(m => m.Assunto.Contains(assunto, StringComparison.Ordinal)).Texto.Split("#token=")[1].Split(['\n', ' '])[0];

    private static async Task<(Guid Dono, string Token)> ConvidarAsync(IdentidadeEnv env, Guid org, string email, Guid? perfil = null)
    {
        var dono = await env.CriarDonoAsync(org, "dono@" + email.Split('@')[1]);
        var resultado = await env.Get<Convites>().ConvidarAsync(IdentidadeEnv.As(dono, org), email, "Pessoa " + email, perfil, null);
        Assert.Equal(StatusConvite.Criado, resultado.Status);
        return (dono, TokenDe(env, "convidado"));
    }

    // ---- invitations ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_accepted_invitation_creates_an_account_that_can_log_in_and_the_token_is_stored_only_as_a_hash()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var (_, token) = await ConvidarAsync(env, OrgA, "nova@a.com");

        var aceito = await env.Get<Convites>().AceitarAsync(token, Senha);

        Assert.Equal(StatusAceite.Aceito, aceito.Status);
        var login = await env.Auth.AutenticarPorEmailAsync("nova@a.com", Senha);
        Assert.Equal(aceito.ContaId, Assert.IsType<Autenticado>(login).ContaId);
        Assert.NotNull(await env.Lookup.LocalizarPorEmailAsync("nova@a.com", default)); // the address was reserved on the platform only now
        Assert.Empty(await env.TablesContainingAsync(token)); // no table holds the token, in any form of text
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM outbox_eventos WHERE tipo = 'identidade.convite-aceito'"));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, IdentidadeAuditActions.ConviteAceitar));
    }

    [Fact]
    public async Task Eight_simultaneous_acceptances_of_one_invitation_create_exactly_one_account()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var (_, token) = await ConvidarAsync(env, OrgA, "nova@a.com");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => env.Get<Convites>().AceitarAsync(token, Senha)));

        Assert.Single(resultados, r => r.Status == StatusAceite.Aceito);
        Assert.All(resultados.Where(r => r.Status != StatusAceite.Aceito), r => Assert.Equal(StatusAceite.TokenInvalido, r.Status));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE email = 'nova@a.com'"));
    }

    [Fact]
    public async Task An_expired_invitation_and_one_whose_organization_locator_was_swapped_find_nothing()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var (_, token) = await ConvidarAsync(env, OrgA, "nova@a.com");
        var trocado = token.Replace(OrgA.ToString("N"), OrgB.ToString("N"), StringComparison.Ordinal);

        Assert.Equal(StatusAceite.TokenInvalido, (await env.Get<Convites>().AceitarAsync(trocado, Senha)).Status);
        env.Clock.UtcNow += TimeSpan.FromDays(8);
        Assert.Equal(StatusAceite.TokenInvalido, (await env.Get<Convites>().AceitarAsync(token, Senha)).Status);
        Assert.Equal(0L, await env.CountAsync(OrgB, "SELECT count(*) FROM convites"));
    }

    [Fact]
    public async Task A_new_invitation_revokes_the_old_one_in_the_database()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var (dono, primeiro) = await ConvidarAsync(env, OrgA, "nova@a.com");
        await env.Get<Convites>().ConvidarAsync(IdentidadeEnv.As(dono, OrgA), "nova@a.com", "Nova", null, null);

        Assert.Equal(StatusAceite.TokenInvalido, (await env.Get<Convites>().AceitarAsync(primeiro, Senha)).Status);
        Assert.Equal(StatusAceite.Aceito, (await env.Get<Convites>().AceitarAsync(TokenDe(env, "convidado"), Senha)).Status);
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM convites WHERE revogado_em IS NOT NULL"));
    }

    [Fact]
    public async Task A_profile_of_another_organization_is_refused_by_the_composite_foreign_key()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var perfilDeB = Guid.NewGuid();
        await using (var tx = await env.Tenant.BeginAsync(Ctx(OrgB)))
        {
            await SessoesEndpointsExec(tx, perfilDeB);
            await tx.CommitAsync();
        }

        var resultado = await env.Get<Convites>().ConvidarAsync(IdentidadeEnv.As(dono, OrgA), "nova@a.com", "Nova", perfilDeB, null);

        Assert.Equal(StatusConvite.PerfilOuUnidadeInvalidos, resultado.Status);
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM convites"));
    }

    private static async Task SessoesEndpointsExec(Flow4Tech.Infrastructure.Data.TenantTransaction tx, Guid perfil)
    {
        await using var command = new NpgsqlCommand("INSERT INTO perfis (id, organizacao_id, nome, criado_em) VALUES (@i, @o, 'Perfil de B', now())", tx.Connection, tx.Transaction);
        command.Parameters.AddWithValue("i", perfil);
        command.Parameters.AddWithValue("o", OrgB);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_address_already_taken_in_another_organization_is_not_revealed_to_the_inviter_only_to_the_holder_of_the_token()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgB, "compartilhado@b.com");
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        var convite = await env.Get<Convites>().ConvidarAsync(IdentidadeEnv.As(dono, OrgA), "compartilhado@b.com", "Alguem", null, null);
        var aceite = await env.Get<Convites>().AceitarAsync(TokenDe(env, "convidado"), Senha);

        Assert.Equal(StatusConvite.Criado, convite.Status); // same answer as for a free address
        Assert.Equal(StatusAceite.EmailIndisponivel, aceite.Status);
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE email = 'compartilhado@b.com'")); // rolled back with the failed unit
    }

    [Fact]
    public async Task Someone_who_must_still_change_the_temporary_password_cannot_invite()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (operacional, _) = await env.CriarOperacionalAsync(dono, OrgA, "joao");

        var resultado = await env.Get<Convites>().ConvidarAsync(IdentidadeEnv.As(operacional, OrgA), "nova@a.com", "Nova", null, null);

        Assert.Equal(StatusConvite.Negado, resultado.Status);
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, IdentidadeAuditActions.ConviteCriar, "negado"));
    }

    // ---- recovery ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recovery_replaces_the_password_ends_every_session_and_tells_the_person_and_the_owner()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        var s1 = await env.Sessoes.IniciarAsync(gerente, OrgA, 1, false, new ClienteDaSessao("203.0.113.1", "Chrome"));
        var s2 = await env.Sessoes.IniciarAsync(gerente, OrgA, 1, false, new ClienteDaSessao("203.0.113.2", "Firefox"));

        await env.Get<RecuperacaoDeAcesso>().SolicitarAsync("gerente@a.com", ContextoDeLogin.Nenhum);
        var token = TokenDe(env, "Recupera");
        var resultado = await env.Get<RecuperacaoDeAcesso>().RedefinirAsync(token, NovaSenha);

        Assert.Equal(StatusRecuperacao.Redefinida, resultado.Status);
        Assert.Null(await env.Sessoes.AutenticarAsync(s1.AccessToken));
        Assert.Null(await env.Sessoes.AutenticarAsync(s2.AccessToken));
        Assert.IsType<Autenticado>(await env.Auth.AutenticarPorEmailAsync("gerente@a.com", NovaSenha));
        Assert.IsType<FalhaDeAutenticacao>(await env.Auth.AutenticarPorEmailAsync("gerente@a.com", IdentidadeEnv.Senha));
        Assert.Empty(await env.TablesContainingAsync(token));

        var dispatcher = new OutboxDispatcher(env.Tenant, env.Get<IEnumerable<IOutboxConsumer>>(), env.Clock);
        await dispatcher.DispatchAsync(IdentidadeEnv.Pre(OrgA));
        var enviadas = env.Get<EnviadorDeEmailDeDesenvolvimento>().Enviadas.Where(m => m.Assunto == "Acesso à sua conta recuperado").Select(m => m.Para).Order().ToArray();
        Assert.Equal(["dono@a.com", "gerente@a.com"], enviadas);
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, IdentidadeAuditActions.ContaRecuperarAcesso));
        _ = dono;
    }

    [Fact]
    public async Task Of_two_simultaneous_redemptions_exactly_one_wins()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        await env.Get<RecuperacaoDeAcesso>().SolicitarAsync("dono@a.com", ContextoDeLogin.Nenhum);
        var token = TokenDe(env, "Recupera");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => env.Get<RecuperacaoDeAcesso>().RedefinirAsync(token, $"senha-{i}-bem-longa-2027")));

        Assert.Single(resultados, r => r.Status == StatusRecuperacao.Redefinida);
        Assert.All(resultados.Where(r => r.Status != StatusRecuperacao.Redefinida), r => Assert.Equal(StatusRecuperacao.TokenInvalido, r.Status));
    }

    [Fact]
    public async Task Asking_for_recovery_of_a_known_and_of_an_unknown_address_looks_the_same_from_outside()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        var servico = env.Get<RecuperacaoDeAcesso>();

        await servico.SolicitarAsync("dono@a.com", ContextoDeLogin.Nenhum);
        await servico.SolicitarAsync("ninguem@a.com", ContextoDeLogin.Nenhum);

        Assert.Equal(2, env.Floor.Calls); // the same floor for both
        Assert.Single(env.Get<EnviadorDeEmailDeDesenvolvimento>().Enviadas, m => m.Assunto.Contains("Recupera", StringComparison.Ordinal));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM recuperacoes"));
    }

    [Fact]
    public async Task A_recovery_token_expires_and_the_expired_one_changes_nothing()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        await env.Get<RecuperacaoDeAcesso>().SolicitarAsync("dono@a.com", ContextoDeLogin.Nenhum);
        var token = TokenDe(env, "Recupera");
        env.Clock.UtcNow += TimeSpan.FromMinutes(61);

        Assert.Equal(StatusRecuperacao.TokenInvalido, (await env.Get<RecuperacaoDeAcesso>().RedefinirAsync(token, NovaSenha)).Status);
        Assert.IsType<Autenticado>(await env.Auth.AutenticarPorEmailAsync("dono@a.com", IdentidadeEnv.Senha));
    }

    // ---- operational credential reset -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resetting_an_operational_account_gives_a_new_credential_ends_its_sessions_and_brings_back_an_expired_one()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (garcom, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "joao");
        env.Clock.UtcNow += TimeSpan.FromHours(100); // the first credential expired
        Assert.IsType<FalhaDeAutenticacao>(await env.Auth.AutenticarOperacionalAsync(OrgA, "joao", credencial));
        var sessao = await env.Sessoes.IniciarAsync(garcom, OrgA, 1, false, new ClienteDaSessao("203.0.113.1", "Chrome"));

        var reset = await env.Contas.RedefinirAcessoAsync(IdentidadeEnv.As(dono, OrgA), garcom);

        Assert.Equal(StatusRedefinicao.Redefinida, reset.Status);
        Assert.IsType<DeveTrocarSenha>(await env.Auth.AutenticarOperacionalAsync(OrgA, "joao", reset.Credencial!.Valor));
        Assert.IsType<FalhaDeAutenticacao>(await env.Auth.AutenticarOperacionalAsync(OrgA, "joao", credencial));
        Assert.Null(await env.Sessoes.AutenticarAsync(sessao.AccessToken));
        Assert.Empty(await env.TablesContainingAsync(reset.Credencial.Valor));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, IdentidadeAuditActions.ContaRedefinirAcesso, "sucesso"));
    }

    [Fact]
    public async Task The_reset_never_touches_an_account_of_another_organization()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var donoA = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");
        var (garcomB, _) = await env.CriarOperacionalAsync(donoB, OrgB, "joao");

        var reset = await env.Contas.RedefinirAcessoAsync(IdentidadeEnv.As(donoA, OrgA), garcomB);

        Assert.Equal(StatusRedefinicao.NaoEncontrada, reset.Status);
    }

    // ---- Google ----------------------------------------------------------------------------------------------------------------------------

    private static Task<IdentidadeEnv> GoogleEnvAsync(PostgresFixture postgres, FakeProvedorOidc provedor) =>
        IdentidadeEnv.CreateAsync(postgres, configure: services => services.Replace(ServiceDescriptor.Singleton<IProvedorOidc>(provedor)));

    private static (string State, string Nonce) Parametros(InicioGoogle inicio)
    {
        var query = System.Web.HttpUtility.ParseQueryString(inicio.Url.Query);
        return (query["state"]!, query["nonce"]!);
    }

    [Fact]
    public async Task Linking_then_signing_in_with_google_works_end_to_end_and_an_unlinked_account_stays_out()
    {
        var provedor = new FakeProvedorOidc { Identidade = new IdentidadeOidc("sub-gerente", "gerente@a.com", true) };
        await using var env = await GoogleEnvAsync(postgres, provedor);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        var google = env.Get<ContasGoogle>();

        // not linked yet: neutral
        var antes = google.IniciarEntrada()!;
        Assert.IsType<GoogleNeutro>(await google.ConcluirAsync(antes.Selo, Parametros(antes).State, "bom", ContextoDeLogin.Nenhum));

        // link with the password
        var vinculo = await google.IniciarVinculoAsync(IdentidadeEnv.As(gerente, OrgA), IdentidadeEnv.Senha);
        Assert.Equal(StatusInicioVinculo.Iniciado, vinculo.Status);
        Assert.IsType<GoogleVinculado>(await google.ConcluirAsync(vinculo.Inicio!.Selo, Parametros(vinculo.Inicio).State, "bom", ContextoDeLogin.Nenhum));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM identidades_externas"));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, IdentidadeAuditActions.ContaVincularGoogle));

        // now it signs in
        var entrada = google.IniciarEntrada()!;
        var entrou = Assert.IsType<GoogleEntrou>(await google.ConcluirAsync(entrada.Selo, Parametros(entrada).State, "bom", ContextoDeLogin.Nenhum));
        Assert.Equal(gerente, entrou.Conta.ContaId);

        // and never created anything
        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
    }

    [Fact]
    public async Task The_database_refuses_a_google_link_for_an_operational_account()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (garcom, _) = await env.CriarOperacionalAsync(dono, OrgA, "joao");

        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        await using var command = new NpgsqlCommand(
            "INSERT INTO identidades_externas (organizacao_id, conta_id, provedor, sub, email, tipo_conta, criada_em) VALUES (@o, @c, 'google', 'sub-x', 'x@a.com', 'email', now())", tx.Connection, tx.Transaction);
        command.Parameters.AddWithValue("o", OrgA);
        command.Parameters.AddWithValue("c", garcom);
        var erro = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("23503", erro.SqlState); // composite FK (organization, account, type): the type of an operational account is not 'email'
    }

    [Fact]
    public async Task One_google_identity_links_to_one_account_across_organizations()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var a = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var b = await env.CriarDonoAsync(OrgB, "dono@b.com");
        var vinculos = env.Get<IIdentidadesExternasStore>();

        var primeiro = await env.Uow.ExecuteAsync(IdentidadeEnv.Pre(OrgA), ct => vinculos.InserirAsync(OrgA, a, "google", "sub-comum", "dono@a.com", env.Clock.UtcNow, ct));
        var segundo = await env.Uow.ExecuteAsync(IdentidadeEnv.Pre(OrgB), ct => vinculos.InserirAsync(OrgB, b, "google", "sub-comum", "dono@b.com", env.Clock.UtcNow, ct));

        Assert.True(primeiro);
        Assert.False(segundo);
        Assert.Equal(0L, await env.CountAsync(OrgB, "SELECT count(*) FROM identidades_externas"));
    }

    [Fact]
    public async Task The_three_new_tables_are_invisible_to_another_organization_and_without_context()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await ConvidarAsync(env, OrgA, "nova@a.com");
        await env.Get<RecuperacaoDeAcesso>().SolicitarAsync("dono@a.com", ContextoDeLogin.Nenhum);

        foreach (var tabela in new[] { "convites", "recuperacoes", "identidades_externas" })
        {
            Assert.Equal(0L, await env.CountAsync(OrgB, $"SELECT count(*) FROM {tabela}"));
        }

        await using var connection = await env.App.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM convites) + (SELECT count(*) FROM recuperacoes) + (SELECT count(*) FROM identidades_externas)", connection);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }
}
