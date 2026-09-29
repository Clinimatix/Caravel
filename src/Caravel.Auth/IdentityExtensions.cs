using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

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
        // Native pending-MFA cookies otherwise contain only a user id. Bind them to the
        // current stamp so password changes and session revocation cancel unfinished logins too.
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            options.SlidingExpiration = false;
            options.Events.OnSigningIn = async context =>
            {
                var id = context.Principal?.FindFirstValue(ClaimTypes.Name);
                if (id is null) return;
                var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<TUser>>();
                var user = await users.FindByIdAsync(id);
                if (user is not null && users.SupportsUserSecurityStamp && context.Principal?.Identity is ClaimsIdentity identity)
                    identity.AddClaim(new Claim(users.Options.ClaimsIdentity.SecurityStampClaimType, await users.GetSecurityStampAsync(user)));
            };
            options.Events.OnValidatePrincipal = async context =>
            {
                var id = context.Principal?.FindFirstValue(ClaimTypes.Name);
                if (id is null) return;
                var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<TUser>>();
                var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<TUser>>();
                var user = await users.FindByIdAsync(id);
                if (user is null || !await signIn.CanSignInAsync(user) || !await users.GetTwoFactorEnabledAsync(user) ||
                    (users.SupportsUserLockout && await users.IsLockedOutAsync(user)) ||
                    !await signIn.ValidateSecurityStampAsync(user, context.Principal?.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType)))
                    context.RejectPrincipal();
            };
        });
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.AddAuthorization();
        services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
        return identity;
    }
}
