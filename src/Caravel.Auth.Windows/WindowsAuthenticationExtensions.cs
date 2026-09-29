using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Caravel.Auth.Windows;

public static class WindowsAuthenticationExtensions
{
    /// <summary>
    /// Adds native Negotiate authentication, an authenticated fallback policy and HTTPS antiforgery
    /// services. Hosting, directory membership and Kerberos configuration remain application deployment concerns.
    /// </summary>
    /// <remarks>
    /// Intended for applications using Windows authentication as their default scheme. Browser mutations
    /// still require antiforgery validation. Anonymous endpoints must be explicitly marked AllowAnonymous.
    /// </remarks>
    public static AuthenticationBuilder AddCaravelWindowsAuthentication(this IServiceCollection services,
        Action<NegotiateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = NegotiateDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = NegotiateDefaults.AuthenticationScheme;
        }).AddNegotiate(configure ?? (_ => { }));
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
        services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
        return authentication;
    }
}
