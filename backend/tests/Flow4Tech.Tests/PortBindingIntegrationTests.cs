using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

/// <summary>Starts the real published host as a process, the way Railway does: PORT and DATABASE_URL injected, nothing else.</summary>
public sealed class PortBindingIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task The_host_listens_on_the_injected_PORT()
    {
        var port = FreePort();
        using var process = StartHost(("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        try
        {
            var response = await GetWhenUp(new Uri($"http://127.0.0.1:{port}/health/live"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Readiness_works_from_a_railway_style_DATABASE_URL()
    {
        var b = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        var databaseUrl = $"postgres://{Uri.EscapeDataString(b.Username!)}:{Uri.EscapeDataString(b.Password!)}@{b.Host}:{b.Port}/{b.Database}";
        var port = FreePort();
        using var process = StartHost(("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("DATABASE_URL", databaseUrl));
        try
        {
            var response = await GetWhenUp(new Uri($"http://127.0.0.1:{port}/health/ready"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task An_invalid_DATABASE_URL_stops_the_host_without_echoing_the_secret()
    {
        using var process = StartHost(("PORT", FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture)), ("DATABASE_URL", "postgres://user:TOPSECRET@:notaport/db"));
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("The database URL is invalid", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TOPSECRET", output, StringComparison.Ordinal);
    }

    private static Process StartHost(params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Flow4Tech.Api.dll")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DOTNET_URLS", StringComparison.OrdinalIgnoreCase) || k is "PORT" or "DATABASE_URL" || k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        return Process.Start(start)!;
    }

    private static async Task<HttpResponseMessage> GetWhenUp(Uri uri)
    {
        using var http = new HttpClient();
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                return await http.GetAsync(uri);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(250);
            }
        }

        throw new TimeoutException("host did not start listening");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
