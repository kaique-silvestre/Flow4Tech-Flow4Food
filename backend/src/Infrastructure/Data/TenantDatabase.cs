using Flow4Tech.Kernel;
using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// The only way application code gets a database connection for tenant data. Every unit of work is a transaction that
/// carries the organization context (<c>set_config(..., is_local =&gt; true)</c>, the parameterizable form of SET LOCAL),
/// so the context dies with the transaction and can never leak to the next user of a pooled connection.
/// The data source must use the application credential (not the owner, no BYPASSRLS).
/// </summary>
public sealed class TenantDatabase(NpgsqlDataSource dataSource)
{
    /// <summary>Name of the transaction-local setting read by the database function <c>app_organizacao_id()</c>.</summary>
    internal const string SettingName = "app.organizacao_id";

    public async Task<TenantTransaction> BeginAsync(OrganizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(context));
        }

        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT set_config(@name, @value, true)", connection, transaction);
            command.Parameters.AddWithValue("name", SettingName);
            command.Parameters.AddWithValue("value", context.OrganizationId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new TenantTransaction(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>A transaction with the organization context already set. Disposing without commit rolls back.</summary>
public sealed class TenantTransaction : IAsyncDisposable
{
    internal TenantTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    public Task CommitAsync(CancellationToken cancellationToken = default) => Transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
