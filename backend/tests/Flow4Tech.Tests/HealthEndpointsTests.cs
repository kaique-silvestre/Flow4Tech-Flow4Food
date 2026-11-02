using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Flow4Tech.Tests;

public sealed class HealthEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    Task IAsyncLifetime.DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static ApiFactory CreateApi(string? connectionString) => new(connectionString);

    [Fact]
    public async Task Liveness_returns_ok_without_a_database()
    {
        await using var api = CreateApi(connectionString: null);
        using var client = api.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_returns_ok_when_database_answers()
    {
        await using var api = CreateApi(_postgres.GetConnectionString());
        using var client = api.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_when_database_is_unreachable()
    {
        await using var api = CreateApi("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1");
        using var client = api.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_when_database_is_stopped()
    {
        await using var api = CreateApi(_postgres.GetConnectionString());
        using var client = api.CreateClient();
        await _postgres.StopAsync();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_when_connection_string_is_missing()
    {
        await using var api = CreateApi(connectionString: null);
        using var client = api.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class ApiFactory(string? connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                }));
    }
}
