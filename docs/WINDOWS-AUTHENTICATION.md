# Windows authentication

Building an intranet app for people who already have organizational Windows accounts? `Clinimatix.Caravel.Auth.Windows` connects your app to ASP.NET Core's Negotiate handler. Your directory manages the accounts; your app decides what each authenticated person may do.

The package works on its own, without local Identity or Clarion. It doesn't create accounts, change server settings or impersonate users.

## Add it to an app

Reference `Clinimatix.Caravel.Auth.Windows` (until it's on NuGet, see [using prerelease packages](RELEASE-POLICY.md#using-prerelease-packages)), then register it:

```csharp
using System.Security.Claims;
using Caravel.AspNetCore;
using Caravel.Auth.Windows;

var builder = WebApplication.CreateBuilder(args);
builder.AddCaravel();
builder.Services.AddCaravelWindowsAuthentication();
builder.Services.AddAuthorizationBuilder().AddPolicy("Operators", policy =>
    policy.RequireAuthenticatedUser().RequireRole(@"EXAMPLE\CaravelOperators"));

var app = builder.Build();
app.UseHttpsRedirection();
app.UseCaravel();

app.MapGet("/me", (ClaimsPrincipal user) =>
    TypedResults.Ok(new { name = user.Identity?.Name })).RequireAuthorization();
app.MapGet("/operations", () => TypedResults.Ok(new { access = "granted" }))
    .RequireAuthorization("Operators");

await app.RunAsync();
```

Replace the example group with one your deployment actually supplies. The helper sets Negotiate as the default authenticate/challenge scheme and adds an authenticated fallback policy. Endpoints without their own authorization metadata therefore require authentication; explicitly public endpoints need `.AllowAnonymous()`. Named policies still need to require the permissions you intend. Protect static files and middleware responses separately: endpoint policies only apply when requests reach authorization middleware.

The method returns ASP.NET Core's `AuthenticationBuilder`. Its optional callback configures ASP.NET Core's `NegotiateOptions` directly. For applications combining multiple authentication mechanisms, configure schemes explicitly with ASP.NET Core's authentication and authorization options rather than relying on this helper's defaults.

## Choose and verify your host

**IIS:** Enable its Windows Authentication feature and configure the site. Negotiate delegates to the server's authentication when enabled. An IIS Express launch profile does not configure deployed IIS. If IIS disallows anonymous requests, `.AllowAnonymous()` cannot bypass that server setting.

**Kestrel:** Configure the service identity, host name and Kerberos service principal names for your environment. A proxy in front of Negotiate must preserve a dedicated client-to-server connection; ordinary pooled proxy connections are unsuitable. Use HTTPS and verify the actual deployment path.

**Linux/macOS:** Negotiate can use Kerberos, but joining/configuring the realm and protecting its keytab are deployment work. Group roles need explicit LDAP configuration; do not assume Windows group behavior appears automatically.

These requirements, including protocol and host differences, are covered in Microsoft's [Windows authentication guide](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0). This package does not change IIS, directory entries, SPNs, keytabs or OS settings. Bosun does not yet offer a `--auth windows` starter switch.

## Protect browser requests

Windows authentication does not remove the need for CSRF protection: browsers can attach ambient credentials to requests. Protect state-changing operations with antiforgery validation, just as you would with cookie authentication. JSON endpoints must call `IAntiforgery.ValidateRequestAsync` or use an endpoint filter that does so **before** changing data. Return a client error when validation fails; do not put mutations on GET routes. See the explicit JSON filter in [the Identity sample](../samples/Caravel.Identity/Program.cs) and Microsoft's [antiforgery guidance](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).

There's no sign-out or session revocation with Windows authentication: the browser signs in with the user's Windows credentials on each connection. Disabling an account or changing group membership happens in your directory, and how quickly it takes effect depends on your directory and server setup.

## Testing

Caravel's tests cover registration, option customization, the authenticated-by-default fallback policy and role checks, using simulated users. They can't perform a real Kerberos or NTLM handshake, so before going live, test sign-in, group-based roles and account changes on your actual server, domain, browsers and any proxy in between.
