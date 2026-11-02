using System.Net.Http;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel.Audit;
using Microsoft.Extensions.DependencyInjection;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Findings of the independent review of ticket 03: single founder, expiring temporary credential, input bound, breach-check policy, owner flag on active accounts.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class RevisaoIdentidadeIntegrationTests(PostgresFixture postgres)
{
    private const string NovaSenha = "minha-senha-nova-2026";

    // ---- 1. the founder only exists in an organization without accounts -------------------------------------------------------------------------

    [Fact]
    public async Task The_founding_owner_is_refused_when_the_organization_already_has_an_account_and_nothing_is_stored_or_reserved()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");

        var second = await env.Contas.CriarDonoAsync(OrgA, "intruso@a.com", "Intruso", IdentidadeEnv.Senha);

        Assert.Equal(new ResultadoCriacaoDono(StatusCriacao.OrganizacaoJaIniciada, null, []), second);
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
        Assert.Null(await env.Lookup.LocalizarPorEmailAsync("intruso@a.com", CancellationToken.None)); // the e-mail was not reserved
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:criar"));
    }

    [Fact]
    public async Task Any_existing_account_blocks_the_founder_not_only_an_owner()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.SeedGerenteAsync(OrgA, "gerente@a.com"); // an organization with accounts but no owner

        var founder = await env.Contas.CriarDonoAsync(OrgA, "dono@a.com", "Dono", IdentidadeEnv.Senha);

        Assert.Equal(StatusCriacao.OrganizacaoJaIniciada, founder.Status);
    }

    [Fact]
    public async Task A_deactivated_account_still_counts_so_the_founder_cannot_be_created_afterwards()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(dono, OrgA), gerente, true);
        await env.Contas.DesativarAsync(IdentidadeEnv.As(gerente, OrgA), gerente); // two owners: one may leave

        var founder = await env.Contas.CriarDonoAsync(OrgA, "novo@a.com", "Novo", IdentidadeEnv.Senha);

        Assert.Equal(StatusCriacao.OrganizacaoJaIniciada, founder.Status);
    }

    [Fact]
    public async Task Founding_an_owner_many_times_at_once_yields_exactly_one_founder()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => env.Contas.CriarDonoAsync(OrgA, $"fundador{i}@a.com", "Fundador " + i, IdentidadeEnv.Senha)));

        Assert.Equal(1, results.Count(r => r.Status == StatusCriacao.Criada));
        Assert.Equal(7, results.Count(r => r.Status == StatusCriacao.OrganizacaoJaIniciada));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE dono"));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:criar", "sucesso"));
        var winner = Assert.Single(results, r => r.Status == StatusCriacao.Criada);
        var reserved = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            if (await env.Lookup.LocalizarPorEmailAsync($"fundador{i}@a.com", CancellationToken.None) is { } found)
            {
                reserved.Add(found.ContaId);
            }
        }

        Assert.Equal([winner.ContaId!.Value], reserved); // only the winner's e-mail was reserved
    }

    [Fact]
    public async Task Two_organizations_each_get_their_own_founder()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);

        var results = await Task.WhenAll(env.Contas.CriarDonoAsync(OrgA, "dono@a.com", "A", IdentidadeEnv.Senha), env.Contas.CriarDonoAsync(OrgB, "dono@b.com", "B", IdentidadeEnv.Senha));

        Assert.All(results, r => Assert.Equal(StatusCriacao.Criada, r.Status));
    }

    // ---- 2. the temporary credential expires -----------------------------------------------------------------------------------------------------

    private static async Task<DateTimeOffset?> ExpiryAsync(IdentidadeEnv env, Guid org, Guid conta) =>
        Assert.Single(await IdentidadeEnv.QueryAsync(env.App, org, "SELECT temporaria_expira_em FROM credenciais WHERE conta_id = @i",
            r => r.IsDBNull(0) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(0), ("i", conta)));

    [Fact]
    public async Task A_temporary_credential_gets_a_deadline_of_72_hours_by_default_and_the_other_credentials_have_none()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");

        Assert.Equal(env.Clock.UtcNow.AddHours(72), await ExpiryAsync(env, OrgA, maria));
        Assert.Null(await ExpiryAsync(env, OrgA, dono));
        Assert.Null(await ExpiryAsync(env, OrgA, gerente));
    }

    [Fact]
    public async Task The_validity_is_set_by_the_host_option()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, tweak: o => o.CredencialTemporariaHoras = 5);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");

        Assert.Equal(env.Clock.UtcNow.AddHours(5), await ExpiryAsync(env, OrgA, maria));
    }

    [Fact]
    public async Task A_temporary_credential_works_until_its_deadline_and_then_fails_like_any_wrong_password()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        var created = env.Clock.UtcNow;

        env.Clock.UtcNow = created.AddHours(72).AddTicks(-1);
        var before = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", credencial);
        env.Clock.UtcNow = created.AddHours(72);
        var atDeadline = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", credencial);
        env.Clock.UtcNow = created.AddDays(30);
        var long_after = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", credencial);

        Assert.Equal(new DeveTrocarSenha(maria, OrgA, TipoConta.Operacional, 1), before);
        Assert.Equal(new FalhaDeAutenticacao(), atDeadline);
        Assert.Equal(new FalhaDeAutenticacao(), long_after);
        Assert.Equal(2L, await env.AuditCountAsync(OrgA, "autenticacao:falhar", "negado"));
    }

    [Fact]
    public async Task An_expired_credential_costs_the_same_hashing_work_as_a_wrong_password_and_as_an_unknown_user()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (_, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(73);

        env.Hasher.Reset();
        var expired = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", credencial);
        var expiredWork = env.Hasher.Work;
        env.Hasher.Reset();
        var wrong = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", "errada-errada-errada");
        var wrongWork = env.Hasher.Work;
        env.Hasher.Reset();
        var unknown = await env.Auth.AutenticarOperacionalAsync(OrgA, "ninguem", credencial);

        Assert.Equal(new FalhaDeAutenticacao(), expired);
        Assert.Equal(expired, wrong);
        Assert.Equal(expired, unknown);
        Assert.Equal(1, expiredWork);
        Assert.Equal(wrongWork, expiredWork);
        Assert.Equal(expiredWork, env.Hasher.Work);
    }

    [Fact]
    public async Task An_expired_credential_cannot_be_used_to_change_the_password_either_and_nothing_changes()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(73);

        var result = await env.Troca.TrocarAsync(OrgA, maria, credencial, NovaSenha);

        Assert.Equal(StatusTrocaDeSenha.SenhaAtualInvalida, result.Status);
        Assert.Equal(new FalhaDeAutenticacao(), await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", NovaSenha));
        Assert.Equal(0L, await env.AuditCountAsync(OrgA, "conta:trocar-senha", "sucesso"));
    }

    [Fact]
    public async Task Changing_the_password_clears_the_deadline_so_the_new_password_never_expires()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "maria");

        env.Clock.UtcNow = env.Clock.UtcNow.AddHours(10);
        var changed = await env.Troca.TrocarAsync(OrgA, maria, credencial, NovaSenha);
        env.Clock.UtcNow = env.Clock.UtcNow.AddDays(365);
        var later = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", NovaSenha);

        Assert.Equal(StatusTrocaDeSenha.Trocada, changed.Status);
        Assert.Null(await ExpiryAsync(env, OrgA, maria));
        Assert.Equal(new Autenticado(maria, OrgA, TipoConta.Operacional, 2), later);
    }

    [Fact]
    public async Task The_database_refuses_a_deadline_on_a_credential_that_is_not_temporary()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Exec(tx, "UPDATE credenciais SET temporaria_expira_em = now() WHERE conta_id = @i", ("i", dono)));
        Assert.Equal("23514", error.SqlState);
    }

    // ---- 3. input bound before NFKC + Argon2 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_giant_current_or_new_password_is_refused_before_any_normalization_hashing_or_lookup()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        var giant = new string('a', (PoliticaDeSenha.MaximoCaracteres * 4) + 1);
        env.Hasher.Reset();

        var giantCurrent = await env.Troca.TrocarAsync(OrgA, maria, giant, NovaSenha);
        var giantNew = await env.Troca.TrocarAsync(OrgA, maria, credencial, giant);

        Assert.Equal(StatusTrocaDeSenha.SenhaAtualInvalida, giantCurrent.Status);
        Assert.Equal(StatusTrocaDeSenha.SenhaInvalida, giantNew.Status);
        Assert.Equal([ProblemaDeSenha.Longa], giantNew.Problemas);
        Assert.Equal(0, env.Hasher.Work);
        Assert.IsType<DeveTrocarSenha>(await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", credencial)); // untouched
    }

    [Fact]
    public async Task The_limit_is_the_same_as_the_login_one_so_the_longest_accepted_input_still_goes_through_the_check()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        env.Hasher.Reset();

        var edge = await env.Troca.TrocarAsync(OrgA, maria, new string('a', PoliticaDeSenha.MaximoCaracteres * 4), NovaSenha);

        Assert.Equal(StatusTrocaDeSenha.SenhaAtualInvalida, edge.Status);
        Assert.Equal(1, env.Hasher.Work); // verified (wrong), not short-circuited
    }

    [Fact]
    public async Task A_giant_password_for_the_founder_is_refused_before_hashing()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        env.Hasher.Reset();

        var result = await env.Contas.CriarDonoAsync(OrgA, "dono@a.com", "Dono", new string('a', (PoliticaDeSenha.MaximoCaracteres * 4) + 1));

        Assert.Equal(StatusCriacao.SenhaInvalida, result.Status);
        Assert.Equal([ProblemaDeSenha.Longa], result.Problemas);
        Assert.Equal(0, env.Hasher.Work);
    }

    // ---- 4. HibpIndisponivelRecusa --------------------------------------------------------------------------------------------------------------

    private sealed class Down : ICompromisedPasswordCheck
    {
        public bool Up { get; set; }

        public Task<bool> IsCompromisedAsync(string password, CancellationToken cancellationToken = default) =>
            Up ? Task.FromResult(false) : Task.FromException<bool>(new HttpRequestException("fora do ar"));
    }

    private sealed class Silent : ITechnicalLog
    {
        public void Write(LogSeverity severity, string eventName, IReadOnlyDictionary<string, object?>? fields = null)
        {
        }
    }

    private static CompositeCompromisedPasswordCheck Composite(bool refuse, Down? remote = null) => new(new LocalCompromisedPasswords(), remote ?? new Down(), new Silent(), refuse);

    [Fact]
    public async Task With_the_option_on_an_unreachable_breach_service_raises_instead_of_accepting_the_password()
    {
        await Assert.ThrowsAsync<VerificacaoDeSenhaIndisponivelException>(() => Composite(refuse: true).IsCompromisedAsync("uma-senha-boa-2026"));
        Assert.False(await Composite(refuse: false).IsCompromisedAsync("uma-senha-boa-2026")); // the old behavior
        Assert.True(await Composite(refuse: true).IsCompromisedAsync("password123")); // the local list answers before the service is asked
    }

    [Fact]
    public async Task The_host_option_reaches_the_check_it_builds()
    {
        static ICompromisedPasswordCheck Build(bool refuse)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ITechnicalLog>(new Silent());
            services.AddIdentidade(o =>
            {
                o.UsarHibp = true;
                o.HibpBaseAddress = new Uri("https://127.0.0.1:1/"); // nothing listens: connection refused
                o.HibpIndisponivelRecusa = refuse;
            });
            return services.BuildServiceProvider().GetRequiredService<ICompromisedPasswordCheck>();
        }

        await Assert.ThrowsAsync<VerificacaoDeSenhaIndisponivelException>(() => Build(true).IsCompromisedAsync("uma-senha-boa-2026"));
        Assert.False(await Build(false).IsCompromisedAsync("uma-senha-boa-2026"));
    }

    [Fact]
    public async Task The_founder_is_refused_with_a_typed_result_when_the_check_is_required_and_down_and_nothing_is_stored()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, compromised: Composite(refuse: true));

        var result = await env.Contas.CriarDonoAsync(OrgA, "dono@a.com", "Dono", IdentidadeEnv.Senha);

        Assert.Equal(new ResultadoCriacaoDono(StatusCriacao.VerificacaoIndisponivel, null, []), result);
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
    }

    [Fact]
    public async Task The_founder_is_accepted_when_the_check_is_down_and_the_option_is_off()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, compromised: Composite(refuse: false));

        var result = await env.Contas.CriarDonoAsync(OrgA, "dono@a.com", "Dono", IdentidadeEnv.Senha);

        Assert.Equal(StatusCriacao.Criada, result.Status);
    }

    [Fact]
    public async Task A_password_change_is_refused_with_a_typed_result_when_the_check_is_required_and_down_and_the_old_password_stays()
    {
        var remote = new Down { Up = true };
        await using var env = await IdentidadeEnv.CreateAsync(postgres, compromised: Composite(refuse: true, remote));
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        remote.Up = false; // the breach service goes down after the account exists

        var result = await env.Troca.TrocarAsync(OrgA, maria, credencial, NovaSenha);

        Assert.Equal(StatusTrocaDeSenha.VerificacaoIndisponivel, result.Status);
        Assert.IsType<DeveTrocarSenha>(await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", credencial));
    }

    // ---- 5. the owner flag only on an active account ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_store_does_not_name_an_owner_on_an_account_that_was_deactivated_meanwhile()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        Assert.True((await env.Contas.DesativarAsync(IdentidadeEnv.As(dono, OrgA), gerente)).Permitido);

        // The race: the service read the target as active, then another unit deactivated it before this UPDATE ran.
        var written = await env.Uow.ExecuteAsync(IdentidadeEnv.Pre(OrgA), ct => env.Store.DefinirDonoAsync(OrgA, gerente, true, ct));

        Assert.False(written);
        var row = Assert.Single(await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT dono, ativa, versao_credencial FROM contas WHERE id = @i", r => (r.GetBoolean(0), r.GetBoolean(1), r.GetInt32(2)), ("i", gerente)));
        Assert.Equal((false, false, 2), row); // untouched: still not an owner, version bumped only by the deactivation
    }

    [Fact]
    public async Task Naming_a_deactivated_account_owner_is_refused_and_leaves_no_owner_flag()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        await env.Contas.DesativarAsync(IdentidadeEnv.As(dono, OrgA), gerente);

        var result = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(dono, OrgA), gerente, true);

        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.ContaDesativada), result);
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE dono"));
        Assert.Equal(0L, await env.AuditCountAsync(OrgA, "conta:definir-dono", "sucesso"));
    }
}
