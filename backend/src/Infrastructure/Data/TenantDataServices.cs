using Flow4Tech.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

public static class TenantDataServices
{
    /// <summary>
    /// Registers the only door to tenant data: <see cref="TenantDatabase"/> and <see cref="IUnitOfWork"/>. The connection (application
    /// credential) is read lazily from <paramref name="connectionString"/> the first time data is touched, so a host without a database still starts.
    /// </summary>
    public static IServiceCollection AddTenantData(this IServiceCollection services, Func<IServiceProvider, string?> connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionString);
        services.AddSingleton(provider =>
            NpgsqlDataSource.Create(connectionString(provider) ?? throw new InvalidOperationException("No database connection configured (ConnectionStrings__Default or DATABASE_URL).")));
        services.AddSingleton<TenantDatabase>();
        services.AddSingleton<PreAuthDatabase>();
        services.AddSingleton<PendingOrganizationsDatabase>();
        services.AddSingleton<IUnitOfWork, TenantUnitOfWork>();
        return services;
    }
}
