using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caravel.Auth;

/// <summary>Opt-in local-account JSON endpoints using native Identity stores and token providers.</summary>
public static class AccountEndpointExtensions
{
    public static RouteGroupBuilder MapCaravelAccountEndpoints<TUser>(this IEndpointRouteBuilder endpoints,
        string prefix, AccountEndpointOptions options) where TUser : class, new()
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var group = endpoints.MapGroup(prefix).RequireRateLimiting(options.RateLimitPolicy)
            .AddEndpointFilter<AccountRequestFilter>();

        group.MapPost("/register", async (RegistrationRequest request, UserManager<TUser> users,
            IUserStore<TUser> store, IEmailSender<TUser> mail, HttpContext context) =>
        {
            if (!options.AllowRegistration) return Results.NotFound();
            var user = new TUser();
            await store.SetUserNameAsync(user, request.Email, context.RequestAborted);
            if (store is not IUserEmailStore<TUser> emailStore)
                throw new NotSupportedException("Account endpoints require an Identity email store.");
            await emailStore.SetEmailAsync(user, request.Email, context.RequestAborted);
            if ((await users.CreateAsync(user, request.Password)).Succeeded)
                await SendConfirmation(user, users, mail, options, context);
            // Account existence and policy failures have the same public response.
            return Results.Accepted();
        }).AllowAnonymous();

        group.MapPost("/resend-confirmation", async (EmailRequest request, UserManager<TUser> users,
            IEmailSender<TUser> mail, HttpContext context) =>
        {
            var user = await users.FindByEmailAsync(request.Email);
            if (user is not null && !await users.IsEmailConfirmedAsync(user))
                await SendConfirmation(user, users, mail, options, context);
            return Results.Accepted();
        }).AllowAnonymous();

