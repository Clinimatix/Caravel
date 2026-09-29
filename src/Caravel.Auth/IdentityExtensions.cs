using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Caravel.Auth;

public static class IdentityExtensions
{
    /// <summary>
    /// Adds native Identity services and HTTPS-only cookies. Chain a user store and optional roles
    /// on the returned builder. This method does not map endpoints or provision accounts.
    /// </summary>
    /// <remarks>
    /// Password endpoints must enable lockoutOnFailure and validate antiforgery tokens.
    /// Security stamps are checked on each authenticated request, which reads the user store.
    /// Override settings using native Identity, cookie and SecurityStampValidator options.
    /// </remarks>
    public static IdentityBuilder AddCaravelIdentity<TUser>(this IServiceCollection services,
        Action<IdentityOptions>? configure = null) where TUser : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        }).AddIdentityCookies();

        var identity = services.AddIdentityCore<TUser>(options =>
        {
            options.SignIn.RequireConfirmedEmail = true;
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            configure?.Invoke(options);
        }).AddSignInManager().AddDefaultTokenProviders();

        foreach (var scheme in new[]
        {
            IdentityConstants.ApplicationScheme, IdentityConstants.ExternalScheme,
            IdentityConstants.TwoFactorRememberMeScheme, IdentityConstants.TwoFactorUserIdScheme
        })
        {
            services.Configure<CookieAuthenticationOptions>(scheme, options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });
        }
        services.ConfigureApplicationCookie(options =>
        {
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
        });
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.AddAuthorization();
        services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
        return identity;
    }
}
