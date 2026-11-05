using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flow4Tech.Identidade.Infrastructure;

public sealed class IdentidadeOptions
{
    public Argon2Options Argon2 { get; set; } = new();

    /// <summary>Every login outcome takes at least this long (spec 5.2.8).</summary>
    public TimeSpan TempoMinimoDeLogin { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Query the HIBP range API (k-anonymity). The local list always applies.</summary>
    public bool UsarHibp { get; set; }

    public Uri HibpBaseAddress { get; set; } = new("https://api.pwnedpasswords.com/");

    /// <summary>How long a temporary credential works, in hours (default 72). Ticket 10 moves this to layered settings.</summary>
    public int CredencialTemporariaHoras { get; set; } = 72;

    /// <summary>Progressive lock, credential stuffing and CAPTCHA thresholds (ticket 08, spec 5.5). Ticket 10 moves them to layered settings.</summary>
    public LimitesDeLogin Bloqueio { get; set; } = new();

    /// <summary>
    /// Only meaningful with <see cref="UsarHibp"/>. When HIBP is unreachable: false = accept the password (availability, the local list still applies), true = refuse it with the
    /// typed result <c>VerificacaoIndisponivel</c> (the person tries again later). With HIBP off the option does nothing: there is no check to be unavailable.
    /// </summary>
    public bool HibpIndisponivelRecusa { get; set; }

    /// <summary>How long an invitation works, in days (default 7). Ticket 10 moves this to layered settings.</summary>
    public int ConviteDias { get; set; } = 7;

    /// <summary>How long a recovery link works, in minutes (default 60).</summary>
    public int RecuperacaoMinutos { get; set; } = 60;

    /// <summary>The front end that receives the links of invitation and recovery e-mails (<c>Flow4Tech:App:BaseUrl</c>). Only the path <c>/convite</c> and <c>/recuperar</c> are appended; the token goes in the fragment.</summary>
    public Uri UrlDoApp { get; set; } = new("http://localhost:5173");

    /// <summary>Google sign-in (off without client id and secret).</summary>
    public OpcoesGoogle Google { get; set; } = new();

    /// <summary>Signing keys and lifetime of the access token (ticket 04). <c>AppKey</c> is required.</summary>
    public TokensOptions Tokens { get; set; } = new();

    /// <summary>The two session clocks (ticket 04). Ticket 10 moves them to layered settings per profile and organization.</summary>
    public SessoesOptions Sessoes { get; set; } = new();
}

public sealed class SessoesOptions
{
    /// <summary>The session ends after this long without activity (default 12 hours: a shift).</summary>
    public TimeSpan Inatividade { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The session ends this long after it started, activity or not (default 14 days).</summary>
    public TimeSpan Absoluta { get; set; } = TimeSpan.FromDays(14);

    /// <summary>The activity write happens at most once per this interval (default 1 minute).</summary>
    public TimeSpan IntervaloDeAtividade { get; set; } = TimeSpan.FromMinutes(1);
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

        if (options.CredencialTemporariaHoras < 1)
        {
            throw new InvalidOperationException("A temporary credential must be valid for at least one hour.");
        }

        if (options.ConviteDias < 1 || options.RecuperacaoMinutos < 5 || options.RecuperacaoMinutos > 24 * 60)
        {
            throw new InvalidOperationException("An invitation must last at least a day; a recovery link between 5 minutes and 24 hours.");
        }

        options.Google.Validar();
        services.AddSingleton(new OpcoesDeAcesso(TimeSpan.FromDays(options.ConviteDias), TimeSpan.FromMinutes(options.RecuperacaoMinutos), options.UrlDoApp));
        services.AddSingleton(new ValidadeDaCredencialTemporaria(TimeSpan.FromHours(options.CredencialTemporariaHoras)));
        services.AddSingleton<IPasswordHasher>(_ => new Argon2PasswordHasher(options.Argon2));
        services.AddSingleton<ITemporaryCredentialGenerator, TemporaryCredentialGenerator>();
        services.AddSingleton<ICompromisedPasswordCheck>(provider =>
            new CompositeCompromisedPasswordCheck(
                new LocalCompromisedPasswords(),
                options.UsarHibp ? new HibpPasswordCheck(HibpClient(options)) : null,
                provider.GetRequiredService<ITechnicalLog>(),
                options.HibpIndisponivelRecusa));
        services.AddSingleton<IResponseTimeFloor>(_ => new ResponseTimeFloor(options.TempoMinimoDeLogin));
        ValidarLimites(options.Bloqueio);
        // Without the Organizacao module's layered settings (a test host, a console) the system layer is what the options carry: same declared options, values from code.
        services.TryAddSingleton<ISettings>(_ => new SystemSettings(definition => OpcoesComoSistema(options, definition)));
        services.AddSingleton<ILimitadorDeLogin>(provider => new LimitadorDeLoginEmMemoria(provider.GetRequiredService<IClock>(), ConfiguracoesDeIdentidade.Limites(provider.GetRequiredService<ISettings>(), options.Bloqueio)));
        services.TryAddSingleton<ICaptcha, CaptchaNaoConfigurado>();
        services.TryAddSingleton<EnviadorDeEmailDeDesenvolvimento>();
        services.TryAddSingleton<IEnviadorDeEmail>(provider => provider.GetRequiredService<EnviadorDeEmailDeDesenvolvimento>());
        services.TryAddSingleton<IProcessedEventStore, DatabaseProcessedEventStore>();
        services.AddSingleton<IAcessosConhecidos, PostgresAcessosConhecidos>();
        // The alert consumers are registered as IOutboxConsumer; the dispatcher (not in the DI yet) takes every registered consumer. Built by factory: one instance per event type.
        foreach (var tipo in new[] { LoginNovo.Type, ContaBloqueada.Type, SenhaTrocada.Type, AcessoRecuperado.Type })
        {
            services.AddSingleton<IOutboxConsumer>(provider => AlertasDeSeguranca.Consumidores(provider.GetRequiredService<IProcessedEventStore>(), provider.GetRequiredService<IEnviadorDeEmail>()).Single(c => c.EventType == tipo));
        }
        services.AddSingleton<IContaStore, PostgresContaStore>();
        services.AddSingleton<IPreAuthLookup, PostgresPreAuthLookup>();
        if (options.Sessoes.Inatividade <= TimeSpan.Zero || options.Sessoes.Absoluta <= TimeSpan.Zero || options.Sessoes.IntervaloDeAtividade < TimeSpan.Zero)
        {
            throw new InvalidOperationException("The session clocks must be positive.");
        }

        // The emitter is built lazily so the host can resolve it at start and refuse to boot without a signing key (Program.cs).
        services.AddSingleton<IEmissorDeTokens>(provider => new JwtEmissorDeTokens(provider.GetRequiredService<IClock>(), options.Tokens));
        services.TryAddSingleton<OutboxWriter>();
        services.AddSingleton<PostgresSessaoStore>();
        services.AddSingleton<ISessaoStore>(provider => provider.GetRequiredService<PostgresSessaoStore>());
        services.AddSingleton<IRevogacaoDeSessoes>(provider => provider.GetRequiredService<PostgresSessaoStore>());
        services.AddSingleton<IConvitesStore, PostgresConvitesStore>();
        services.AddSingleton<IRecuperacoesStore, PostgresRecuperacoesStore>();
        services.AddSingleton<IIdentidadesExternasStore, PostgresIdentidadesExternasStore>();
        // Google (ticket 09). The HTTP client is one long-lived instance with short timeouts; the transaction seal is derived from the token key (replaced by the host with the configured key, like the emitter).
        services.AddSingleton<IProvedorOidc>(provider => options.Google.Configurada
            ? new ProvedorOidcGoogle(new HttpClient { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 256 * 1024 }, options.Google, provider.GetRequiredService<IClock>())
            : new OidcNaoConfigurado());
        services.AddSingleton<ISeloDeTransacao>(provider => new SeloDeTransacaoHmac(options.Tokens.AppKey, provider.GetRequiredService<IClock>()));
        services.AddSingleton<IEventosDeSeguranca, OutboxEventosDeSeguranca>();
        services.AddSingleton<Sessoes>();
        services.AddSingleton<GestaoDeContas>();
        services.AddSingleton<TrocaDeSenha>();
        services.AddSingleton<Autenticacao>();
        services.AddSingleton<Convites>();
        services.AddSingleton<RecuperacaoDeAcesso>();
        services.AddSingleton<ContasGoogle>();
        return services;
    }

