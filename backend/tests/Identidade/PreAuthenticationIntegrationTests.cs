using Flow4Tech.Identidade.Domain;
using Flow4Tech.Isolation.Tests;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.4: the login finds ONE account by an exact key before any organization context exists, without a role that ignores RLS and without any listing.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class PreAuthenticationIntegrationTests(PostgresFixture postgres)
{
    private static async Task<IdentidadeEnv> ArrangeAsync(PostgresFixture postgres)
    {
        var env = await IdentidadeEnv.CreateAsync(postgres);
        var donoA = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var donoB = await env.CriarDonoAsync(OrgB, "dono@b.com");
        await env.CriarOperacionalAsync(donoA, OrgA, "maria");
        await env.CriarOperacionalAsync(donoB, OrgB, "maria");
        await env.CriarOperacionalAsync(donoB, OrgB, "so-na-b");
        return env;
    }

    [Fact]
    public async Task The_same_user_name_in_two_organizations_resolves_to_each_organizations_own_account()
    {
        await using var env = await ArrangeAsync(postgres);

        var a = await env.Lookup.LocalizarOperacionalAsync(OrgA, "maria", CancellationToken.None);
        var b = await env.Lookup.LocalizarOperacionalAsync(OrgB, "maria", CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(OrgA, a.OrganizacaoId);
        Assert.Equal(OrgB, b.OrganizacaoId);
        Assert.NotEqual(a.ContaId, b.ContaId);
        Assert.Equal(TipoConta.Operacional, a.Tipo);
        Assert.True(a.Ativa);
        Assert.True(a.DeveTrocarSenha);
        Assert.StartsWith("$argon2id$", a.SenhaHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_name_that_exists_only_in_another_organization_is_not_found()
    {
        await using var env = await ArrangeAsync(postgres);

        Assert.Null(await env.Lookup.LocalizarOperacionalAsync(OrgA, "so-na-b", CancellationToken.None));
        Assert.Null(await env.Lookup.LocalizarOperacionalAsync(Guid.NewGuid(), "maria", CancellationToken.None)); // organization that does not exist
        Assert.Null(await env.Lookup.LocalizarOperacionalAsync(Guid.Empty, "maria", CancellationToken.None));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("mar%")]
    [InlineData("m_ria")]
    [InlineData("maria' OR '1'='1")]
    [InlineData("")]
    [InlineData("MARIA")] // the caller normalizes: the function compares the exact key
    public async Task The_lookup_is_an_exact_key_never_a_pattern_so_it_cannot_enumerate(string probe)
    {
        await using var env = await ArrangeAsync(postgres);

        Assert.Null(await env.Lookup.LocalizarOperacionalAsync(OrgA, probe, CancellationToken.None));
        Assert.Null(await env.Lookup.LocalizarPorEmailAsync(probe, CancellationToken.None));
    }

    [Fact]
    public async Task An_email_resolves_to_its_one_account_and_organization_and_an_unknown_one_to_nothing()
    {
        await using var env = await ArrangeAsync(postgres);

        var a = await env.Lookup.LocalizarPorEmailAsync("dono@a.com", CancellationToken.None);
        var b = await env.Lookup.LocalizarPorEmailAsync("dono@b.com", CancellationToken.None);

        Assert.Equal(OrgA, a!.OrganizacaoId);
        Assert.Equal(OrgB, b!.OrganizacaoId);
        Assert.Equal(TipoConta.Email, a.Tipo);
        Assert.False(a.DeveTrocarSenha);
        Assert.Null(await env.Lookup.LocalizarPorEmailAsync("ninguem@a.com", CancellationToken.None));
    }

    [Fact]
    public async Task An_operational_account_is_never_found_by_email_and_an_email_account_never_by_user_name()
    {
        await using var env = await ArrangeAsync(postgres);

        Assert.Null(await env.Lookup.LocalizarOperacionalAsync(OrgA, "dono@a.com", CancellationToken.None));
        Assert.Null(await env.Lookup.LocalizarPorEmailAsync("maria", CancellationToken.None));
    }

    [Fact]
    public async Task The_lookup_leaves_no_organization_context_behind_on_the_pooled_connection()
    {
        await using var env = await ArrangeAsync(postgres);

        await env.Lookup.LocalizarOperacionalAsync(OrgA, "maria", CancellationToken.None);
        await env.Lookup.LocalizarPorEmailAsync("dono@b.com", CancellationToken.None);

        await using var connection = await env.App.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT coalesce(current_setting('app.organizacao_id', true), '')", connection);
        Assert.Equal(string.Empty, (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task The_functions_return_at_most_one_row_and_refuse_to_run_inside_an_organization_context()
    {
        await using var env = await ArrangeAsync(postgres);

        await using (var connection = await env.App.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM identidade_localizar_por_usuario(@o, @u)", connection))
        {
            command.Parameters.AddWithValue("o", OrgB);
            command.Parameters.AddWithValue("u", "maria");
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }

        // Inside a tenant transaction (where an injected statement would run) the door is shut.
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM identidade_localizar_por_usuario(@o, @u)", tx.Connection, tx.Transaction);
            command.Parameters.AddWithValue("o", OrgB);
            command.Parameters.AddWithValue("u", "maria");
            await command.ExecuteScalarAsync();
        });
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task The_returned_account_never_prints_its_hash()
    {
        await using var env = await ArrangeAsync(postgres);
        var a = await env.Lookup.LocalizarOperacionalAsync(OrgA, "maria", CancellationToken.None);

        Assert.DoesNotContain(a!.SenhaHash, a.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("argon2", $"{a}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deactivated_account_is_still_found_so_the_login_does_the_same_work_and_answers_the_same_failure()
    {
        await using var env = await ArrangeAsync(postgres);
        var donoA = (await env.Lookup.LocalizarPorEmailAsync("dono@a.com", CancellationToken.None))!.ContaId;
        var maria = await env.Lookup.LocalizarOperacionalAsync(OrgA, "maria", CancellationToken.None);
        await env.Contas.DesativarAsync(IdentidadeEnv.As(donoA, OrgA), maria!.ContaId);

        var after = await env.Lookup.LocalizarOperacionalAsync(OrgA, "maria", CancellationToken.None);

        Assert.NotNull(after);
        Assert.False(after.Ativa);
    }
}
