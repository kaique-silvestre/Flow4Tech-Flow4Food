using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Audit.Tests;

[Collection(PostgresDefinition.Name)]
public sealed class AuditSchemaIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_audit_EF_model_matches_the_migrated_database()
    {
        await using var env = await AuditEnv.CreateAsync(postgres);
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        await using var context = new AuditContext(tx);

        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
