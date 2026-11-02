using System.Text.Json.Nodes;

namespace Flow4Tech.Kernel.Audit;

/// <summary>Outcome of the audited action. Stored as <c>sucesso</c>, <c>negado</c> or <c>falha</c>.</summary>
public enum AuditResult
{
    Sucesso,
    Negado,
    Falha,
}

/// <summary>
/// One audit fact (spec 5.6.3). <see cref="Action"/> is <c>recurso:acao</c> and must be a name from an <see cref="AuditCatalogAttribute"/> class:
/// the catalog is versioned and a published name is never renamed (5.6.7). <see cref="Before"/>/<see cref="After"/> never carry secrets
/// (<see cref="SensitiveData"/>): a forbidden key or a secret-looking value makes the write fail, it is never stored.
/// Who/where/when/correlation are filled by the log (user and session by <see cref="AuditLogExtensions.RecordAsync(IAuditLog, IRequestContext, AuditEntry, CancellationToken)"/>).
/// </summary>
public sealed record AuditEntry
{
    public required string Action { get; init; }

    public AuditResult Result { get; init; } = AuditResult.Sucesso;

    public Guid? UserId { get; init; }

    public Guid? SessionId { get; init; }

    public Guid? UnitId { get; init; }

    public string? ObjectType { get; init; }

    public string? ObjectId { get; init; }

    public JsonObject? Before { get; init; }

    public JsonObject? After { get; init; }
}

/// <summary>
/// The audit trail as the Application layer sees it (spec 5.6). Append-only and hash-chained per organization; implementation in
/// Infrastructure (<c>Flow4Tech.Infrastructure.Audit</c>). Two doors, on purpose:
/// <list type="bullet">
/// <item><see cref="RecordAsync(OrganizationContext, AuditEntry, CancellationToken)"/> joins the unit of work of the action (<see cref="IUnitOfWork"/>): the record commits
/// with the action and disappears with it on rollback. Throws outside a unit of work.</item>
/// <item><see cref="RecordDetachedAsync"/> is for DELIBERATE failure records (login denied, access denied): it runs its own transaction and persists
/// even though the operation failed. Call it AFTER the unit of work ended (it throws inside one: both would contend for the chain lock).</item>
/// </list>
/// </summary>
public interface IAuditLog
{
    Task RecordAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default);

    Task RecordDetachedAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default);
}

public static class AuditLogExtensions
{
    /// <summary>Records on the request's own organization, filling user, session and unit from the request context.</summary>
    public static Task RecordAsync(this IAuditLog log, IRequestContext request, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entry);
        return log.RecordAsync(request.Organization, WithActor(request, entry), cancellationToken);
    }

    /// <summary>Same as <see cref="RecordAsync(IAuditLog, IRequestContext, AuditEntry, CancellationToken)"/> but on its own transaction (deliberate failure records).</summary>
    public static Task RecordDetachedAsync(this IAuditLog log, IRequestContext request, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entry);
        return log.RecordDetachedAsync(request.Organization, WithActor(request, entry), cancellationToken);
    }

    private static AuditEntry WithActor(IRequestContext request, AuditEntry entry) =>
        entry with
        {
            UserId = entry.UserId ?? request.UserId,
            SessionId = entry.SessionId ?? request.SessionId,
            UnitId = entry.UnitId ?? request.Organization.UnitId,
        };
}
