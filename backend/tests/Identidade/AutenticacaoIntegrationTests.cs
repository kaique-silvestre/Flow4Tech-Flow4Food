using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Isolation.Tests;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.1, 5.2.2 and 5.2.8: login without enumeration and with the same work on every path, forced change of the temporary credential, rehash, audit of failures.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class AutenticacaoIntegrationTests(PostgresFixture postgres)
{
    private sealed record Setup(Guid DonoA, Guid MariaA, string CredencialMaria, Guid MariaB, string CredencialMariaB);

    private static async Task<Setup> SeedAsync(IdentidadeEnv env)
    {
        var donoA = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");
        var (mariaA, credA) = await env.CriarOperacionalAsync(donoA, OrgA, "maria");
        var (mariaB, credB) = await env.CriarOperacionalAsync(donoB, OrgB, "maria");
        return new Setup(donoA, mariaA, credA, mariaB, credB);
    }

    [Fact]
    public async Task The_right_temporary_credential_only_opens_the_change_of_password_state()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var result = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", s.CredencialMaria);

        Assert.Equal(new DeveTrocarSenha(s.MariaA, OrgA, TipoConta.Operacional, 1), result);
    }

    [Fact]
    public async Task The_same_user_name_logs_into_the_organization_asked_for_and_the_other_credential_does_not_work_here()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var inB = await env.Auth.AutenticarOperacionalAsync(OrgB, "maria", s.CredencialMariaB);
        var crossed = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", s.CredencialMariaB);

        Assert.Equal(new DeveTrocarSenha(s.MariaB, OrgB, TipoConta.Operacional, 1), inB);
        Assert.Equal(new FalhaDeAutenticacao(), crossed);
    }

    [Fact]
    public async Task An_account_with_email_logs_in_by_email_ignoring_case_and_spaces()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var result = await env.Auth.AutenticarPorEmailAsync("  DONO@a.com ", IdentidadeEnv.Senha);

        Assert.Equal(new Autenticado(s.DonoA, OrgA, TipoConta.Email, 1), result);
    }

    [Fact]
    public async Task Every_way_to_fail_is_the_same_generic_failure()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        await env.Contas.DesativarAsync(IdentidadeEnv.As(s.DonoA, OrgA), s.MariaA);
        var failure = new FalhaDeAutenticacao();

        Assert.Equal(failure, await env.Auth.AutenticarOperacionalAsync(OrgB, "maria", "senha-errada-123")); // wrong password
        Assert.Equal(failure, await env.Auth.AutenticarOperacionalAsync(OrgB, "ninguem", s.CredencialMariaB)); // unknown user
        Assert.Equal(failure, await env.Auth.AutenticarOperacionalAsync(Guid.NewGuid(), "maria", s.CredencialMariaB)); // unknown organization
        Assert.Equal(failure, await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", s.CredencialMaria)); // deactivated, right password
        Assert.Equal(failure, await env.Auth.AutenticarPorEmailAsync("dono@a.com", "senha-errada-123")); // wrong password, email
        Assert.Equal(failure, await env.Auth.AutenticarPorEmailAsync("ninguem@a.com", IdentidadeEnv.Senha)); // unknown email
        Assert.Equal(failure, await env.Auth.AutenticarPorEmailAsync("maria", IdentidadeEnv.Senha)); // a user name is not an email
        Assert.Equal(failure, await env.Auth.AutenticarOperacionalAsync(OrgB, "", "")); // empty input
        Assert.Equal(failure, await env.Auth.AutenticarOperacionalAsync(OrgB, "maria", new string('x', 100_000))); // oversized
    }

    [Fact]
    public async Task A_missing_account_costs_the_same_hashing_work_as_a_wrong_password()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        await env.Contas.DesativarAsync(IdentidadeEnv.As(s.DonoA, OrgA), s.MariaA);
        var hasher = env.Hasher;

        async Task<int> WorkOf(Func<Task> attempt)
        {
            hasher.Reset();
            await attempt();
            Assert.Equal(0, hasher.Hashes); // a login never creates a hash (no rehash needed here)
            return hasher.Work;
        }

        var wrongPassword = await WorkOf(() => env.Auth.AutenticarOperacionalAsync(OrgB, "maria", "senha-errada-123"));
        var unknownUser = await WorkOf(() => env.Auth.AutenticarOperacionalAsync(OrgB, "ninguem", "senha-errada-123"));
        var unknownOrganization = await WorkOf(() => env.Auth.AutenticarOperacionalAsync(Guid.NewGuid(), "maria", "senha-errada-123"));
        var deactivated = await WorkOf(() => env.Auth.AutenticarOperacionalAsync(OrgA, "maria", s.CredencialMaria));
        var unknownEmail = await WorkOf(() => env.Auth.AutenticarPorEmailAsync("ninguem@a.com", "senha-errada-123"));
        var wrongEmailPassword = await WorkOf(() => env.Auth.AutenticarPorEmailAsync("dono@a.com", "senha-errada-123"));
        var success = await WorkOf(() => env.Auth.AutenticarOperacionalAsync(OrgB, "maria", s.CredencialMariaB));

        Assert.Equal(1, wrongPassword);
        Assert.All([unknownUser, unknownOrganization, deactivated, unknownEmail, wrongEmailPassword, success], work => Assert.Equal(wrongPassword, work));
    }

    [Fact]
    public async Task The_dummy_path_is_the_one_used_when_there_is_no_account_and_the_real_one_otherwise()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        var hasher = env.Hasher;

        hasher.Reset();
        await env.Auth.AutenticarOperacionalAsync(OrgB, "ninguem", "senha-errada-123");
        Assert.Equal((0, 1), (hasher.Verifications, hasher.DummyVerifications));

        hasher.Reset();
        await env.Auth.AutenticarOperacionalAsync(OrgB, "maria", "senha-errada-123");
        Assert.Equal((1, 0), (hasher.Verifications, hasher.DummyVerifications));
    }

    [Fact]
    public async Task The_response_time_floor_is_applied_to_every_outcome()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        await env.Auth.AutenticarOperacionalAsync(OrgB, "maria", s.CredencialMariaB); // must change
        await env.Auth.AutenticarOperacionalAsync(OrgB, "maria", "errada-errada"); // wrong
        await env.Auth.AutenticarOperacionalAsync(OrgB, "ninguem", "errada-errada"); // unknown
        await env.Auth.AutenticarPorEmailAsync("dono@b.com", IdentidadeEnv.Senha); // success
        await env.Auth.AutenticarPorEmailAsync("ninguem@b.com", IdentidadeEnv.Senha); // unknown email

        Assert.Equal(5, env.Floor.Calls);
    }

    [Fact]
    public async Task The_temporary_credential_must_be_changed_and_stops_working_afterwards()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var changed = await env.Troca.TrocarAsync(OrgA, s.MariaA, s.CredencialMaria, "minha-senha-nova-2026");
        var withNew = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", "minha-senha-nova-2026");
        var withTemporary = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", s.CredencialMaria);

        Assert.Equal(StatusTrocaDeSenha.Trocada, changed.Status);
        Assert.Equal(new Autenticado(s.MariaA, OrgA, TipoConta.Operacional, 2), withNew); // the change bumped the credential version: ticket 04 ends the old sessions
        Assert.Equal(new FalhaDeAutenticacao(), withTemporary);
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:trocar-senha", "sucesso"));
    }

    [Fact]
    public async Task A_change_needs_the_current_password_and_a_password_that_is_neither_weak_nor_leaked()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var wrongCurrent = await env.Troca.TrocarAsync(OrgA, s.MariaA, "nao-e-a-senha-atual", "minha-senha-nova-2026");
        var weak = await env.Troca.TrocarAsync(OrgA, s.MariaA, s.CredencialMaria, "curta");
        var leaked = await env.Troca.TrocarAsync(OrgA, s.MariaA, s.CredencialMaria, "qwertyuiop");
        var other = await env.Troca.TrocarAsync(OrgB, s.MariaA, s.CredencialMaria, "minha-senha-nova-2026"); // right id, wrong organization: invisible

        Assert.Equal(StatusTrocaDeSenha.SenhaAtualInvalida, wrongCurrent.Status);
        Assert.Equal(StatusTrocaDeSenha.SenhaInvalida, weak.Status);
        Assert.Equal([ProblemaDeSenha.Curta], weak.Problemas);
        Assert.Equal(StatusTrocaDeSenha.SenhaVazada, leaked.Status);
        Assert.Equal(StatusTrocaDeSenha.SenhaAtualInvalida, other.Status);
        Assert.Equal(new DeveTrocarSenha(s.MariaA, OrgA, TipoConta.Operacional, 1), await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", s.CredencialMaria)); // untouched
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:trocar-senha", "negado"));
    }

    [Fact]
    public async Task The_new_password_cannot_be_the_temporary_credential_the_administrator_saw()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        var same = await env.Troca.TrocarAsync(OrgA, s.MariaA, s.CredencialMaria, s.CredencialMaria);

        Assert.Equal(StatusTrocaDeSenha.SenhaInvalida, same.Status);
        Assert.Equal([ProblemaDeSenha.IgualASenhaAtual], same.Problemas);
        Assert.IsType<DeveTrocarSenha>(await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", s.CredencialMaria)); // still must change
    }

    [Fact]
    public async Task A_deactivated_account_cannot_change_its_password()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        await env.Contas.DesativarAsync(IdentidadeEnv.As(s.DonoA, OrgA), s.MariaA);

        var result = await env.Troca.TrocarAsync(OrgA, s.MariaA, s.CredencialMaria, "minha-senha-nova-2026");

        Assert.Equal(StatusTrocaDeSenha.ContaInativa, result.Status);
    }

    [Fact]
    public async Task Passwords_are_unicode_normalized_so_composed_and_decomposed_text_are_the_same_password()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        await env.Troca.TrocarAsync(OrgA, s.MariaA, s.CredencialMaria, "Sénha-forte-123");

        var decomposed = await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", "Sénha-forte-123");

        Assert.IsType<Autenticado>(decomposed);
    }

    [Fact]
    public async Task A_hash_made_with_older_parameters_is_rehashed_on_the_next_successful_login_without_ending_sessions()
    {
        await using var first = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(first);
        var before = await IdentidadeEnv.QueryAsync(first.App, OrgA, "SELECT senha_hash FROM credenciais WHERE conta_id = @i", r => r.GetString(0), ("i", s.DonoA));
        Assert.Contains("m=1024,", before[0], StringComparison.Ordinal);

        await using var stronger = await IdentidadeEnv.CreateAsync(postgres, o => o.Argon2 = IdentidadeEnv.FastArgon with { MemoryKib = 2048 }, existing: first.Db);
        var result = await stronger.Auth.AutenticarPorEmailAsync("dono@a.com", IdentidadeEnv.Senha);

        Assert.Equal(new Autenticado(s.DonoA, OrgA, TipoConta.Email, 1), result); // same credential version
        var after = await IdentidadeEnv.QueryAsync(first.App, OrgA, "SELECT senha_hash FROM credenciais WHERE conta_id = @i", r => r.GetString(0), ("i", s.DonoA));
        Assert.Contains("m=2048,", after[0], StringComparison.Ordinal);
        Assert.IsType<Autenticado>(await stronger.Auth.AutenticarPorEmailAsync("dono@a.com", IdentidadeEnv.Senha));
        Assert.Equal(1, stronger.Hasher.Hashes); // rehashed once, not again
    }

    [Fact]
    public async Task A_failure_of_a_known_account_is_audited_with_the_reason_and_no_secret_and_an_unknown_one_leaves_no_trace_to_tell_them_apart()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);
        await env.Contas.DesativarAsync(IdentidadeEnv.As(s.DonoA, OrgA), s.MariaA);
        var auditBeforeA = await env.CountAsync(OrgA, "SELECT count(*) FROM auditoria_registros");
        var auditBeforeB = await env.CountAsync(OrgB, "SELECT count(*) FROM auditoria_registros");

        await env.Auth.AutenticarOperacionalAsync(OrgB, "maria", "senha-errada-123"); // wrong password, known account
        await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", s.CredencialMaria); // deactivated, right password
        await env.Auth.AutenticarOperacionalAsync(OrgB, "ninguem", "senha-errada-123"); // unknown
        await env.Auth.AutenticarOperacionalAsync(Guid.NewGuid(), "maria", "senha-errada-123"); // unknown organization

        var rows = await IdentidadeEnv.QueryAsync(env.App, OrgB, "SELECT resultado, usuario_id, objeto_tipo, objeto_id, depois->>'motivo' FROM auditoria_registros WHERE acao = 'autenticacao:falhar'",
            r => (r.GetString(0), r.IsDBNull(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        Assert.Equal(("negado", true, "conta", s.MariaB.ToString("D"), "senha-invalida"), Assert.Single(rows));
        var rowsA = await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT depois->>'motivo' FROM auditoria_registros WHERE acao = 'autenticacao:falhar'", r => r.GetString(0));
        Assert.Equal("conta-desativada", Assert.Single(rowsA));
        Assert.Equal(auditBeforeA + 1, await env.CountAsync(OrgA, "SELECT count(*) FROM auditoria_registros"));
        Assert.Equal(auditBeforeB + 1, await env.CountAsync(OrgB, "SELECT count(*) FROM auditoria_registros"));
    }

    [Fact]
    public async Task A_successful_login_attempt_is_not_a_failure_record()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var s = await SeedAsync(env);

        await env.Auth.AutenticarPorEmailAsync("dono@a.com", IdentidadeEnv.Senha);

        Assert.Equal(0L, await env.AuditCountAsync(OrgA, "autenticacao:falhar"));
    }
}
