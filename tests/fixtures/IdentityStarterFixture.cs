using System.Net;
using System.Net.Http.Json;
using Caravel.IdentitySample;
using Caravel.Queues;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    private const string Password = "Synthetic-only!Pass123";

    private static async Task<HttpResponseMessage> Login(HttpClient client, string user, string password)
    {
        await Csrf(client);
        return await client.PostAsJsonAsync("/auth/login", new { userName = $"{user}@example.invalid", password });
    }

    private static async Task Csrf(HttpClient client)
    {
        var tokens = (await client.GetFromJsonAsync<CsrfResponse>("/auth/csrf"))!;
        client.DefaultRequestHeaders.Remove(tokens.HeaderName);
        client.DefaultRequestHeaders.Add(tokens.HeaderName, tokens.Token);
    }

    private sealed record CsrfResponse(string Token, string HeaderName);

    private sealed class IdentityFactory(IdentityTestDatabase? sharedDatabase = null, TestClock? clock = null) : WebApplicationFactory<Program>
    {
        public IdentityTestDatabase Database { get; } = sharedDatabase ?? new IdentityTestDatabase();
        public TestClock Clock { get; } = clock ?? new();
        public Dictionary<string, string> Settings { get; } = [];
        public Action<IServiceCollection>? ExtraServices { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Caravel:IdentityDatabase", Database.ConfigurationValue);
            builder.UseSetting("Caravel:DatabaseProvider", Database.Provider);
            builder.UseSetting("Caravel:RunWorker", "false");
            foreach (var setting in Settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddSingleton<TimeProvider>(Clock);
                services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.TimeProvider = Clock);
                services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options => options.TimeProvider = Clock);
                services.Configure<SecurityStampValidatorOptions>(options => options.TimeProvider = Clock);
                ExtraServices?.Invoke(services);
            });
        }

        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        public async Task InitializeAsync(bool initializeQueue = true, string? identityMigration = null)
        {
            await Database.CreateAsync();
            await using var scope = Services.CreateAsyncScope();
            var identityDb = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            Assert.False(identityDb.Database.HasPendingModelChanges(), "Generate migrations for the selected provider before running the packaged backend tests.");
            if (identityMigration is not null)
                identityMigration = identityDb.Database.GetMigrations().Single(migration => migration.EndsWith("_" + identityMigration, StringComparison.Ordinal));
            await identityDb.GetService<IMigrator>().MigrateAsync(identityMigration);
            if (initializeQueue)
            {
                await using var queueDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
                await queueDb.Database.MigrateAsync();
                Assert.False(queueDb.Database.HasPendingModelChanges());
            }
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            Assert.True((await roles.CreateAsync(new IdentityRole("Administrator"))).Succeeded);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            foreach (var name in new[] { "alice", "bob", "admin", "unconfirmed" })
            {
                var user = new IdentityUser { Id = name, UserName = $"{name}@example.invalid", Email = $"{name}@example.invalid", EmailConfirmed = name != "unconfirmed" };
                Assert.True((await users.CreateAsync(user, Password)).Succeeded);
                if (name == "admin") Assert.True((await users.AddToRoleAsync(user, "Administrator")).Succeeded);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (sharedDatabase is null) await Database.DisposeAsync();
        }
    }

    public sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }

}
