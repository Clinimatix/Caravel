using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Caravel.AspNetCore.Tests;

// Application-owned, synthetic wire contract. No real messages, provider or browser are involved.
public sealed class ThreadReloadContractTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FreshReloadPreservesRootAndReplyWithEitherRootNullEncoding(bool omitNull)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.AddCaravel();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.DefaultIgnoreCondition =
            omitNull ? JsonIgnoreCondition.WhenWritingNull : JsonIgnoreCondition.Never);
        await using var app = builder.Build();
        app.UseCaravel();
        ThreadItem? root = null;
        var replies = new List<ThreadItem>();
        app.Routes(routes =>
        {
            routes.Post("/thread", () =>
            {
                root = new(Guid.NewGuid(), "root", null, "synthetic root");
                return Results.Created("/thread", root);
            });
            routes.Get("/thread", () => Results.Ok(new[] { root! }.Concat(replies).ToArray()));
            routes.Post("/thread/reply", (ReplyRequest input) =>
            {
                if (!Guid.TryParseExact(input.ParentId, "D", out var parent) || root?.Id != parent)
                    return Results.BadRequest();
                var reply = new ThreadItem(Guid.NewGuid(), "reply", parent, "synthetic reply");
                replies.Add(reply);
                return Results.Created("/thread", reply);
            });
        });
        await app.StartAsync();

        using (var createClient = app.GetTestClient())
        using (var created = await createClient.PostAsync("/thread", null))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // No state or response body from creation is carried into reload.
        using var freshClient = app.GetTestClient();
        var firstJson = await freshClient.GetStringAsync("/thread");
        output.WriteLine("Root reload: " + firstJson);
        using var first = JsonDocument.Parse(firstJson);
        var firstWire = Assert.Single(first.RootElement.EnumerateArray());
        Assert.Equal(!omitNull, firstWire.TryGetProperty("parentId", out var parentWire));
        if (!omitNull) Assert.Equal(JsonValueKind.Null, parentWire.ValueKind);
        var reloadedRoot = ReadItem(firstWire);
        Assert.Equal("root", reloadedRoot.Kind);
        Assert.Null(reloadedRoot.ParentId);

        using var rejected = await freshClient.PostAsJsonAsync("/thread/reply", new ReplyRequest("malformed"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var accepted = await freshClient.PostAsJsonAsync("/thread/reply", new ReplyRequest(reloadedRoot.Id.ToString("D")));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        using var secondClient = app.GetTestClient();
        var secondJson = await secondClient.GetStringAsync("/thread");
        output.WriteLine("Root and reply reload: " + secondJson);
        using var second = JsonDocument.Parse(secondJson);
        var items = second.RootElement.EnumerateArray().Select(ReadItem).ToArray();
        Assert.Collection(items, item => Assert.Equal(reloadedRoot, item), reply =>
        {
            Assert.Equal("reply", reply.Kind);
            Assert.NotEqual(reloadedRoot.Id, reply.Id);
            Assert.Equal(reloadedRoot.Id, reply.ParentId);
            Assert.Equal("synthetic reply", reply.Text);
        });
    }

    [Theory]
    [InlineData("reply", "")]
    [InlineData("reply", "\"parentId\":null,")]
    [InlineData("reply", "\"parentId\":\"malformed\",")]
    [InlineData("reply", "\"parentId\":7,")]
    [InlineData("root", "\"parentId\":\"a5dda17b-37ed-4d21-95eb-2e3c2947f1ab\",")]
    public void ConsumerRejectsInvalidParentWithoutLosingRootNullCompatibility(string kind, string parentProperty)
    {
        using var document = JsonDocument.Parse($$"""
            {"kind":"{{kind}}",{{parentProperty}}"id":"a49d5dcb-39e2-46e9-9683-5f78abb0321b","text":"synthetic"}
            """);
        Assert.Throws<JsonException>(() => ReadItem(document.RootElement));
    }

    [Fact]
    public void ConsumerStillRequiresUnrelatedFields()
    {
        using var document = JsonDocument.Parse("""
            {"kind":"root","id":"a49d5dcb-39e2-46e9-9683-5f78abb0321b"}
            """);
        Assert.Throws<JsonException>(() => ReadItem(document.RootElement));
    }

    private static ThreadItem ReadItem(JsonElement wire)
    {
        var kind = RequiredString(wire, "kind");
        var id = ParseId(RequiredString(wire, "id"));
        var text = RequiredString(wire, "text");
        var hasParent = wire.TryGetProperty("parentId", out var parent);
        return kind switch
        {
            "root" when !hasParent || parent.ValueKind == JsonValueKind.Null => new(id, kind, null, text),
            "reply" when hasParent && parent.ValueKind == JsonValueKind.String => new(id, kind, ParseId(parent.GetString()!), text),
            _ => throw new JsonException("Invalid thread parent."),
        };
    }

    private static string RequiredString(JsonElement wire, string name) =>
        wire.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new JsonException($"Required field: {name}.");

    private static Guid ParseId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? id : throw new JsonException("Invalid thread identifier.");

    public sealed record ThreadItem(Guid Id, string Kind, Guid? ParentId, string Text);
    public sealed record ReplyRequest(string? ParentId);
}
