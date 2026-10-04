using System.Net;
using Caravel.IdentitySample;
using Caravel.Queues;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    [Fact]
    public async Task Generated_host_does_not_create_schema_and_explicit_migrations_can_rollback_and_reapply()
    {
        await using var factory = new IdentityFactory();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.False(await factory.Database.ExistsAsync());
        await factory.InitializeAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityContext>();
        await using var queue = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        await queue.GetService<IMigrator>().MigrateAsync("0");
        await identity.GetService<IMigrator>().MigrateAsync("0");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        await identity.Database.MigrateAsync();
        await queue.Database.MigrateAsync();
        Assert.False(identity.Database.HasPendingModelChanges());
        Assert.False(queue.Database.HasPendingModelChanges());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Generated_cookie_session_is_revoked_by_native_security_stamp()
    {
        await using var factory = new IdentityFactory(); await factory.InitializeAsync();
        using var client = factory.Client();
        using var response = await Login(client, "alice", Password);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith(".AspNetCore.Identity.Application="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync("alice"))!)).Succeeded);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }
}
