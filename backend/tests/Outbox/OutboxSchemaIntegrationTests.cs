using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Outbox.Tests;

[Collection(PostgresDefinition.Name)]
public sealed class OutboxSchemaIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_outbox_EF_model_matches_the_migrated_database()
    {
        await using var env = await Env.CreateAsync(postgres);
        await using var tx = await env.Tenant.BeginAsync(Ctx(OrgA));
        await using var context = new OutboxContext(tx);

        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
