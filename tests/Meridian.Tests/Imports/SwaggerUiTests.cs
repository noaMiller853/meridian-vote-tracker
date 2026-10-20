using System.Net;
using System.Text.Json;
using Meridian.Tests.Persistence;

namespace Meridian.Tests.Imports;

/// <summary>The browser UI and the OpenAPI document exist in Development only.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SwaggerUiTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Development_serves_swagger_ui_and_a_document_with_the_user_header_and_the_file_upload()
    {
        await using var api = await TestApi.StartAsync(fixture, environment: "Development");
        using var client = api.Client(user: null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var scheme = doc.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("X-User");
        Assert.Equal(("apiKey", "header", "X-User"),
            (scheme.GetProperty("type").GetString(), scheme.GetProperty("in").GetString(), scheme.GetProperty("name").GetString()));
        Assert.True(doc.RootElement.GetProperty("security")[0].TryGetProperty("X-User", out _));

        // No "servers": inside Docker it would be Kestrel's own address (http://[::]:8080), which a browser cannot
        // reach, so "Try it out" and the generated client must use the address the page came from.
        Assert.False(doc.RootElement.TryGetProperty("servers", out var servers) && servers.GetArrayLength() > 0);

        var upload = doc.RootElement.GetProperty("paths").GetProperty("/api/imports").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data")
            .GetProperty("schema").GetProperty("properties").GetProperty("file");
        Assert.Equal(("string", "binary"), (upload.GetProperty("type").GetString(), upload.GetProperty("format").GetString()));
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task Other_environments_serve_neither_the_ui_nor_the_document(string environment)
    {
        await using var api = await TestApi.StartAsync(fixture, environment: environment);
        using var client = api.Client(user: null);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }
}
