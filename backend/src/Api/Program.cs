using Flow4Tech.Api;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Caixa.Application;
using Flow4Tech.Caixa.Infrastructure;
using Flow4Tech.Pagamentos.Infrastructure;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Comandas.Infrastructure;
using Flow4Tech.Cardapio.Infrastructure;
using Flow4Tech.Kernel;
using Flow4Tech.Organizacao.Infrastructure;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Railway: PORT is injected; DATABASE_URL (postgres://...) is mapped when ConnectionStrings__Default is not set. Errors never echo the secret.
var listenUrl = HostBinding.ResolveUrl(
    builder.Configuration["PORT"],
    otherBindingConfigured: !string.IsNullOrWhiteSpace(builder.Configuration["URLS"]) || !string.IsNullOrWhiteSpace(builder.Configuration["HTTP_PORTS"]) || !string.IsNullOrWhiteSpace(builder.Configuration["HTTPS_PORTS"]));
if (listenUrl is not null)
{
    builder.WebHost.UseUrls(listenUrl);
}

var databaseConnection = DatabaseUrl.Resolve(builder.Configuration.GetConnectionString("Default"), builder.Configuration["DATABASE_URL"]);
if (databaseConnection is not null)
{
    builder.Configuration["ConnectionStrings:Default"] = databaseConnection;
}

// Structured technical log with scopes (CorrelationId rides in the scope). The framework's own request lines print the full URL, query string included
// (a ?token= would land in the log), so they are muted below Warning: CorrelationMiddleware writes the request line instead (method, route pattern, status).
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
// The permission catalog is generated from the module code (assemblies next to the host); the factory policy, the engine and the route check all read it.
var moduleCatalog = ModuleCatalog.Load(AppContext.BaseDirectory);
var moduleSettings = ModuleCatalog.LoadSettings(AppContext.BaseDirectory);
// x-permission (ticket 05): the contract tells every client which permission an operation needs, so the generated client and the screens can hide what a user may not do.
builder.Services.AddOpenApi(options => options.AddOperationTransformer((operation, context, _) =>
{
    if (context.Description.ActionDescriptor.EndpointMetadata.OfType<PermissionDeclaration>().FirstOrDefault() is { } declaration)
    {
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions["x-permission"] = new JsonNodeExtension(JsonValue.Create(declaration.Name));
    }

    return Task.CompletedTask;
}));
builder.Services.AddTenantData(provider => provider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddAuditoria();
// Layered settings (ticket 10): the catalog is collected from the modules; the SYSTEM layer of an option is its declared default, overridden by configuration (SettingDefinition.SystemConfigPath,
// which keeps the old Flow4Tech:Sessoes:* and Flow4Tech:Identidade:Bloqueio:* keys working). Read late, after every configuration source is loaded.
builder.Services.AddOrganizacao(
    moduleSettings,
    provider => definition => provider.GetRequiredService<IConfiguration>()[definition.SystemConfigPath]);
builder.Services.AddCardapio();
builder.Services.AddCaixa();
builder.Services.AddPagamentos();
builder.Services.AddComandas();
builder.Services.AddSingleton<IPermissaoDoOperador, PermissaoDoOperador>();
builder.Services.AddSingleton<IPermissaoDaComanda, PermissaoDoOperador>();
builder.Services.AddSingleton<IDiaOperacional, DiaOperacionalDaUnidade>();
builder.Services.AddSingleton<IPermissaoDoChamador, PermissaoDoChamador>();
// Permissions on HTTP (ticket 05): one engine (IAuthorizationEngine.DecidirAsync), asked by the handler for every route that declares a permission.
builder.Services.AddPermissoes(moduleCatalog);
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<IPerfisDoUsuario, PerfisDaConta>();
// Identidade (tickets 03 and 04): accounts, passwords, pre-authentication, sessions and tokens.
// Flow4Tech:Identidade:TempoMinimoDeLoginMs (default 400) is the response-time floor of a login attempt; Flow4Tech:Identidade:Hibp:Enabled turns on the k-anonymity breach check
// and Flow4Tech:Identidade:Hibp:IndisponivelRecusa (default false) makes an unreachable breach service refuse the password instead of accepting it;
// Flow4Tech:Identidade:CredencialTemporariaHoras (default 72) is how long a temporary credential works.
// Token keys are read lazily (SessionConfiguration), after all configuration is loaded; the session clocks and the lock policy are layered settings (IdentidadeSettings).
builder.Services.AddIdentidade(options =>
{
    options.TempoMinimoDeLogin = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Flow4Tech:Identidade:TempoMinimoDeLoginMs", 400));
    options.UsarHibp = builder.Configuration.GetValue("Flow4Tech:Identidade:Hibp:Enabled", false);
    options.HibpIndisponivelRecusa = builder.Configuration.GetValue("Flow4Tech:Identidade:Hibp:IndisponivelRecusa", false);
    options.CredencialTemporariaHoras = builder.Configuration.GetValue("Flow4Tech:Identidade:CredencialTemporariaHoras", 72);
    // Ticket 09: Flow4Tech:Identidade:ConviteDias (7), RecuperacaoMinutos (60), Flow4Tech:App:BaseUrl (front end that receives the links), and Google (off without ClientId and ClientSecret):
    // Flow4Tech:Identidade:Google:ClientId|ClientSecret|RedirectUri (the /login/google/retorno of this API, registered at Google).
    options.ConviteDias = builder.Configuration.GetValue("Flow4Tech:Identidade:ConviteDias", 7);
    options.RecuperacaoMinutos = builder.Configuration.GetValue("Flow4Tech:Identidade:RecuperacaoMinutos", 60);
    options.UrlDoApp = new Uri(builder.Configuration["Flow4Tech:App:BaseUrl"] is { Length: > 0 } baseUrl ? baseUrl : "http://localhost:5173");
    options.Google = new OpcoesGoogle
    {
        ClientId = builder.Configuration["Flow4Tech:Identidade:Google:ClientId"],
        ClientSecret = builder.Configuration["Flow4Tech:Identidade:Google:ClientSecret"],
        RedirectUri = builder.Configuration["Flow4Tech:Identidade:Google:RedirectUri"] is { Length: > 0 } redirect ? new Uri(redirect) : null,
    };
});
// The address of the client (login throttling by address, new-login alert). Behind a reverse proxy the peer is the proxy: with Flow4Tech:Rede:ProxyConfiavel=true the LAST entry of
// X-Forwarded-For (the one the proxy itself appended, ForwardLimit 1) becomes the client address. Only safe when the API is reachable ONLY through that proxy (Railway); default false.
var proxyConfiavel = builder.Configuration.GetValue("Flow4Tech:Rede:ProxyConfiavel", false);
if (proxyConfiavel)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
builder.Services.Replace(ServiceDescriptor.Singleton<IEmissorDeTokens>(provider => new JwtEmissorDeTokens(
    provider.GetRequiredService<IClock>(),
    SessionConfiguration.Tokens(provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IHostEnvironment>()))));
// Login protection (ticket 08, spec 5.5): the thresholds are the system layer of the bloqueio.* settings (Flow4Tech:Identidade:Bloqueio:{FalhasEmail,FalhasOperacional,BloqueioBaseSegundos,
// BloqueioMaximoMinutos,FalhasPorIp,ContasPorIp,IpsPorConta}), read when the limiter is first needed (AddIdentidade builds it from ISettings), after every configuration source is loaded.
builder.Services.Replace(ServiceDescriptor.Singleton<ISeloDeTransacao>(provider => new SeloDeTransacaoHmac(
    SessionConfiguration.Tokens(provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IHostEnvironment>()).AppKey,
    provider.GetRequiredService<IClock>())));
builder.Services.Replace(ServiceDescriptor.Singleton<IResponseTimeFloor>(provider => new ResponseTimeFloor(
    TimeSpan.FromMilliseconds(provider.GetRequiredService<IConfiguration>().GetValue("Flow4Tech:Identidade:TempoMinimoDeLoginMs", 400)))));
builder.Services.AddSingleton(provider => SessionConfiguration.Origins(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IUnidadesDaConta, UnidadesDaContaAdapter>();
builder.Services.AddScoped<RequestContextAccessor>();
builder.Services.AddScoped<IRequestContext>(provider => provider.GetRequiredService<RequestContextAccessor>());
// The one session source: the access token of the Authorization header, checked against the session row on every request (ticket 04). There is no test door.
builder.Services.AddScoped<ISessionSource, BearerSessionSource>();

var app = builder.Build();
// Fail fast: outside Development/Testing the host does not start without the token signing key (Flow4Tech:Tokens:AppKey). The build-time OpenAPI generator
// (`-p:GenerateContract=true`) loads the host only to read its routes, serves nothing and has no secrets, so the check is skipped for it.
if (Assembly.GetEntryAssembly()?.GetName().Name != "GetDocument.Insider")
{
    _ = app.Services.GetRequiredService<IEmissorDeTokens>();
}

if (proxyConfiavel)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<RouteGuardsMiddleware>();
app.UseMiddleware<RequestContextMiddleware>();
app.UseMiddleware<PermissionMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/version", () => TypedResults.Ok(new VersionInfo(
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")))
    .WithName("getVersion")
    .RequirePermission(SistemaPermissions.VerVersao);

app.MapGet("/health/live", () => TypedResults.Ok(new HealthStatus("live")))
    .WithName("getHealthLive");

app.MapGet("/health/ready", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(timeout.Token);
        return Results.Ok(new HealthStatus("ready", BuildInfo.Commit(configuration)));
    }
    catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or InvalidOperationException or ArgumentException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("getHealthReady")
.Produces<HealthStatus>()
.Produces(StatusCodes.Status503ServiceUnavailable);

app.MapOrganizacao();
app.MapSessao();
app.MapAcesso();
app.MapConfiguracao();
app.MapCardapio();
app.MapCaixa();
app.MapComandas();
app.MapMesas();
app.MapPermissoesDoUsuario(moduleCatalog);

// Fail fast: a route without a catalog permission (or with an unknown one) stops the host from starting.
RoutePermissions.EnsureValid(
    ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints),
    moduleCatalog.ToHashSet(StringComparer.Ordinal));

app.Run();

internal sealed record VersionInfo(string Version);

internal sealed record HealthStatus(string Status, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Commit = null);

internal partial class Program;
