using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// Applies the versioned <c>*.sql</c> files of <c>backend/migrations</c> in file-name order, each in its own transaction,
/// recording versions in <c>schema_migrations</c>. Always run with the OWNER/migration credential, never the application one.
/// </summary>
public static class MigrationRunner
{
    private const long AdvisoryLockKey = 7_272_001;

    /// <summary>Returns the versions applied by this call (already-applied files are skipped).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Executing the SQL text of repository migration files is the purpose of this method; input is never user-supplied.")]
    public static async Task<IReadOnlyList<string>> ApplyAsync(string ownerConnectionString, string directory, CancellationToken cancellationToken = default)
    {
        var applied = new List<string>();
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        await AdvisoryLock(connection, acquire: true, cancellationToken);
        try
        {
            await using (var create = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
                connection))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.sql").Order(StringComparer.Ordinal))
            {
                var version = Path.GetFileNameWithoutExtension(file);
                if (await IsApplied(connection, version, cancellationToken))
                {
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using (var script = new NpgsqlCommand(await File.ReadAllTextAsync(file, cancellationToken), connection, transaction))
                {
                    await script.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@version)", connection, transaction))
                {
                    record.Parameters.AddWithValue("version", version);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                applied.Add(version);
            }
        }
        finally
        {
            await AdvisoryLock(connection, acquire: false, CancellationToken.None);
        }

        return applied;
    }

    private static async Task<bool> IsApplied(NpgsqlConnection connection, string version, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = @version)", connection);
        command.Parameters.AddWithValue("version", version);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task AdvisoryLock(NpgsqlConnection connection, bool acquire, CancellationToken cancellationToken)
    {
        await using var command = acquire
            ? new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection)
            : new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
