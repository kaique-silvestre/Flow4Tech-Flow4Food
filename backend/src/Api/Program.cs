using Flow4Tech.Api;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Organizacao.Infrastructure;
using Flow4Tech.Permissoes.Application;
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
builder.Services.AddOpenApi();
builder.Services.AddTenantData(provider => provider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddAuditoria();
builder.Services.AddOrganizacao();
// Identidade (ticket 03): accounts, passwords, pre-authentication. No route uses it yet (login arrives with the sessions, ticket 04).
// Flow4Tech:Identidade:TempoMinimoDeLoginMs (default 400) is the response-time floor of a login attempt; Flow4Tech:Identidade:Hibp:Enabled turns on the k-anonymity breach check.
builder.Services.AddIdentidade(options =>
{
    options.TempoMinimoDeLogin = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Flow4Tech:Identidade:TempoMinimoDeLoginMs", 400));
    options.UsarHibp = builder.Configuration.GetValue("Flow4Tech:Identidade:Hibp:Enabled", false);
});
builder.Services.AddScoped<RequestContextAccessor>();
builder.Services.AddScoped<IRequestContext>(provider => provider.GetRequiredService<RequestContextAccessor>());
// Session source (resolved lazily, after all configuration is loaded): the test session only in Development/Testing with a configured key
// (ticket 01 decision, see TestSession); otherwise none until ticket 04.
builder.Services.AddSingleton<ISessionSource>(provider =>
    TestSession.IsEnabled(provider.GetRequiredService<IHostEnvironment>(), provider.GetRequiredService<IConfiguration>())
        ? new SignedHeaderSessionSource(provider.GetRequiredService<IConfiguration>()[TestSession.KeyConfiguration]!)
        : new NoSessionSource());

var app = builder.Build();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<RequestContextMiddleware>();

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

// Fail fast: a route without a catalog permission (or with an unknown one) stops the host from starting.
RoutePermissions.EnsureValid(
    ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints),
    ModuleCatalog.Load(AppContext.BaseDirectory).ToHashSet(StringComparer.Ordinal));

app.Run();

internal sealed record VersionInfo(string Version);

internal sealed record HealthStatus(string Status, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Commit = null);

internal partial class Program;
