using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Permissoes.Infrastructure;

public static class PermissoesServices
{
    /// <summary>
    /// Registers the authorization engine behind <see cref="IAuthorizationEngine"/>: the rules-as-data engine over the factory policy built from the catalog
    /// (ticket 06 replaces the policy with the rules persisted per organization), the cumulative-limit store and the operating-day resolver.
    /// Needs <c>IClock</c> and <c>AddTenantData</c>.
    /// </summary>
    public static IServiceCollection AddPermissoes(this IServiceCollection services, IEnumerable<string> catalogo)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalogo);
        var policy = PoliticaDeFabrica.Criar(catalogo);
        services.AddSingleton(policy);
        services.AddSingleton<ICumulativeLimitStore, PostgresCumulativeLimitStore>();
        services.AddSingleton<IOperatingDayResolver>(provider => new ConfiguredOperatingDayResolver(provider.GetRequiredService<Flow4Tech.Kernel.IClock>()));
        services.AddSingleton<IAuthorizationEngine, RulesAuthorizationEngine>();
        return services;
    }
}
