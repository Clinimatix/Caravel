using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caravel.Auth.Tests;

public sealed class IdentityTests
{
    [Fact]
    public async Task IdentityUsesNativeSchemesAndSecureDefaultsWithoutChoosingAStore()
    {
        var builder = WebApplication.CreateBuilder();
        var identity = builder.Services.AddCaravelIdentity<ApplicationUser>();
        Assert.Equal(typeof(ApplicationUser), identity.UserType);
        Assert.Null(identity.RoleType);
        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(IUserStore<ApplicationUser>));
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(SignInManager<ApplicationUser>));
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(ISecurityStampValidator));
        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
        Assert.True(options.SignIn.RequireConfirmedEmail);
        Assert.True(options.User.RequireUniqueEmail);
        Assert.Equal(12, options.Password.RequiredLength);
        Assert.True(options.Lockout.AllowedForNewUsers);
        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Lockout.DefaultLockoutTimeSpan);
        Assert.Contains(TokenOptions.DefaultProvider, options.Tokens.ProviderMap.Keys);
        Assert.Equal(TimeSpan.Zero, app.Services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval);

        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(IdentityConstants.ApplicationScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal(IdentityConstants.ApplicationScheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        Assert.Equal(IdentityConstants.ExternalScheme, (await schemes.GetDefaultSignInSchemeAsync())?.Name);
        var cookies = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        foreach (var scheme in await schemes.GetAllSchemesAsync())
        {
            var cookie = cookies.Get(scheme.Name);
            Assert.True(cookie.Cookie.HttpOnly);
            Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
            Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
        }
        var applicationCookie = cookies.Get(IdentityConstants.ApplicationScheme);
        Assert.Equal(TimeSpan.FromHours(8), applicationCookie.ExpireTimeSpan);
        Assert.False(applicationCookie.SlidingExpiration);
        Assert.Equal((Func<CookieValidatePrincipalContext, Task>)SecurityStampValidator.ValidatePrincipalAsync,
            applicationCookie.Events.OnValidatePrincipal);
        Assert.Equal(CookieSecurePolicy.Always, app.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task NativeConfigurationAndOptionalRolesRemainAvailable()
    {
        var builder = WebApplication.CreateBuilder();
        var identity = builder.Services.AddCaravelIdentity<ApplicationUser>(options =>
        {
            options.Password.RequiredLength = 16;
            options.Lockout.MaxFailedAccessAttempts = 3;
        }).AddRoles<ApplicationRole>();
        builder.Services.ConfigureApplicationCookie(options => options.ExpireTimeSpan = TimeSpan.FromMinutes(20));
        builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
        await using var app = builder.Build();

        Assert.Equal(typeof(ApplicationRole), identity.RoleType);
        var options = app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
        Assert.Equal(16, options.Password.RequiredLength);
        Assert.Equal(3, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(20), app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).ExpireTimeSpan);
        Assert.Equal(TimeSpan.FromMinutes(1), app.Services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval);
    }

    // Arbitrary user/role classes ensure the package does not impose Identity's default string key or EF store.
    public sealed class ApplicationUser;
    public sealed class ApplicationRole;
}
