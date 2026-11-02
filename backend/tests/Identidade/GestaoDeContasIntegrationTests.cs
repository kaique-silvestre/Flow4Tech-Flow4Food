using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Isolation.Tests;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.1 and 5.2.7: creating accounts, the temporary credential shown once, the protection rules, history kept, everything audited in the same unit of work.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class GestaoDeContasIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_founding_owner_gets_an_account_with_a_normalized_email_the_owner_flag_and_a_password_that_needs_no_change()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);

        var id = await env.CriarDonoAsync(OrgA, "  Dono@A.com ");

        var rows = await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT c.tipo, c.email, c.dono, c.ativa, c.versao_credencial, k.deve_trocar_senha FROM contas c JOIN credenciais k ON k.conta_id = c.id WHERE c.id = @i",
            r => (r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetInt32(4), r.GetBoolean(5)), ("i", id));
        Assert.Equal(("email", "dono@a.com", true, true, 1, false), Assert.Single(rows));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:criar", "sucesso"));
    }

    [Fact]
    public async Task Founding_an_owner_refuses_a_weak_leaked_or_malformed_input_and_stores_nothing()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);

        var curta = await env.Contas.CriarDonoAsync(OrgA, "a@a.com", "Dono", "curta");
        var vazada = await env.Contas.CriarDonoAsync(OrgA, "b@a.com", "Dono", "password123");
        var email = await env.Contas.CriarDonoAsync(OrgA, "nao-e-email", "Dono", IdentidadeEnv.Senha);
        var igual = await env.Contas.CriarDonoAsync(OrgA, "dono-da-casa@a.com", "Dono", "dono-da-casa@a.com");

        Assert.Equal(StatusCriacao.SenhaInvalida, curta.Status);
        Assert.Equal([ProblemaDeSenha.Curta], curta.Problemas);
        Assert.Equal(StatusCriacao.SenhaVazada, vazada.Status);
        Assert.Equal(StatusCriacao.IdentificadorInvalido, email.Status);
        Assert.Equal(StatusCriacao.SenhaInvalida, igual.Status);
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
        Assert.Equal(0L, await env.AuditCountAsync(OrgA, "conta:criar"));
    }

    [Fact]
    public async Task An_email_belongs_to_one_account_in_one_organization_for_the_whole_platform()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");

        var sameOrg = await env.Contas.CriarDonoAsync(OrgA, "DONO@a.com", "Outro", IdentidadeEnv.Senha);
        var otherOrg = await env.Contas.CriarDonoAsync(OrgB, "dono@a.com", "Outro", IdentidadeEnv.Senha);

        Assert.Equal(StatusCriacao.Indisponivel, sameOrg.Status);
        Assert.Equal(StatusCriacao.Indisponivel, otherOrg.Status);
        Assert.Equal(0L, await env.CountAsync(OrgB, "SELECT count(*) FROM contas")); // the refused attempt rolled back completely
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
    }

    [Fact]
    public async Task An_operational_account_gets_a_temporary_credential_that_is_returned_once_and_stored_only_as_a_hash()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        var (id, credencial) = await env.CriarOperacionalAsync(dono, OrgA, " Maria.S ");

        var row = Assert.Single(await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT c.tipo, c.usuario, c.email, c.dono, k.deve_trocar_senha, k.senha_hash FROM contas c JOIN credenciais k ON k.conta_id = c.id WHERE c.id = @i",
            r => (r.GetString(0), r.GetString(1), r.IsDBNull(2), r.GetBoolean(3), r.GetBoolean(4), r.GetString(5)), ("i", id)));
        Assert.Equal(("operacional", "maria.s", true, false, true), (row.Item1, row.Item2, row.Item3, row.Item4, row.Item5));
        Assert.True(await env.Hasher.VerifyAsync(credencial, row.Item6)); // the hash is of the credential that was shown
        Assert.NotEqual(credencial, row.Item6);

        // Nothing anywhere stores it: not a table row, not the audit trail.
        Assert.Empty(await env.TablesContainingAsync(credencial));
    }

    [Fact]
    public async Task Each_operational_account_gets_a_different_credential_and_none_can_be_read_back_later()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        var a = await env.CriarOperacionalAsync(dono, OrgA, "ana");
        var b = await env.CriarOperacionalAsync(dono, OrgA, "bia");

        Assert.NotEqual(a.Credencial, b.Credencial);
        // There is no read API for the credential: the account store never returns it.
        var account = await env.Uow.ExecuteAsync(IdentidadeEnv.Pre(OrgA), ct => env.Store.ObterAsync(OrgA, a.Id, ct));
        Assert.DoesNotContain(a.Credencial, account!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_name_is_unique_inside_the_organization_but_reusable_in_another()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var donoA = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");
        await env.CriarOperacionalAsync(donoA, OrgA, "maria");

        var again = await env.Contas.CriarOperacionalAsync(IdentidadeEnv.As(donoA, OrgA), "MARIA", "Outra Maria");
        var other = await env.Contas.CriarOperacionalAsync(IdentidadeEnv.As(donoB, OrgB), "maria", "Maria da B");
        var invalid = await env.Contas.CriarOperacionalAsync(IdentidadeEnv.As(donoA, OrgA), "a b", "Invalida");

        Assert.Equal(StatusCriacao.Indisponivel, again.Status);
        Assert.Null(again.Credencial);
        Assert.Equal(StatusCriacao.Criada, other.Status);
        Assert.Equal(StatusCriacao.IdentificadorInvalido, invalid.Status);
        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
    }

    [Fact]
    public async Task Creating_an_account_is_audited_in_the_same_unit_of_work_so_a_failing_audit_leaves_no_account()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, decorateAudit: inner => new FailingAudit(inner, "conta:criar"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Contas.CriarDonoAsync(OrgA, "dono@a.com", "Dono", IdentidadeEnv.Senha));

        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM credenciais"));
        Assert.Null(await env.Lookup.LocalizarPorEmailAsync("dono@a.com", CancellationToken.None)); // the global email reservation rolled back too
    }

    [Fact]
    public async Task Deactivating_an_account_marks_it_bumps_the_credential_version_and_erases_nothing()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        env.Clock.UtcNow = new DateTimeOffset(2026, 11, 3, 8, 30, 0, TimeSpan.Zero);

        var result = await env.Contas.DesativarAsync(IdentidadeEnv.As(dono, OrgA), maria);

        Assert.True(result.Permitido);
        var row = Assert.Single(await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT ativa, desativada_em, versao_credencial FROM contas WHERE id = @i",
            r => (r.GetBoolean(0), r.GetFieldValue<DateTimeOffset>(1), r.GetInt32(2)), ("i", maria)));
        Assert.Equal((false, env.Clock.UtcNow, 2), row);
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM credenciais WHERE conta_id = @i", ("i", maria))); // history and credential row kept
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:desativar", "sucesso"));
        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT count(*) FROM auditoria_registros WHERE objeto_id = @i", ("i", maria.ToString("D")))); // the creation record is still there beside the deactivation
    }

    [Fact]
    public async Task Deactivating_twice_or_an_unknown_account_is_refused()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        await env.Contas.DesativarAsync(IdentidadeEnv.As(dono, OrgA), maria);

        var twice = await env.Contas.DesativarAsync(IdentidadeEnv.As(dono, OrgA), maria);
        var unknown = await env.Contas.DesativarAsync(IdentidadeEnv.As(dono, OrgA), Guid.NewGuid());

        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.ContaDesativada), twice);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.ContaInexistente), unknown);
    }

    [Fact]
    public async Task An_owner_is_not_deactivated_by_another_owner_and_the_refusal_is_audited_as_denied()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono1 = await env.CriarDonoAsync(OrgA, "dono1@a.com");
        var dono2 = await env.CriarDonoAsync(OrgA, "dono2@a.com");

        var result = await env.Contas.DesativarAsync(IdentidadeEnv.As(dono1, OrgA), dono2);

        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.DonoNaoPodeSerDesativadoPorOutro), result);
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE id = @i AND ativa AND versao_credencial = 1", ("i", dono2)));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:desativar", "negado"));
        Assert.Equal(0L, await env.AuditCountAsync(OrgA, "conta:desativar", "sucesso"));
    }

    [Fact]
    public async Task There_is_always_at_least_one_owner_the_last_one_cannot_leave()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono1 = await env.CriarDonoAsync(OrgA, "dono1@a.com");
        var dono2 = await env.CriarDonoAsync(OrgA, "dono2@a.com");

        var first = await env.Contas.DesativarAsync(IdentidadeEnv.As(dono1, OrgA), dono1); // one of two may leave
        var second = await env.Contas.DesativarAsync(IdentidadeEnv.As(dono2, OrgA), dono2); // the last one may not

        Assert.True(first.Permitido);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.UltimoDono), second);
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE dono AND ativa"));
    }

    [Fact]
    public async Task Two_owners_leaving_at_the_same_time_never_leave_the_organization_without_one()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono1 = await env.CriarDonoAsync(OrgA, "dono1@a.com");
        var dono2 = await env.CriarDonoAsync(OrgA, "dono2@a.com");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => i % 2 == 0
                ? env.Contas.DesativarAsync(IdentidadeEnv.As(dono1, OrgA), dono1)
                : env.Contas.DesativarAsync(IdentidadeEnv.As(dono2, OrgA), dono2)));

        Assert.Equal(1, results.Count(r => r.Permitido));
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE dono AND ativa"));
    }

    [Fact]
    public async Task Nobody_changes_their_own_permissions_and_only_an_owner_names_another_owner()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        var gerente2 = await env.SeedGerenteAsync(OrgA, "gerente2@a.com");

        var self = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(gerente, OrgA), gerente, true);
        var notOwner = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(gerente, OrgA), gerente2, true);
        var ownerSelf = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(dono, OrgA), dono, false);

        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.AutoAlteracaoDePermissao), self);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.ApenasDonoAlteraDono), notOwner);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.AutoAlteracaoDePermissao), ownerSelf);
        Assert.Equal(1L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE dono"));
        Assert.Equal(3L, await env.AuditCountAsync(OrgA, "conta:definir-dono", "negado"));
    }

    [Fact]
    public async Task An_owner_can_name_another_owner_who_is_then_never_demoted_by_anyone_else()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");

        var promoted = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(dono, OrgA), gerente, true);
        var demoted = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(dono, OrgA), gerente, false);
        var demotedBack = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(gerente, OrgA), dono, false);

        Assert.True(promoted.Permitido);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.DonoNaoPodeSerRebaixado), demoted);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.DonoNaoPodeSerRebaixado), demotedBack);
        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE dono AND ativa"));
        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT versao_credencial FROM contas WHERE id = @i", ("i", gerente))); // the permission change ends the account's sessions
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:definir-dono", "sucesso"));
    }

    [Fact]
    public async Task An_operational_account_cannot_become_an_owner()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");

        var result = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(dono, OrgA), maria, true);

        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.DonoPrecisaDeEmail), result);
    }

    [Fact]
    public async Task An_owner_of_one_organization_cannot_touch_an_account_of_another()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var donoA = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");
        var (mariaB, _) = await env.CriarOperacionalAsync(donoB, OrgB, "maria");

        var deactivate = await env.Contas.DesativarAsync(IdentidadeEnv.As(donoA, OrgA), mariaB);
        var promote = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(donoA, OrgA), donoB, false);

        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.ContaInexistente), deactivate);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.ContaInexistente), promote);
        Assert.Equal(2L, await env.CountAsync(OrgB, "SELECT count(*) FROM contas WHERE ativa"));
    }

    [Fact]
    public async Task An_inactive_actor_cannot_deactivate_anyone()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var dono2 = await env.CriarDonoAsync(OrgA, "dono2@a.com");
        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        await env.Contas.DesativarAsync(IdentidadeEnv.As(dono2, OrgA), dono2); // dono2 leaves (two owners)

        var result = await env.Contas.DesativarAsync(IdentidadeEnv.As(dono2, OrgA), maria);

        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.AtorDesativado), result);
    }

    [Fact]
    public async Task An_account_that_must_still_change_its_password_can_do_nothing_else_until_it_does()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        var (joao, _) = await env.CriarOperacionalAsync(dono, OrgA, "joao");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");

        var create = await env.Contas.CriarOperacionalAsync(IdentidadeEnv.As(maria, OrgA), "novato", "Novato");
        var deactivate = await env.Contas.DesativarAsync(IdentidadeEnv.As(maria, OrgA), joao);
        var promote = await env.Contas.DefinirDonoAsync(IdentidadeEnv.As(maria, OrgA), gerente, true);

        Assert.Equal(StatusCriacao.Negada, create.Status);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.AtorDeveTrocarSenha), deactivate);
        Assert.Equal(new ResultadoProtecao(false, RecusaDeProtecao.AtorDeveTrocarSenha), promote);
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE usuario = 'novato'"));

        await env.Troca.TrocarAsync(OrgA, maria, credencial, "minha-senha-nova-2026");

        Assert.True((await env.Contas.DesativarAsync(IdentidadeEnv.As(maria, OrgA), joao)).Permitido); // the guard lifts with the change
    }

    [Fact]
    public async Task Creating_the_same_user_name_many_times_at_once_yields_exactly_one_account()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => env.Contas.CriarOperacionalAsync(IdentidadeEnv.As(dono, OrgA), "maria", "Maria")));

        Assert.Equal(1, results.Count(r => r.Status == StatusCriacao.Criada));
        Assert.Equal(7, results.Count(r => r.Status == StatusCriacao.Indisponivel));
        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
        Assert.Equal(1L, await env.AuditCountAsync(OrgA, "conta:criar", "sucesso") - 1); // the owner's own creation is the other record
    }

    [Fact]
    public async Task The_database_itself_refuses_a_user_name_or_email_that_is_not_in_the_canonical_lower_case()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);

        foreach (var sql in new[]
        {
            "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (gen_random_uuid(), @o, 'operacional', 'x', 'Maria', now())",
            "INSERT INTO contas (id, organizacao_id, tipo, nome, email, criada_em) VALUES (gen_random_uuid(), @o, 'email', 'x', 'Dono@A.com', now())",
        })
        {
            await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Exec(tx, sql, ("o", OrgA)));
            Assert.Equal("23514", error.SqlState);
        }
    }
}
