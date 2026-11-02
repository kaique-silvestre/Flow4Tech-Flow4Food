using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// Applies the versioned <c>*.sql</c> files of <c>backend/migrations</c> in file-name order, each in its own transaction,
/// recording version and SHA-256 checksum in <c>schema_migrations</c> (editing an applied file is refused). Always run with the OWNER/migration credential, never the application one.
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
                "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now(), checksum text)",
                connection))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            // Journals written before checksums existed: add the column; null checksums are filled on first sight below.
            await using (var upgrade = new NpgsqlCommand("ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS checksum text", connection))
            {
                await upgrade.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.sql").Order(StringComparer.Ordinal))
            {
                var version = Path.GetFileNameWithoutExtension(file);
                var text = await File.ReadAllTextAsync(file, cancellationToken);
                var checksum = Checksum(text);
                var (isApplied, recorded) = await Recorded(connection, version, cancellationToken);
                if (isApplied)
                {
                    if (recorded is null)
                    {
                        await SetChecksum(connection, version, checksum, cancellationToken);
                    }
                    else if (recorded != checksum)
                    {
                        throw new InvalidOperationException($"Migration {version} was edited after being applied (checksum mismatch). Never edit applied files; add a new one.");
                    }

                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using (var script = new NpgsqlCommand(text, connection, transaction))
                {
                    await script.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (version, checksum) VALUES (@version, @checksum)", connection, transaction))
                {
                    record.Parameters.AddWithValue("version", version);
                    record.Parameters.AddWithValue("checksum", checksum);
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

    /// <summary>SHA-256 of the file with line endings normalized (git autocrlf must not look like an edit).</summary>
    private static string Checksum(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static async Task<(bool IsApplied, string? Checksum)> Recorded(NpgsqlConnection connection, string version, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT checksum FROM schema_migrations WHERE version = @version", connection);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (true, await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetString(0)) : (false, null);
    }

    private static async Task SetChecksum(NpgsqlConnection connection, string version, string checksum, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE schema_migrations SET checksum = @checksum WHERE version = @version", connection);
        command.Parameters.AddWithValue("checksum", checksum);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
