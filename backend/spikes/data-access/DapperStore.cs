using Dapper;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;

namespace DataAccess.Spike;

/// <summary>Dapper on the tenant transaction: the SQL is the contract, the record is the typing.</summary>
public sealed class DapperStore(TenantDatabase tenant) : IItemStore
{
    private const string InsertSql = "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@Id, @OrganizacaoId, @UnidadeId, @Nome, @ValorCentavos)";

    static DapperStore() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public async Task AddAsync(OrganizationContext ctx, Item item)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        await tx.Connection.ExecuteAsync(InsertSql, item, tx.Transaction);
        await tx.CommitAsync();
    }

    public async Task AddThenFailAsync(OrganizationContext ctx, Item item)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        await tx.Connection.ExecuteAsync(InsertSql, item, tx.Transaction);
        throw new InvalidOperationException("boom");
    }

    public async Task<IReadOnlyList<Item>> ListAsync(OrganizationContext ctx)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        var rows = await tx.Connection.QueryAsync<Item>(
            "SELECT id AS Id, organizacao_id AS OrganizacaoId, unidade_id AS UnidadeId, nome AS Nome, valor_centavos AS ValorCentavos FROM amostra_itens ORDER BY nome",
            transaction: tx.Transaction);
        var list = rows.AsList();
        await tx.CommitAsync();
        return list;
    }
}
