using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace Caravel.IdentitySample;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/password", async Task<Results<NoContent, UnauthorizedHttpResult, ProblemHttpResult>> (
            ChangePasswordRequest request, ClaimsPrincipal principal, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return TypedResults.Unauthorized();
            // Native Identity validates the current/new passwords and updates the security stamp.
            var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded) return TypedResults.Problem(statusCode: 400, title: "Password change was not accepted.");
            await signIn.SignOutAsync();
            return TypedResults.NoContent();
        }).RequireAuthorization().AddEndpointFilter<RequireAntiforgery>().RequireRateLimiting("login");

        app.MapPost("/auth/logout-all", async Task<Results<NoContent, UnauthorizedHttpResult, ProblemHttpResult>> (
            ClaimsPrincipal principal, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return TypedResults.Unauthorized();
            var result = await users.UpdateSecurityStampAsync(user);
            if (!result.Succeeded) return TypedResults.Problem(statusCode: 409, title: "Session revocation was not completed. Try again.");
            await signIn.SignOutAsync();
            return TypedResults.NoContent();
        }).RequireAuthorization().AddEndpointFilter<RequireAntiforgery>();
    }
}

public sealed record ChangePasswordRequest(
    [property: Required, StringLength(1024)] string CurrentPassword,
    [property: Required, StringLength(1024)] string NewPassword);
