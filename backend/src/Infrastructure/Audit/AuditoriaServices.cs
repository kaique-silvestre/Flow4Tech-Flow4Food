using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Kernel.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flow4Tech.Infrastructure.Audit;

public static class AuditoriaServices
{
    /// <summary>Registers the audit trail, its chain verifier and the technical log. Needs <c>AddTenantData</c> and an <c>IClock</c>.</summary>
    public static IServiceCollection AddAuditoria(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OutboxWriter>();
        services.AddSingleton<IAuditLog, PostgresAuditLog>();
        services.AddSingleton<AuditChainVerifier>();
        services.AddSingleton<ITechnicalLog, TechnicalLog>();
        return services;
    }
}