        group.MapPost("/confirm-email", async (AccountTokenRequest request, UserManager<TUser> users) =>
        {
            var user = await users.FindByEmailAsync(request.Email);
            var token = Decode(request.Token);
            if (user is null || token is null || !(await users.ConfirmEmailAsync(user, token)).Succeeded)
                return Rejected();
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapPost("/forgot-password", async (EmailRequest request, UserManager<TUser> users,
            IEmailSender<TUser> mail, HttpContext context) =>
        {
            var user = await users.FindByEmailAsync(request.Email);
            if (user is not null && await users.IsEmailConfirmedAsync(user))
            {
                var token = Encode(await users.GeneratePasswordResetTokenAsync(user));
                var link = Link(options.PasswordResetPage, request.Email, token);
                await Deliver(() => mail.SendPasswordResetLinkAsync(user, request.Email, link), context);
            }
            return Results.Accepted();
        }).AllowAnonymous();

        group.MapPost("/reset-password", async (ResetAccountPasswordRequest request, UserManager<TUser> users) =>
        {
            var user = await users.FindByEmailAsync(request.Email);
            var token = Decode(request.Token);
            if (user is null || token is null || !await users.IsEmailConfirmedAsync(user) ||
                !(await users.ResetPasswordAsync(user, token, request.NewPassword)).Succeeded)
                return Rejected();
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapPost("/mfa/login", async (MfaLoginRequest request, SignInManager<TUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) == string.IsNullOrWhiteSpace(request.RecoveryCode))
                return Results.Unauthorized();
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            if (user is null) return Results.Unauthorized();
            var useRecovery = string.IsNullOrWhiteSpace(request.Code);
            var result = !useRecovery
                ? await signIn.TwoFactorAuthenticatorSignInAsync(request.Code!.Replace(" ", "").Replace("-", ""), false, false)
                : await signIn.TwoFactorRecoveryCodeSignInAsync(request.RecoveryCode!);
            // Identity's recovery-code sign-in does not increment lockout failures itself.
            if (!result.Succeeded && useRecovery && signIn.UserManager.SupportsUserLockout)
                await signIn.UserManager.AccessFailedAsync(user);
            return result.Succeeded ? Results.NoContent() : Results.Unauthorized();
        }).AllowAnonymous();

        var manage = group.MapGroup("/mfa").RequireAuthorization(new AuthorizeAttribute
            { AuthenticationSchemes = IdentityConstants.ApplicationScheme });
        manage.MapPost("/setup", async (MfaManagementRequest request, ClaimsPrincipal principal,
            UserManager<TUser> users, SignInManager<TUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !await Reauthenticate(user, request, users, signIn)) return Results.Unauthorized();
            if (await users.GetTwoFactorEnabledAsync(user)) return Results.Conflict();
            var key = await users.GetAuthenticatorKeyAsync(user);
            if (key is null)
            {
                if (!(await users.ResetAuthenticatorKeyAsync(user)).Succeeded) return Rejected();
                key = await users.GetAuthenticatorKeyAsync(user);
                await signIn.RefreshSignInAsync(user);
            }
            return Results.Ok(new { sharedKey = key });
        });
        manage.MapPost("/enable", async (MfaManagementRequest request, ClaimsPrincipal principal,
            UserManager<TUser> users, SignInManager<TUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !await Reauthenticate(user, request, users, signIn, requireCode: true)) return Results.Unauthorized();
            if (await users.GetTwoFactorEnabledAsync(user)) return Results.Conflict();
            // Revoke first: later store failures must not leave password-only sessions usable.
            if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) return Rejected();
            if (!(await users.SetTwoFactorEnabledAsync(user, true)).Succeeded) return Rejected();
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            if (codes is null) return Rejected();
            await signIn.SignOutAsync();
            return Results.Ok(new { recoveryCodes = codes });
        });
        manage.MapPost("/recovery-codes", async (MfaManagementRequest request, ClaimsPrincipal principal,
            UserManager<TUser> users, SignInManager<TUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !await Reauthenticate(user, request, users, signIn)) return Results.Unauthorized();
            if (!await users.GetTwoFactorEnabledAsync(user)) return Results.Conflict();
            if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) return Rejected();
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            if (codes is null) return Rejected();
            await signIn.SignOutAsync();
            return Results.Ok(new { recoveryCodes = codes });
        });
        manage.MapPost("/disable", async (MfaManagementRequest request, ClaimsPrincipal principal,
            UserManager<TUser> users, SignInManager<TUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !await Reauthenticate(user, request, users, signIn)) return Results.Unauthorized();
            if (!(await users.UpdateSecurityStampAsync(user)).Succeeded ||
                !(await users.SetTwoFactorEnabledAsync(user, false)).Succeeded ||
                !(await users.ResetAuthenticatorKeyAsync(user)).Succeeded) return Rejected();
            if (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0) is null) return Rejected();
            await signIn.ForgetTwoFactorClientAsync();
            await signIn.SignOutAsync();
            return Results.NoContent();
        });
        return group;
    }

    private static IResult Rejected() => Results.Problem(statusCode: 400, title: "Account request was not accepted.");
    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
    private static string? Decode(string token)
    {
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)); }
        catch (FormatException) { return null; }
    }
    private static string Link(Uri page, string email, string token) => QueryHelpers.AddQueryString(page.AbsoluteUri,
        new Dictionary<string, string?> { ["email"] = email, ["token"] = token });
    private static async Task SendConfirmation<TUser>(TUser user, UserManager<TUser> users, IEmailSender<TUser> mail,
        AccountEndpointOptions options, HttpContext context) where TUser : class
    {
        var email = (await users.GetEmailAsync(user))!;
        var token = Encode(await users.GenerateEmailConfirmationTokenAsync(user));
        await Deliver(() => mail.SendConfirmationLinkAsync(user, email, Link(options.ConfirmationPage, email, token)), context);
    }
    private static async Task Deliver(Func<Task> send, HttpContext context)
    {
        try { await send(); }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Never log recipient, token, message body or transport exception; preserve the generic response.
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Caravel.Auth.AccountMail")
                .LogError("Account email delivery failed. Check the configured mail service and retry the account request.");
        }
    }
    private static Task<bool> VerifyAuthenticator<TUser>(TUser user, string? code, UserManager<TUser> users) where TUser : class =>
        string.IsNullOrWhiteSpace(code) ? Task.FromResult(false) : users.VerifyTwoFactorTokenAsync(user,
            users.Options.Tokens.AuthenticatorTokenProvider, code.Replace(" ", "").Replace("-", ""));
    private static async Task<bool> Reauthenticate<TUser>(TUser user, MfaManagementRequest request,
        UserManager<TUser> users, SignInManager<TUser> signIn, bool requireCode = false) where TUser : class
    {
        if (!await signIn.CanSignInAsync(user) || (users.SupportsUserLockout && await users.IsLockedOutAsync(user))) return false;
        var valid = await users.CheckPasswordAsync(user, request.Password);
        if (valid && (requireCode || await users.GetTwoFactorEnabledAsync(user)))
            valid = requireCode
                ? string.IsNullOrWhiteSpace(request.RecoveryCode) && await VerifyAuthenticator(user, request.Code, users)
                : string.IsNullOrWhiteSpace(request.RecoveryCode)
                    ? await VerifyAuthenticator(user, request.Code, users)
                    : string.IsNullOrWhiteSpace(request.Code) && (await users.RedeemTwoFactorRecoveryCodeAsync(user, request.RecoveryCode)).Succeeded;
        if (!valid)
        {
            if (users.SupportsUserLockout) await users.AccessFailedAsync(user);
            return false;
        }
        return !users.SupportsUserLockout || (await users.ResetAccessFailedCountAsync(user)).Succeeded;
    }

    private sealed class AccountRequestFilter(IAntiforgery antiforgery) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
            catch (AntiforgeryValidationException) { return Rejected(); }
            foreach (var request in context.Arguments.OfType<IAccountRequest>())
                if (!Validator.TryValidateObject(request, new ValidationContext(request), null, true)) return Rejected();
            return await next(context);
        }
    }
}

public interface IAccountRequest;
public sealed record RegistrationRequest(
    [property: Required, EmailAddress, StringLength(256)] string Email,
    [property: Required, StringLength(1024)] string Password) : IAccountRequest;
public sealed record EmailRequest([property: Required, EmailAddress, StringLength(256)] string Email) : IAccountRequest;
public sealed record AccountTokenRequest([property: Required, EmailAddress, StringLength(256)] string Email,
    [property: Required, StringLength(4096)] string Token) : IAccountRequest;
public sealed record ResetAccountPasswordRequest([property: Required, EmailAddress, StringLength(256)] string Email,
    [property: Required, StringLength(4096)] string Token,
    [property: Required, StringLength(1024)] string NewPassword) : IAccountRequest;
public sealed record MfaLoginRequest([property: StringLength(32)] string? Code = null,
    [property: StringLength(64)] string? RecoveryCode = null) : IAccountRequest;
public sealed record MfaManagementRequest([property: Required, StringLength(1024)] string Password,
    [property: StringLength(32)] string? Code = null,
    [property: StringLength(64)] string? RecoveryCode = null) : IAccountRequest;
