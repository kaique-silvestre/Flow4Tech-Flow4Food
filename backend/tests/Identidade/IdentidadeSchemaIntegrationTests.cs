using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.4 and 6 ("duas organizacoes"): the identity tables sit under RLS, the one global table is unreachable by the application role, and the pre-authentication functions are the only door.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class IdentidadeSchemaIntegrationTests(PostgresFixture postgres)
{
    private const string TriggerFunction = "outbox_registrar_pendente";

    [Fact]
    public async Task The_identity_model_has_no_drift_against_the_migrated_database()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        await using var context = new IdentidadeDbContext(tx);

        Assert.Empty(await SchemaDrift.FindAsync(context));
    }

    [Fact]
    public async Task An_application_session_sees_only_its_own_organizations_accounts_and_credentials()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var donoA = await env.CriarDonoAsync(OrgA, "dono@a.com");
        await env.CriarDonoAsync(OrgB, "dono@b.com");
        await env.CriarOperacionalAsync(donoA, OrgA, "maria");

        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas"));
        Assert.Equal(2L, await env.CountAsync(OrgA, "SELECT count(*) FROM credenciais"));
        Assert.Equal(1L, await env.CountAsync(OrgB, "SELECT count(*) FROM contas"));
        // Asking for the other organization's rows by its key finds nothing: RLS, not an application filter.
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM contas WHERE organizacao_id = @o", ("o", OrgB)));
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM credenciais WHERE organizacao_id = @o", ("o", OrgB)));
    }

    [Fact]
    public async Task Without_an_organization_context_the_application_reads_no_account_and_cannot_write_one()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");

        await using var connection = await env.App.OpenConnectionAsync();
        await using var count = new NpgsqlCommand("SELECT (SELECT count(*) FROM contas) + (SELECT count(*) FROM credenciais)", connection);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);

        await using var insert = new NpgsqlCommand(
            "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (gen_random_uuid(), @o, 'operacional', 'x', 'invasor', now())", connection);
        insert.Parameters.AddWithValue("o", OrgA);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task A_tenant_cannot_write_an_account_for_another_organization()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);

        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        var error = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            tx,
            "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (gen_random_uuid(), @o, 'operacional', 'x', 'invasor', now())",
            ("o", OrgB)));
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task The_same_operational_user_name_coexists_in_two_organizations_but_not_twice_in_one()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var donoA = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");

        await env.CriarOperacionalAsync(donoA, OrgA, "maria");
        await env.CriarOperacionalAsync(donoB, OrgB, "maria");

        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        var error = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            tx,
            "INSERT INTO contas (id, organizacao_id, tipo, nome, usuario, criada_em) VALUES (gen_random_uuid(), @o, 'operacional', 'x', 'maria', now())",
            ("o", OrgA)));
        Assert.Equal("23505", error.SqlState);
    }

    [Fact]
    public async Task A_credential_cannot_point_at_an_account_of_another_organization()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");

        // The composite foreign key (organizacao_id, conta_id) is checked below RLS: B's account id with A's organization id does not exist.
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        var error = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            tx,
            "INSERT INTO credenciais (organizacao_id, conta_id, senha_hash, deve_trocar_senha, atualizada_em) VALUES (@o, @c, 'x', false, now())",
            ("o", OrgA), ("c", donoB)));
        Assert.Equal("23503", error.SqlState);
    }

    [Fact]
    public async Task A_user_unit_link_needs_a_real_account_of_the_same_organization()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");

        await using (var tx = await env.Tenant.BeginAsync(Ctx(OrgA)))
        {
            var unknown = await Assert.ThrowsAsync<PostgresException>(() => Exec(
                tx,
                "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)",
                ("o", OrgA), ("u", Guid.NewGuid()), ("n", UnitA)));
            Assert.Equal("23503", unknown.SqlState);
        }

        await using (var tx = await env.Tenant.BeginAsync(Ctx(OrgA)))
        {
            var foreign = await Assert.ThrowsAsync<PostgresException>(() => Exec(
                tx,
                "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @u, @n)",
                ("o", OrgA), ("u", donoB), ("n", UnitA)));
            Assert.Equal("23503", foreign.SqlState);
        }
    }

    [Fact]
    public async Task The_global_email_table_is_unreachable_for_the_application_role()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");

        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
        {
            await using var connection = await env.App.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT has_table_privilege('flow4tech_app', 'identidade_emails', @p)", connection);
            command.Parameters.AddWithValue("p", privilege);
            Assert.False((bool)(await command.ExecuteScalarAsync())!, privilege);
        }

        await using var read = await env.App.OpenConnectionAsync();
        await using var select = new NpgsqlCommand("SELECT email FROM identidade_emails", read);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
        Assert.Equal("42501", denied.SqlState);
    }

    [Fact]
    public async Task The_email_registration_function_only_works_for_an_account_of_the_callers_own_organization()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");

        // Without a context: refused.
        await using (var connection = await env.App.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("SELECT identidade_registrar_email(@c, 'invasor@x.com')", connection))
        {
            command.Parameters.AddWithValue("c", donoB);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        }

        // With organization A's context, B's account is invisible: refused, and nothing was registered.
        await using (var tx = await env.Tenant.BeginAsync(Ctx(OrgA)))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT identidade_registrar_email(@c, 'invasor@x.com')", tx.Connection, tx.Transaction);
                command.Parameters.AddWithValue("c", donoB);
                await command.ExecuteScalarAsync();
            });
            Assert.Equal("42501", error.SqlState);
        }

        Assert.Null(await env.Lookup.LocalizarPorEmailAsync("invasor@x.com", CancellationToken.None));
    }

    [Fact]
    public async Task The_only_security_definer_functions_are_the_three_identity_ones_and_the_three_outbox_registry_ones_with_a_fixed_search_path_and_no_public_execute()
    {
        var db = await postgres.CreateDatabaseAsync();
        await using var owner = NpgsqlDataSource.Create(db.OwnerConnectionString);
        await using var connection = await owner.OpenConnectionAsync();

        var functions = new List<(string Name, string[]? Config, bool PublicExecute, bool AppExecute, bool OwnerSuper, bool OwnerBypass)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT p.proname, p.proconfig,
                   EXISTS (SELECT 1 FROM aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a WHERE a.grantee = 0 AND a.privilege_type = 'EXECUTE'),
                   has_function_privilege('flow4tech_app', p.oid, 'EXECUTE'),
                   r.rolsuper, r.rolbypassrls
              FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
             WHERE p.pronamespace = 'public'::regnamespace AND p.prosecdef
             ORDER BY p.proname
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                functions.Add((reader.GetString(0), await reader.IsDBNullAsync(1) ? null : await reader.GetFieldValueAsync<string[]>(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
            }
        }

        Assert.Equal(
            [
                "identidade_localizar_por_email",
                "identidade_localizar_por_usuario",
                "identidade_registrar_email",
                "outbox_encerrar_pendencia",
                "outbox_organizacoes_pendentes",
                TriggerFunction,
            ],
            functions.Select(f => f.Name));
        foreach (var f in functions)
        {
            Assert.Contains("search_path=pg_catalog, pg_temp", f.Config ?? [], StringComparer.Ordinal);
            Assert.False(f.PublicExecute, $"{f.Name} is executable by PUBLIC");
            // The outbox trigger function (ticket 07) fires with the owner's rights on insert into outbox_eventos; nobody calls it, so the application role has no EXECUTE.
            Assert.Equal(f.Name != TriggerFunction, f.AppExecute);
            Assert.False(f.OwnerSuper, $"{f.Name} is owned by a superuser");
            Assert.False(f.OwnerBypass, $"{f.Name} is owned by a role that bypasses RLS");
        }
    }

    [Fact]
    public async Task No_role_that_bypasses_row_level_security_exists_besides_the_bootstrap_superuser()
    {
        var db = await postgres.CreateDatabaseAsync();
        await using var owner = NpgsqlDataSource.Create(db.OwnerConnectionString);
        await using var connection = await owner.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT rolname FROM pg_roles WHERE rolbypassrls AND NOT rolsuper", connection);
        Assert.Empty(await ReadAll(command));

        await using var app = new NpgsqlCommand("SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = 'flow4tech_app'", connection);
        Assert.False((bool)(await app.ExecuteScalarAsync())!);
        await using var ownerRole = new NpgsqlCommand("SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = current_user", connection);
        Assert.False((bool)(await ownerRole.ExecuteScalarAsync())!);
    }
}
