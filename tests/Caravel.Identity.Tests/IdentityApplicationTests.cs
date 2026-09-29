using System.Net;
using System.Net.Http.Json;
using Caravel.Events;
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

    [Fact]
    public async Task Login_policies_cookie_flags_and_logout_use_native_identity_and_antiforgery()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/login", new { userName = "alice@example.invalid", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "alice", "wrong-password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "unconfirmed", Password)).StatusCode);
        var login = await Login(client, "alice", Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(".AspNetCore.Identity.Application="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin")).StatusCode);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);

        using var admin = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(admin, "admin", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Failed_passwords_lock_account_and_security_stamp_revokes_existing_cookie()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "alice", "wrong-password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "alice", Password)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True(await users.IsLockedOutAsync((await users.FindByNameAsync("alice@example.invalid"))!));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "bob", Password)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByNameAsync("bob@example.invalid"))!)).Succeeded);
        }
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Cookie_expires_after_fixed_lifetime_even_after_authenticated_activity()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Notes_validate_input_and_enforce_owner_on_reads_and_writes_with_scoped_events()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var alice = factory.Client();
        using var bob = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(alice, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(bob, "bob", Password)).StatusCode);
        await Csrf(alice);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/notes/", new { text = new string('x', 501) })).StatusCode);
        var created = await alice.PostAsJsonAsync("/notes/", new { text = "Synthetic note", ownerId = "bob" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = (await created.Content.ReadFromJsonAsync<NoteResponse>())!;
        Assert.Equal(note.Id, Assert.Single(factory.Events));
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/notes/{note.Id}")).StatusCode);
        await Csrf(bob);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/notes/{note.Id}", new { text = "Stolen" })).StatusCode);
        alice.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync($"/notes/{note.Id}", new { text = "No CSRF" })).StatusCode);
        await Csrf(alice);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync($"/notes/{note.Id}", new { text = "Updated" })).StatusCode);
        Assert.Equal("Updated", (await alice.GetFromJsonAsync<NoteResponse>($"/notes/{note.Id}"))!.Text);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
        var stored = await db.Notes.SingleAsync();
        Assert.Equal("alice", stored.OwnerId);
        Assert.NotEqual(default, stored.CreatedAt);
        Assert.True(stored.UpdatedAt >= stored.CreatedAt);
        Assert.False(db.Database.HasPendingModelChanges());
    }

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
    private sealed record NoteResponse(Guid Id, string Text);

    private sealed class IdentityFactory : WebApplicationFactory<Program>
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("caravel-identity-");
        public TestClock Clock { get; } = new();
        public List<Guid> Events { get; } = [];
        public Dictionary<string, string> Settings { get; } = [];
        public Action<IServiceCollection>? ExtraServices { get; set; }
        public string DatabasePath => Path.Combine(directory.FullName, "synthetic.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Caravel:IdentityDatabase", DatabasePath);
            builder.UseSetting("Caravel:RunWorker", "false");
            foreach (var setting in Settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddSingleton<TimeProvider>(Clock);
                services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.TimeProvider = Clock);
                services.Configure<SecurityStampValidatorOptions>(options => options.TimeProvider = Clock);
                services.AddSingleton(Events);
                services.AddEventListener<NoteCreated, ObserveNote>();
                ExtraServices?.Invoke(services);
            });
        }

        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        public async Task InitializeAsync(bool initializeQueue = true, string? identityMigration = null)
        {
            await using var scope = Services.CreateAsyncScope();
            var identityDb = scope.ServiceProvider.GetRequiredService<IdentityContext>();
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    public sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }

    public sealed class ObserveNote(IdentityContext db, List<Guid> events) : IEventListener<NoteCreated>
    {
        public Task HandleAsync(NoteCreated message, CancellationToken cancellationToken)
        {
            // The listener sees the same scoped context and a saved entity, not a root/new context.
            var entry = Assert.Single(db.ChangeTracker.Entries<Note>());
            Assert.Equal(EntityState.Unchanged, entry.State);
            Assert.Equal(message.NoteId, entry.Entity.Id);
            events.Add(message.NoteId);
            return Task.CompletedTask;
        }
    }
}
