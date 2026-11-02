using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// Base class of EVERY EF Core context that touches tenant data. It cannot be constructed without a <see cref="TenantTransaction"/>
/// (whose constructor is internal to this assembly, so it only comes from <see cref="TenantDatabase.BeginAsync"/>): the context
/// joins that transaction on its connection, so the organization context set by <c>set_config</c> applies to every statement.
/// Modules derive from this class; deriving from <see cref="DbContext"/> directly, AddDbContext and UseNpgsql outside this project are
/// banned by the architecture tests. A command that is not inside a live tenant transaction (for example after commit) throws
/// <see cref="TenantContextRequiredException"/> before reaching the database.
/// </summary>
public abstract class TenantDbContext : DbContext
{
    private readonly TenantTransaction _transaction;

    protected TenantDbContext(TenantTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        _transaction = transaction;
        Database.UseTransaction(transaction.Transaction);
    }

    protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.UseNpgsql(_transaction.Connection).AddInterceptors(TenantCommandGuard.Instance);
    }
}

/// <summary>A command ran outside a live tenant transaction (organization context not guaranteed).</summary>
public sealed class TenantContextRequiredException : InvalidOperationException
{
    public TenantContextRequiredException()
        : base("Command refused: not inside an open tenant transaction.")
    {
    }

    public TenantContextRequiredException(string message)
        : base(message)
    {
    }

    public TenantContextRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Registry of the transactions created by <see cref="TenantDatabase"/> plus the interceptor that refuses any other command.</summary>
internal sealed class TenantCommandGuard : DbCommandInterceptor
{
    // Npgsql reuses the NpgsqlTransaction instance of a pooled connector, so the key maps to the CURRENT tenant transaction (AddOrUpdate).
    private static readonly ConditionalWeakTable<DbTransaction, TenantTransaction> Live = [];

    public static TenantCommandGuard Instance { get; } = new();

    public static void Register(DbTransaction transaction, TenantTransaction owner) => Live.AddOrUpdate(transaction, owner);

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    private static void Check(DbCommand command)
    {
        var transaction = command.Transaction;
        if (transaction is null || !Live.TryGetValue(transaction, out var owner) || owner.Ended || transaction.Connection is null
            || !ReferenceEquals(command.Connection, owner.Connection)) // a context from a finished unit of work, reused after another tenant took the pooled connector
        {
            throw new TenantContextRequiredException("Command refused: it is not running inside an open TenantDatabase.BeginAsync transaction (ADR-002).");
        }
    }
}