    private static void ValidarLimites(LimitesDeLogin l) => l.Validar();

    /// <summary>The value the options hold for a system-layer setting of this module, or null (= the declared default). Only used when the host did not register the layered settings.</summary>
    private static string? OpcoesComoSistema(IdentidadeOptions options, SettingDefinition definition)
    {
        var b = options.Bloqueio;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        if (definition.Key == IdentidadeSettings.SessaoInatividadeHoras.Key)
        {
            return ConfiguracoesDeIdentidade.Horas(options.Sessoes.Inatividade);
        }

        if (definition.Key == IdentidadeSettings.SessaoAbsolutaDias.Key)
        {
            return ConfiguracoesDeIdentidade.Dias(options.Sessoes.Absoluta);
        }

        if (definition.Key == IdentidadeSettings.SessaoIntervaloDeAtividadeSegundos.Key)
        {
            return ((long)options.Sessoes.IntervaloDeAtividade.TotalSeconds).ToString(c);
        }

        if (definition.Key == IdentidadeSettings.BloqueioFalhasEmail.Key)
        {
            return b.FalhasParaBloquearEmail.ToString(c);
        }

        if (definition.Key == IdentidadeSettings.BloqueioFalhasOperacional.Key)
        {
            return b.FalhasParaBloquearOperacional.ToString(c);
        }

        if (definition.Key == IdentidadeSettings.BloqueioBaseSegundos.Key)
        {
            return b.BloqueioBase.TotalSeconds.ToString(c);
        }

        if (definition.Key == IdentidadeSettings.BloqueioMaximoMinutos.Key)
        {
            return b.BloqueioMaximo.TotalMinutes.ToString(c);
        }

        if (definition.Key == IdentidadeSettings.BloqueioFalhasPorIp.Key)
        {
            return b.FalhasParaBloquearIp.ToString(c);
        }

        if (definition.Key == IdentidadeSettings.BloqueioContasPorIp.Key)
        {
            return b.ContasDistintasPorIp.ToString(c);
        }

        return definition.Key == IdentidadeSettings.BloqueioIpsPorConta.Key ? b.IpsDistintosPorConta.ToString(c) : null;
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
