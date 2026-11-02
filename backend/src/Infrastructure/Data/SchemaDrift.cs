using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// The schema is owned by the SQL migrations (<see cref="MigrationRunner"/>), not by EF. This compares a context's EF model with the
/// migrated database (table, column, store type, nullability) so model/schema drift fails a test instead of production.
/// Every module with a <see cref="TenantDbContext"/> calls it from an integration test.
/// </summary>
public static class SchemaDrift
{
    public static async Task<IReadOnlyList<string>> FindAsync(TenantDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var drift = new List<string>();
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        foreach (var entity in context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null))
        {
            var table = entity.GetTableName()!;
            var schema = entity.GetSchema() ?? "public";
            var actual = new Dictionary<string, (string Type, bool NotNull)>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand(
                "SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull FROM pg_attribute a " +
                "WHERE a.attrelid = to_regclass(@qualified) AND a.attnum > 0 AND NOT a.attisdropped",
                connection))
            {
                command.Parameters.AddWithValue("qualified", $"\"{schema}\".\"{table}\"");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    actual[reader.GetString(0)] = (reader.GetString(1), reader.GetBoolean(2));
                }
            }

            if (actual.Count == 0)
            {
                drift.Add($"{table}: table does not exist in the database");
                continue;
            }

            var id = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(id);
                if (column is null)
                {
                    continue;
                }

                if (!actual.TryGetValue(column, out var found))
                {
                    drift.Add($"{table}.{column}: column does not exist in the database");
                    continue;
                }

                var modelType = property.GetColumnType();
                if (!string.Equals(found.Type, modelType, StringComparison.OrdinalIgnoreCase))
                {
                    drift.Add($"{table}.{column}: database type {found.Type}, model type {modelType}");
                }

                if (found.NotNull == property.IsColumnNullable(id))
                {
                    drift.Add($"{table}.{column}: database {(found.NotNull ? "NOT NULL" : "nullable")}, model {(property.IsColumnNullable(id) ? "nullable" : "required")}");
                }
            }
        }

        return drift;
    }
}
