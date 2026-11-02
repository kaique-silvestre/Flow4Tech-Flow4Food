using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Identidade.Infrastructure;

public sealed class IdentidadeOptions
{
    public Argon2Options Argon2 { get; set; } = new();

    /// <summary>Every login outcome takes at least this long (spec 5.2.8).</summary>
    public TimeSpan TempoMinimoDeLogin { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Query the HIBP range API (k-anonymity). The local list always applies.</summary>
    public bool UsarHibp { get; set; }

    public Uri HibpBaseAddress { get; set; } = new("https://api.pwnedpasswords.com/");

    /// <summary>When HIBP is unreachable: false = accept the password (availability), true = refuse it.</summary>
    public bool HibpIndisponivelRecusa { get; set; }
}

public static class IdentidadeServices
{
    /// <summary>
    /// Registers the Identidade module. Needs <c>AddTenantData</c>, <c>AddAuditoria</c> and an <c>IClock</c>. All services are singletons: they hold no request state
    /// (the request context is an argument of each call).
    /// </summary>
    public static IServiceCollection AddIdentidade(this IServiceCollection services, Action<IdentidadeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new IdentidadeOptions();
        configure?.Invoke(options);
        if (options.UsarHibp && options.HibpBaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The breach service must be reached over https.");
        }

        services.AddSingleton<IPasswordHasher>(_ => new Argon2PasswordHasher(options.Argon2));
        services.AddSingleton<ITemporaryCredentialGenerator, TemporaryCredentialGenerator>();
        services.AddSingleton<ICompromisedPasswordCheck>(provider =>
            new CompositeCompromisedPasswordCheck(
                new LocalCompromisedPasswords(),
                options.UsarHibp ? new HibpPasswordCheck(HibpClient(options)) : null,
                provider.GetRequiredService<ITechnicalLog>()));
        services.AddSingleton<IResponseTimeFloor>(_ => new ResponseTimeFloor(options.TempoMinimoDeLogin));
        services.AddSingleton<IContaStore, PostgresContaStore>();
        services.AddSingleton<IPreAuthLookup, PostgresPreAuthLookup>();
        services.AddSingleton<GestaoDeContas>();
        services.AddSingleton<TrocaDeSenha>();
        services.AddSingleton<Autenticacao>();
        return services;
    }

    private static HttpClient HibpClient(IdentidadeOptions options)
    {
        // One long-lived client for the process (the default handler pools connections). Known limit: the default handler never re-resolves DNS while the process lives.
        var client = new HttpClient();
        client.BaseAddress = options.HibpBaseAddress;
        client.Timeout = TimeSpan.FromSeconds(3);
        return client;
    }
}
