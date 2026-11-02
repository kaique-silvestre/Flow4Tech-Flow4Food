using Npgsql;

using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/version", () => TypedResults.Ok(new VersionInfo(
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")))
    .WithName("getVersion");

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
        return Results.Ok(new HealthStatus("ready"));
    }
    catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or InvalidOperationException or ArgumentException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("getHealthReady")
.Produces<HealthStatus>()
.Produces(StatusCodes.Status503ServiceUnavailable);

app.Run();

internal sealed record VersionInfo(string Version);

internal sealed record HealthStatus(string Status);

internal partial class Program;
