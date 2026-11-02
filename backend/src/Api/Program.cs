using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

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
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or InvalidOperationException or ArgumentException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

internal partial class Program;
