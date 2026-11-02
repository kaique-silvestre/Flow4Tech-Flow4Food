using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Flow4Tech.Tests;

public sealed class VersionEndpointsTests
{
    [Fact]
    public async Task Version_returns_the_api_version()
    {
        await using var api = new WebApplicationFactory<Program>();
        using var client = api.CreateClient();

        var response = await client.GetAsync(new Uri("/version", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body["version"]));
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_lists_the_version_operation()
    {
        await using var api = new WebApplicationFactory<Program>();
        using var client = api.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var get = document?["paths"]?["/version"]?["get"];
        Assert.Equal("getVersion", (string?)get?["operationId"]);
        var schemaRef = (string?)get?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"];
        Assert.Equal("#/components/schemas/VersionInfo", schemaRef);
        Assert.NotNull(document?["components"]?["schemas"]?["VersionInfo"]?["properties"]?["version"]);
    }
}
