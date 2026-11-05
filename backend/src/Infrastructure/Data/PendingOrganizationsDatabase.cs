using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>An organization the outbox registry says has pending events, and since when. Carries no event data.</summary>
public sealed record PendingOrganization(Guid OrganizationId, DateTimeOffset Since);

/// <summary>
/// Discovery for the outbox dispatcher (ticket 07, spec 5.8, ADR-003): which organizations have pending events. RLS hides the events without a context, so
/// this opens a connection WITHOUT one, and, like <see cref="PreAuthDatabase"/>, it can only call one SECURITY DEFINER function of migration 0011
/// (<c>outbox_organizacoes_pendentes</c>: ids and instants out, refuses to run inside an organization context). It takes no SQL text, returns no connection
/// and always rolls back. The application role has no BYPASSRLS and no privilege on the registry table; the events are still read under RLS, one
/// organization at a time.
/// </summary>
public sealed class PendingOrganizationsDatabase(NpgsqlDataSource dataSource)
{
    private const string Query = "SELECT o_organizacao_id, o_desde FROM outbox_organizacoes_pendentes(@limite)";

    public async Task<IReadOnlyList<PendingOrganization>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = new List<PendingOrganization>();
        await using (var command = new NpgsqlCommand(Query, connection, transaction))
        {
            command.Parameters.AddWithValue("limite", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new PendingOrganization(reader.GetGuid(0), await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken)));
            }
        }

        await transaction.RollbackAsync(cancellationToken);
        return result;
    }
}
