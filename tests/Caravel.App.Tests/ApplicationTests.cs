using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class ApplicationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;

    public ApplicationTests(WebApplicationFactory<Program> factory)
        => client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    [Fact]
    public async Task Application_routes_bind_validate_and_return_standard_errors()
    {
        Assert.Equal("Hello from Clinimatix Caravel", await client.GetStringAsync("/"));
        var valid = await client.PostAsJsonAsync("/api/greetings", new { name = "Rhett" });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Contains("Hello, Rhett", await valid.Content.ReadAsStringAsync());
        var invalid = await client.PostAsJsonAsync("/api/greetings", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        var missing = await client.GetAsync("/not-found");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", missing.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Development_document_uses_native_OpenApi_31()
    {
        var document = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/openapi/v1.json");
        Assert.StartsWith("3.1", document.GetProperty("openapi").GetString());
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/greetings", out _));
    }
}
