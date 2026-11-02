using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// What the login needs about an account, as the pre-authentication functions return it (migration 0007). Carries the password hash, so it never prints:
/// <see cref="ToString"/> is a placeholder and there is no serializer for it.
/// </summary>
public sealed class PreAuthAccount(Guid accountId, Guid organizationId, string type, string passwordHash, bool mustChangePassword, bool active, int credentialVersion)
{
    public Guid AccountId { get; } = accountId;

    public Guid OrganizationId { get; } = organizationId;

    /// <summary><c>email</c> or <c>operacional</c> (column <c>contas.tipo</c>).</summary>
    public string Type { get; } = type;

    public string PasswordHash { get; } = passwordHash;

    public bool MustChangePassword { get; } = mustChangePassword;

    public bool Active { get; } = active;

    public int CredentialVersion { get; } = credentialVersion;

    public override string ToString() => "[pre-auth account]";
}

/// <summary>
/// The ONE place a connection is opened WITHOUT an organization context (spec 5.2.4): the login must find an account before it knows the organization.
/// It is not a general door. It can only call the two minimal SECURITY DEFINER functions of migration 0007 (exact key in, at most one row out, refuse to
/// run inside an organization context); it takes no SQL text, returns no connection and runs in a transaction that is always rolled back. The application
/// role has no BYPASSRLS and no privilege on the tables behind the functions (ADR-005).
/// </summary>
public sealed class PreAuthDatabase(NpgsqlDataSource dataSource)
{
    private const string ByUser = "SELECT o_conta_id, o_organizacao_id, o_tipo, o_senha_hash, o_deve_trocar_senha, o_ativa, o_versao_credencial FROM identidade_localizar_por_usuario(@organizacao, @usuario)";

    private const string ByEmail = "SELECT o_conta_id, o_organizacao_id, o_tipo, o_senha_hash, o_deve_trocar_senha, o_ativa, o_versao_credencial FROM identidade_localizar_por_email(@email)";

    public async Task<PreAuthAccount?> FindOperationalAsync(Guid organizationId, string normalizedUser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedUser);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ByUser, connection, transaction);
        command.Parameters.AddWithValue("organizacao", organizationId);
        command.Parameters.AddWithValue("usuario", normalizedUser);
        return await ReadAndRollbackAsync(command, transaction, cancellationToken);
    }

    public async Task<PreAuthAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ByEmail, connection, transaction);
        command.Parameters.AddWithValue("email", normalizedEmail);
        return await ReadAndRollbackAsync(command, transaction, cancellationToken);
    }

    private static async Task<PreAuthAccount?> ReadAndRollbackAsync(NpgsqlCommand command, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        PreAuthAccount? account = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                account = new PreAuthAccount(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5),
                    reader.GetInt32(6));
            }
        }

        // Nothing to commit (read only); rolling back also makes sure no transaction-local setting survives on the pooled connection.
        await transaction.RollbackAsync(cancellationToken);
        return account;
    }
}
