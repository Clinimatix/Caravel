using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace Caravel.IdentitySample;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
        });
        // JSON bodies need an explicit antiforgery check before any cookie-based mutation.
        app.MapPost("/auth/login", async (LoginRequest request, SignInManager<IdentityUser> signIn) =>
        {
            var result = await signIn.PasswordSignInAsync(request.UserName, request.Password,
                isPersistent: false, lockoutOnFailure: true);
            return result.Succeeded ? Results.NoContent() : result.RequiresTwoFactor
                ? Results.Json(new { requiresTwoFactor = true }, statusCode: StatusCodes.Status202Accepted)
                : Results.Unauthorized();
        }).AddEndpointFilter<RequireAntiforgery>().RequireRateLimiting("login");
        app.MapPost("/auth/logout", async (SignInManager<IdentityUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return TypedResults.NoContent();
        }).RequireAuthorization().AddEndpointFilter<RequireAntiforgery>();
        app.MapGet("/auth/me", (ClaimsPrincipal user) => TypedResults.Ok(new { id = user.FindFirstValue(ClaimTypes.NameIdentifier) }))
            .RequireAuthorization();
    }
}

public sealed record LoginRequest([property: Required, StringLength(256)] string UserName,
    [property: Required, StringLength(1024)] string Password);

public sealed class RequireAntiforgery(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Invalid antiforgery token."); }
        return await next(context);
    }
}
