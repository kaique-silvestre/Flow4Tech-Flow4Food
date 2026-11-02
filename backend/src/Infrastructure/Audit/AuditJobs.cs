using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel;

namespace Flow4Tech.Infrastructure.Audit;

/// <summary>
/// Periodic integrity check as a background job (spec 5.6.5), behind <see cref="IBackgroundJobs"/>: whoever schedules (a timer per organization, later the
/// real scheduler) enqueues <see cref="VerifyChain"/>; the handler restores nothing ambient, it verifies the organization the job carries. A violation is not a job
/// failure: it becomes the outbox event <c>auditoria.integridade-violada</c>. The recurring trigger itself is not wired yet (ticket 01/02 have no scheduler).
/// </summary>
public static class AuditJobs
{
    /// <summary>Job type name. Stable: never rename once published.</summary>
    public const string VerifyChainType = "auditoria.verificar-cadeia";

    public static Job VerifyChain(OrganizationContext organization) => new(organization, VerifyChainType, "{}");

    public static JobHandler Handler(AuditChainVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return async (job, cancellationToken) =>
        {
            if (string.Equals(job.Type, VerifyChainType, StringComparison.Ordinal))
            {
                await verifier.VerifyAsync(job.Organization, null, cancellationToken);
            }
        };
    }
}
