using System.Security.Claims;
using Caravel.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Caravel.OidcSample;

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddCaravel();
        var authority = RequiredHttpsSetting(builder.Configuration, "Oidc:Authority");
        var issuer = RequiredHttpsSetting(builder.Configuration, "Oidc:Issuer");
        var clientId = RequiredSetting(builder.Configuration, "Oidc:ClientId");
        var clientSecret = RequiredSetting(builder.Configuration, "Oidc:ClientSecret");

        // APIs challenge with 401; only the explicit login route redirects to the provider.
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "__Host-Caravel.Oidc.Session";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
                options.SlidingExpiration = false;
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = authority;
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.UseTokenLifetime = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    ValidateIssuer = true,
                    ValidAudience = clientId,
                    ValidateAudience = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidTypes = ["JWT"],
                    NameClaimType = "name",
                    RoleClaimType = "roles"
                };
                options.Events.OnTokenValidated = context =>
                {
                    // Metadata must not broaden this sample's explicitly configured issuer boundary.
                    var subjects = context.Principal!.FindAll("sub").ToArray();
                    if (!string.Equals(context.SecurityToken.Issuer, issuer, StringComparison.Ordinal)
                        || subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0].Value)
                        || subjects[0].Value.Length > 128 || subjects[0].Value.Any(char.IsControl))
                        context.Fail("The identity does not match the configured sign-in profile.");
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = async context =>
                {
                    context.HandleResponse();
                    await Results.Problem(statusCode: 400, title: "Sign-in could not be completed.")
                        .ExecuteAsync(context.HttpContext);
                };
            });
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy("Administrator", policy => policy.RequireAuthenticatedUser().RequireRole("Administrator"));
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-Caravel.Oidc.Antiforgery";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });

        var app = builder.Build();
        app.UseHttpsRedirection();
        app.UseCaravel();
        app.MapGet("/", () => TypedResults.Ok(new { sample = "Clinimatix Caravel OIDC", guide = "docs/OIDC-AUTHENTICATION.md" }))
            .AllowAnonymous();
        app.MapGet("/auth/login", () => Results.Challenge(
            new AuthenticationProperties { RedirectUri = "/auth/me" }, [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();
        app.MapGet("/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var tokens = antiforgery.GetAndStoreTokens(context);
            return TypedResults.Ok(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
        });
        app.MapPost("/auth/logout", async Task<Results<NoContent, ProblemHttpResult>> (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return TypedResults.Problem(statusCode: 400, title: "Invalid antiforgery token."); }
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        }).RequireAuthorization();
        app.MapGet("/auth/me", (ClaimsPrincipal user) => TypedResults.Ok(new { issuer, subject = user.FindFirstValue("sub") }))
            .RequireAuthorization();
        app.MapGet("/admin", () => TypedResults.Ok(new { message = "Administrator access" }))
            .RequireAuthorization("Administrator");
        if (await app.ExportCaravelRoutesAsync(args)) return;
        await app.RunAsync();
    }

    private static string RequiredSetting(IConfiguration configuration, string key) =>
        !string.IsNullOrWhiteSpace(configuration[key]) ? configuration[key]!
            : throw new InvalidOperationException($"Configure {key} before starting the OIDC sample.");

    private static string RequiredHttpsSetting(IConfiguration configuration, string key)
    {
        var value = RequiredSetting(configuration, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"Configure {key} as an absolute HTTPS URL without credentials, query or fragment.");
        return value;
    }
}
