using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Npgsql;

namespace DataAccess.Spike;

/// <summary>Baseline: plain Npgsql on the tenant transaction.</summary>
public sealed class RawStore(TenantDatabase tenant) : IItemStore
{
    private const string InsertSql = "INSERT INTO amostra_itens (id, organizacao_id, unidade_id, nome, valor_centavos) VALUES (@id, @org, @unit, @nome, @valor)";

    public async Task AddAsync(OrganizationContext ctx, Item item)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        await InsertAsync(tx, item);
        await tx.CommitAsync();
    }

    public async Task AddThenFailAsync(OrganizationContext ctx, Item item)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        await InsertAsync(tx, item);
        throw new InvalidOperationException("boom");
    }

    public async Task<IReadOnlyList<Item>> ListAsync(OrganizationContext ctx)
    {
        await using var tx = await tenant.BeginAsync(ctx);
        await using var command = new NpgsqlCommand("SELECT id, organizacao_id, unidade_id, nome, valor_centavos FROM amostra_itens ORDER BY nome", tx.Connection, tx.Transaction);
        var result = new List<Item>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                result.Add(new Item(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetInt64(4)));
            }
        }

        await tx.CommitAsync();
        return result;
    }

    private static async Task InsertAsync(TenantTransaction tx, Item item)
    {
        await using var command = new NpgsqlCommand(InsertSql, tx.Connection, tx.Transaction);
        command.Parameters.AddWithValue("id", item.Id);
        command.Parameters.AddWithValue("org", item.OrganizacaoId);
        command.Parameters.AddWithValue("unit", (object?)item.UnidadeId ?? DBNull.Value);
        command.Parameters.AddWithValue("nome", item.Nome);
        command.Parameters.AddWithValue("valor", item.ValorCentavos);
        await command.ExecuteNonQueryAsync();
    }
}
