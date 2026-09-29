using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    private const string NewPassword = "Another-synthetic!Pass456";

    [Fact]
    public async Task Account_mutations_require_authentication_and_antiforgery()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        var change = new { currentPassword = Password, newPassword = NewPassword };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/password", change)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/auth/logout-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/password", change)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        using var unchanged = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(unchanged, "alice", Password)).StatusCode);
    }

    [Fact]
    public async Task Rejected_password_changes_preserve_credentials_and_current_session()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        await Csrf(client);
        foreach (var change in new[]
        {
            new { currentPassword = "Incorrect!Password123", newPassword = NewPassword },
            new { currentPassword = Password, newPassword = "weak" },
            new { currentPassword = Password, newPassword = new string('A', 1025) },
            new { currentPassword = "", newPassword = NewPassword }
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/password", change)).StatusCode);
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        }
        using var unchanged = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(unchanged, "alice", Password)).StatusCode);
    }

    [Fact]
    public async Task Password_change_revokes_other_sessions_and_copied_cookie_and_requires_new_password()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var alice = factory.Client();
        using var otherSession = factory.Client();
        using var copiedCookie = factory.Client();
        using var bob = factory.Client();
        var login = await Login(alice, "alice", Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        copiedCookie.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith(".AspNetCore.Identity.Application=")).Split(';')[0]);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(otherSession, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(bob, "bob", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await copiedCookie.GetAsync("/auth/me")).StatusCode);
        await Csrf(alice);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsJsonAsync("/auth/password", new
        { currentPassword = Password, newPassword = NewPassword, userId = "bob" })).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherSession.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copiedCookie.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(alice, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(alice, "alice", NewPassword)).StatusCode);
        using var bobAgain = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(bobAgain, "bob", Password)).StatusCode);
    }

    [Fact]
    public async Task Logout_all_revokes_only_the_authenticated_account_and_keeps_its_password()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var alice = factory.Client();
        using var otherSession = factory.Client();
        using var bob = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(alice, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(otherSession, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(bob, "bob", Password)).StatusCode);
        await Csrf(alice);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsJsonAsync("/auth/logout-all", new { userId = "bob" })).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherSession.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(alice, "alice", Password)).StatusCode);
    }

    [Fact]
    public async Task Password_changes_share_the_login_quota_without_blocking_session_revocation()
    {
        await using var factory = new IdentityFactory();
        factory.Settings["Caravel:Limits:LoginPermitLimit"] = "2";
        factory.Settings["Caravel:Limits:LoginWindowSeconds"] = "3600";
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        await Csrf(client);
        var change = new { currentPassword = "Incorrect!Password123", newPassword = NewPassword };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/password", change)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/auth/password", change)).StatusCode);
        using var stranger = factory.Client();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(stranger, "unknown", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/auth/logout-all", null)).StatusCode);
    }
}
