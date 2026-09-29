using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    [Fact]
    public async Task Kestrel_rejects_large_declared_and_chunked_requests_before_login()
    {
        await using var factory = new IdentityFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        var address = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri(address), AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromSeconds(15);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        foreach (var chunked in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            { Content = JsonContent.Create(new { userName = "synthetic", password = "unused", ignored = new string('x', 17000) }) };
            // Rejection can close an HTTP/1.1 connection with an unread request body.
            request.Headers.ConnectionClose = true;
            if (chunked) request.Headers.TransferEncodingChunked = true;
            else await request.Content.LoadIntoBufferAsync();
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }
}
