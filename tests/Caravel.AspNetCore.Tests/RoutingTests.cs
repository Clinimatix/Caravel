using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caravel.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Validation;
using Xunit;

namespace Caravel.AspNetCore.Tests;

public sealed class RoutingTests
{
    [Fact]
    public async Task HostConfigurationLoadsDotEnvBetweenJsonAndCommandLine()
    {
        var directory = Directory.CreateTempSubdirectory("caravel-host-config-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), "{\"Caravel\":{\"Name\":\"settings\"},\"DotEnvOnly\":\"settings\"}");
            File.WriteAllText(Path.Combine(directory.FullName, ".env"), "APP_NAME=dotenv\nDotEnvOnly=dotenv\nCustomPriority=dotenv");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = directory.FullName, Args = ["--APP_NAME=command-line"]
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CustomPriority"] = "custom" });
            builder.AddCaravel();
            await using var app = builder.Build();
            Assert.Equal("command-line", app.Configuration["Caravel:Name"]);
            Assert.Equal("dotenv", app.Configuration["DotEnvOnly"]);
            Assert.Equal("custom", app.Configuration["CustomPriority"]);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task NativeRoutesGroupsBindingAndProviderBootWorkTogether()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.AddCaravel(options => options.AddProvider<StartupProvider>());
        builder.Services.AddValidation();
        builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = true);
        builder.Services.AddHostedService<BootObserver>();
        await using var app = builder.Build();
        app.UseCaravel();
        app.Routes(routes =>
        {
            routes.Get("/", (StartupState state) => state.Booted ? "ready" : "not ready").Name("home");
            routes.Group("/users", users => users.Get("/{id:int}", (int id) => new { id }).Name("users.show"));
            routes.Post("/users", (CreateUser request) => Results.Ok(request)).Name("users.store");
            routes.Get("/failure", () => ThrowUnexpected()).Name("failure");
        });
        await app.StartAsync();
        var client = app.GetTestClient();
        Assert.Equal("ready", await client.GetStringAsync("/"));
        Assert.Equal("{\"id\":42}", await client.GetStringAsync("/users/42"));
        var valid = await client.PostAsJsonAsync("/users", new CreateUser("somebody@example.com"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var invalid = await client.PostAsJsonAsync("/users", new CreateUser("bad-email"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        var failure = await client.GetAsync("/failure");
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.DoesNotContain("private failure detail", await failure.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", failure.Headers.GetValues("X-Content-Type-Options").Single());
        var route = Assert.Single(app.GetCaravelRoutes(), route => route.Name == "users.show");
        Assert.Equal("/users/{id:int}", route.Path);
        Assert.Equal(new[] { "GET" }, route.Methods);
    }

    [Fact]
    public async Task RouteExportDoesNotStartHostOrBootProvidersAndCannotOverwrite()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddCaravel(options => options.AddProvider<StartupProvider>());
        await using var app = builder.Build();
        app.UseCaravel();
        app.Routes(routes => routes.Get("/", () => "hello").Name("home"));
        var directory = Directory.CreateTempSubdirectory("caravel-routes-");
        var file = Path.Combine(directory.FullName, "routes.json");
        try
        {
            Assert.True(await app.ExportCaravelRoutesAsync(["--caravel-routes-json", file]));
            Assert.False(app.Services.GetRequiredService<StartupState>().Booted);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal("home", json.RootElement[0].GetProperty("name").GetString());
            await Assert.ThrowsAsync<IOException>(() => app.ExportCaravelRoutesAsync(["--caravel-routes-json", file]));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExplicitAsyncStartupBootsBeforeRoutesAndOnlyOnce()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddCaravel(options => options.AddProvider<StartupProvider>());
        await using var app = builder.Build();
        await app.UseCaravelAsync();
        Assert.True(app.Services.GetRequiredService<StartupState>().Booted);
        await app.StartAsync();
        Assert.Equal(1, app.Services.GetRequiredService<StartupState>().BootCount);
    }

    private static string ThrowUnexpected() => throw new InvalidOperationException("private failure detail");
    // .NET 10 requires explicit discovery for DTOs hidden behind a routing facade.
#pragma warning disable ASP0029
    [ValidatableType]
#pragma warning restore ASP0029
    public sealed record CreateUser([property: Required, EmailAddress] string Email);
    public sealed class StartupState { public bool Booted { get; set; } public int BootCount { get; set; } }
    public sealed class BootObserver(StartupState state) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Assert.True(state.Booted);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    public sealed class StartupProvider : Caravel.Core.ServiceProvider
    {
        public override void Register(IServiceCollection services) => services.AddSingleton<StartupState>();
        public override async ValueTask BootAsync(CaravelApplication app, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            var state = app.Services.GetRequiredService<StartupState>();
            state.Booted = true;
            state.BootCount++;
        }
    }
}
