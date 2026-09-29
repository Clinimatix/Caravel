using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    [Fact]
    public async Task Two_factor_account_cannot_sign_in_with_password_only()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var alice = (await users.FindByNameAsync("alice@example.invalid"))!;
            Assert.True((await users.SetTwoFactorEnabledAsync(alice, true)).Succeeded);
            Assert.NotEmpty(await users.GetValidTwoFactorProvidersAsync(alice));
        }

        using var client = factory.Client();
        var response = await Login(client, "alice", Password);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        // A native continuation cookie proves the correct password reached the two-factor branch.
        Assert.Contains(cookies, cookie => cookie.StartsWith(IdentityConstants.TwoFactorUserIdScheme + "=", StringComparison.Ordinal));
        Assert.DoesNotContain(cookies, cookie => cookie.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Existing_cookies_reflect_role_removal_and_account_deletion()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var admin = factory.Client();
        using var bob = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(admin, "admin", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(bob, "bob", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/auth/me")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var account = (await users.FindByNameAsync("admin@example.invalid"))!;
            Assert.True((await users.RemoveFromRoleAsync(account, "Administrator")).Succeeded);
        }
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/auth/me")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.DeleteAsync((await users.FindByNameAsync("bob@example.invalid"))!)).Succeeded);
        }
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(bob, "bob", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/auth/me")).StatusCode);
    }
}
