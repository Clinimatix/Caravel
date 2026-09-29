using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caravel.Auth.Windows.Tests;

public sealed class WindowsAuthenticationTests
{
    [Fact]
    public async Task NativeRegistrationRequiresAuthenticationByDefault()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCaravelWindowsAuthentication();
        await using var app = builder.Build();

        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(NegotiateDefaults.AuthenticationScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal(NegotiateDefaults.AuthenticationScheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        Assert.Equal(typeof(NegotiateHandler), (await schemes.GetSchemeAsync(NegotiateDefaults.AuthenticationScheme))?.HandlerType);
        Assert.Null(app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value.DefaultSignInScheme);
        var policy = await app.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();
        Assert.NotNull(policy);
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, policy)).Succeeded);
        Assert.Equal(CookieSecurePolicy.Always, app.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task NativeNegotiateOptionsAndRolePoliciesRemainConfigurable()
    {
        var builder = WebApplication.CreateBuilder();
        Func<AuthenticatedContext, Task> callback = _ => Task.CompletedTask;
        builder.Services.AddCaravelWindowsAuthentication(options =>
            options.Events = new NegotiateEvents { OnAuthenticated = callback });
        builder.Services.AddAuthorizationBuilder().AddPolicy("Operators", policy =>
            policy.RequireAuthenticatedUser().RequireRole(@"EXAMPLE\CaravelOperators"));
        await using var app = builder.Build();

        var events = app.Services.GetRequiredService<IOptionsMonitor<NegotiateOptions>>()
            .Get(NegotiateDefaults.AuthenticationScheme).Events;
        Assert.NotNull(events);
        Assert.Same(callback, events.OnAuthenticated);
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();
        // Synthetic claims verify policy behavior only, not a domain/Kerberos authentication exchange.
        var user = new ClaimsPrincipal(new ClaimsIdentity([], "synthetic"));
        Assert.False((await authorization.AuthorizeAsync(user, null, "Operators")).Succeeded);
        var member = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, @"EXAMPLE\CaravelOperators")], "synthetic"));
        Assert.True((await authorization.AuthorizeAsync(member, null, "Operators")).Succeeded);
    }
}
