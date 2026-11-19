using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.AspNetCore.Tests;

// A composition example, not a credential provider or production authentication scheme.
public sealed class HostingProfileTests
{
    [Theory]
    [InlineData("development")]
    [InlineData("managed-identity")]
    public async Task CredentialSelectionPreservesRoutesAndDoesNotGrantEdit(string profile)
    {
        await using var app = Create(profile, "read-edit", "synthetic-setting");
        await app.StartAsync();
        using var client = app.GetTestClient();
        var mounted = await client.GetFromJsonAsync<MountedFeatures>("/service/capabilities");
        Assert.Equal(new MountedFeatures(true, true), mounted);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/service/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/service/items", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Edit", "true");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/service/items", null)).StatusCode);
        var routes = app.GetCaravelRoutes();
        Assert.Contains(routes, route => route.Name == "service.read");
        Assert.Contains(routes, route => route.Name == "service.edit");
    }

    [Theory]
    [InlineData("development")]
    [InlineData("managed-identity")]
    public async Task ExplicitReadOnlyHasNoEditCapabilityOrRoute(string profile)
    {
        await using var app = Create(profile, "read-only", "synthetic-setting");
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Edit", "true");
        Assert.Equal(new MountedFeatures(true, false),
            await client.GetFromJsonAsync<MountedFeatures>("/service/capabilities"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/service/items")).StatusCode);
        Assert.DoesNotContain(app.GetCaravelRoutes(), route => route.Name == "service.edit");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/service/items", null)).StatusCode);
    }

    [Theory]
    [InlineData("development", "read-edit", null)]
    [InlineData("managed-identity", "read-edit", " ")]
    [InlineData("unexpected", "read-edit", "synthetic-setting")]
    [InlineData("development", "unexpected", "synthetic-setting")]
    public void InvalidConfigurationFailsInsteadOfSilentlyOmittingRoutes(
        string profile, string mode, string? adapterSetting)
    {
        var builder = WebApplication.CreateBuilder();
        Assert.Throws<InvalidOperationException>(() =>
            AddServiceFeatures(builder.Services, profile, mode, adapterSetting));
    }

    private static WebApplication Create(string profile, string mode, string? setting)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddCaravel();
        AddServiceFeatures(builder.Services, profile, mode, setting);
        builder.Services.AddAuthentication("synthetic")
            .AddScheme<AuthenticationSchemeOptions, SyntheticAuthenticationHandler>("synthetic", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("service.edit", policy => policy.RequireAuthenticatedUser().RequireClaim("edit", "true")));
        var app = builder.Build();
        app.UseCaravel();
        app.UseAuthentication();
        app.UseAuthorization();
        MapServiceFeatures(app);
        return app;
    }

    private static void AddServiceFeatures(IServiceCollection services,
        string profile, string mode, string? adapterSetting)
    {
        if (profile is not ("development" or "managed-identity"))
            throw new InvalidOperationException("Choose a supported credential profile.");
        if (mode is not ("read-edit" or "read-only"))
            throw new InvalidOperationException("Choose an explicit feature mode.");
        if (string.IsNullOrWhiteSpace(adapterSetting))
            throw new InvalidOperationException("Required adapter setting is missing.");
        // Substitute only the adapter. Both profiles share registration and mapping.
        services.AddSingleton(new ExampleAdapter(profile));
        services.AddSingleton(new MountedFeatures(true, mode == "read-edit"));
    }

    private static void MapServiceFeatures(WebApplication app)
    {
        var mounted = app.Services.GetRequiredService<MountedFeatures>();
        app.Routes(routes =>
        {
            routes.Get("/service/capabilities", () => mounted)
                .Name("service.capabilities").RequireAuthorization();
            routes.Get("/service/items", (ExampleAdapter adapter) => Results.Ok(new[] { "example" }))
                .Name("service.read").RequireAuthorization();
            if (mounted.Edit)
                routes.Post("/service/items", () => Results.NoContent())
                    .Name("service.edit").RequireAuthorization("service.edit");
        });
    }

    // Test-only scheme: never trust these request headers in production.
    private sealed class SyntheticAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-Anonymous"] == "true")
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = Request.Headers["X-Test-Unsigned"] == "true"
                ? new ClaimsIdentity() : new ClaimsIdentity(Scheme.Name);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "example-user"));
            if (Request.Headers["X-Test-Edit"] == "true")
                identity.AddClaim(new Claim("edit", "true"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    [Theory]
    [InlineData("/service/capabilities")]
    [InlineData("/service/items")]
    public async Task AnonymousRequestsCannotReadMountedCapabilitiesOrItems(string path)
    {
        await using var app = Create("development", "read-edit", "synthetic-setting");
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("X-Test-Anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("X-Test-Unsigned", HttpStatusCode.Forbidden)]
    public async Task UnauthenticatedEditClaimCannotAuthorizeWrite(string header, HttpStatusCode expected)
    {
        await using var app = Create("development", "read-edit", "synthetic-setting");
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(header, "true");
        client.DefaultRequestHeaders.Add("X-Test-Edit", "true");
        Assert.Equal(expected,
            (await client.PostAsync("/service/items", null)).StatusCode);
    }

    public sealed record MountedFeatures(bool Read, bool Edit);
    private sealed record ExampleAdapter(string Profile);
}
