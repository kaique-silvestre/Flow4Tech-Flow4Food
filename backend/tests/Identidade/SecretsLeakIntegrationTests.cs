using Flow4Tech.Isolation.Tests;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.2 / 6: a password, a temporary credential or a hash never reaches the audit trail, any table outside the credential, a result, a message or an exception.</summary>
[Collection(PostgresDefinition.Name)]
public sealed class SecretsLeakIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task After_the_whole_life_of_accounts_no_secret_is_found_where_it_should_not_be()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com", "Frase-secreta-do-dono-77");
        var (maria, credencial) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        await env.Troca.TrocarAsync(OrgA, maria, credencial, "Outra-frase-secreta-88");
        await env.Auth.AutenticarOperacionalAsync(OrgA, "maria", "tentativa-errada-que-parece-senha");
        await env.Auth.AutenticarPorEmailAsync("dono@a.com", "Frase-secreta-do-dono-77");
        await env.Contas.DesativarAsync(IdentidadeEnv.As(dono, OrgA), maria);

        // Plain secrets: nowhere at all.
        foreach (var secret in new[] { "Frase-secreta-do-dono-77", "Outra-frase-secreta-88", credencial, "tentativa-errada-que-parece-senha" })
        {
            Assert.Empty(await env.TablesContainingAsync(secret));
        }

        // Hashes: only in the credential table.
        Assert.Equal(["credenciais"], await env.TablesContainingAsync("$argon2id$"));
        Assert.Equal(0L, await env.CountAsync(OrgA, "SELECT count(*) FROM auditoria_registros WHERE antes::text ILIKE '%argon%' OR depois::text ILIKE '%argon%' OR objeto_id ILIKE '%argon%'"));
    }

    [Fact]
    public async Task Audit_records_of_the_identity_actions_carry_only_the_facts_not_the_secrets()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        await env.CriarOperacionalAsync(dono, OrgA, "maria");

        var rows = await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT acao, coalesce(antes::text, ''), coalesce(depois::text, '') FROM auditoria_registros ORDER BY sequencia", r => (r.GetString(0), r.GetString(1), r.GetString(2)));

        Assert.Equal(["conta:criar", "conta:criar"], rows.Select(r => r.Item1));
        Assert.All(rows, r =>
        {
            Assert.DoesNotContain("senha", r.Item3, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credencial", r.Item3, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("\"tipo\": \"operacional\"", rows[1].Item3, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Results_and_exceptions_of_the_services_do_not_print_secrets()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, decorateAudit: inner => new FailingAudit(inner, "conta:criar"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => env.Contas.CriarDonoAsync(OrgA, "dono@a.com", "Dono", "Frase-secreta-do-dono-77"));

        Assert.DoesNotContain("Frase-secreta-do-dono-77", thrown.ToString(), StringComparison.Ordinal);
        var refused = await env.Contas.CriarDonoAsync(OrgA, "dono@a.com", "Dono", "curta");
        Assert.DoesNotContain("curta", refused.ToString(), StringComparison.Ordinal);
    }
}
