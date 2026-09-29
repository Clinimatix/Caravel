# Authentication

Caravel Auth helps you set up ASP.NET Core Identity for a web app. It uses .NET's user manager, password hashing, cookies, roles and policies. Add the optional account endpoints for registration, email confirmation, password recovery and authenticator-app MFA. Your app chooses the database, mail transport and account screens.

The optional package is `Clinimatix.Caravel.Auth`. It works in an ordinary ASP.NET Core app; it doesn't require Clarion or bundle a database provider.

## Set up local accounts

```csharp
using Caravel.Auth;
using Microsoft.AspNetCore.Identity;

builder.Services.AddCaravelIdentity<IdentityUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));
```

Register `AppDbContext` with your EF Core provider first. It should inherit from `IdentityDbContext<IdentityUser>` (or the Identity context that matches your user and key types). Install `Microsoft.AspNetCore.Identity.EntityFrameworkCore` in the app. You can use `AddClarion<AppDbContext>` if this context also stores your application's models.

In a Caravel app, `UseCaravel` adds authentication before authorization. In an ordinary ASP.NET Core app, add `UseAuthentication` and `UseAuthorization` in that order. Put HTTPS redirection before them.

This registers services; it doesn't create users, change your database or expose registration endpoints. Apply reviewed EF migrations yourself.

## Add registration, recovery and MFA

Map the account routes explicitly after registering Identity, antiforgery and an ASP.NET Core rate limiting policy:

```csharp
app.MapCaravelAccountEndpoints<IdentityUser>("/auth", new AccountEndpointOptions
{
    AllowRegistration = true,
    ConfirmationPage = new Uri("https://accounts.example.com/confirm"),
    PasswordResetPage = new Uri("https://accounts.example.com/reset-password"),
    RateLimitPolicy = "accounts"
});
```

`AllowRegistration` defaults to `false`. Opt in only when the app should accept new accounts. The routes work with a user class that has a parameterless constructor, an Identity email store, security stamps, authenticator keys and recovery-code support. Identity's standard EF store supplies those capabilities. Register `IEmailSender<TUser>` to deliver native Identity messages; the sample's [adapter](../samples/Caravel.Identity/AccountMailSender.cs) connects it to [Caravel Mail](MAIL.md). Auth itself has no mail-transport dependency.

The two configured URLs are trusted application pages, not API callbacks. They must be absolute HTTPS addresses without credentials, query strings or fragments. Caravel appends `email` and a URL-safe `token`; it never builds these links from the request's Host header. Each page reads those values, obtains an antiforgery token, and posts to the appropriate API route. Build those pages in your application's UI, avoid third-party assets on token-bearing pages, and don't record tokens in analytics or logs.

All these routes are JSON POST endpoints. They validate antiforgery tokens, set `Cache-Control: no-store`, and use the configured rate policy. Supply a token endpoint such as the sample's `/auth/csrf`; fetch a new token after sign-in or sign-out. Add `UseRateLimiter` after authentication and before endpoints. Configure a request-body limit at the server or proxy too.

| Route under the mapped prefix | JSON fields | Result |
| --- | --- | --- |
| `/register` | `email`, `password` | 202 with no account-existence disclosure; 404 when registration is disabled |
| `/resend-confirmation` | `email` | 202; sends only for an existing unconfirmed account |
| `/confirm-email` | `email`, `token` | 204 on confirmation; otherwise 400 |
| `/forgot-password` | `email` | 202; sends only for an existing confirmed account |
| `/reset-password` | `email`, `token`, `newPassword` | 204 on reset; otherwise 400 |
| `/mfa/login` | Either `code` or `recoveryCode` | 204 after the password step; otherwise 401 |
| `/mfa/setup` | `password` | Authenticated; returns `sharedKey` for an unenrolled account |
| `/mfa/enable` | `password`, `code` | Authenticated; enables MFA and returns ten `recoveryCodes` |
| `/mfa/recovery-codes` | `password`, either `code` or `recoveryCode` | Authenticated; replaces all recovery codes |
| `/mfa/disable` | `password`, either `code` or `recoveryCode` | Authenticated; disables MFA, rotates the key and removes recovery codes |

Registration failures, including duplicate accounts and passwords that do not satisfy policy, return the same 202 response. Show the password policy before submission and direct users to check their email or use recovery. Delivery failures are logged without the recipient, token or transport exception and retain that generic response; configure mail-service monitoring and offer resend. These responses reduce account enumeration, but do not promise identical response timing. Invalid input shapes return 400. Apply network/account-aware abuse controls appropriate to your deployment.

Confirmation and reset tokens come from native Identity token providers. A successful password reset changes the security stamp, invalidates the reset token and revokes existing application and pending-MFA cookies. Resetting a password does not disable MFA. Configure native token lifetimes and persistent Data Protection keys for your application.

### Authenticator apps and recovery

Password login must return a distinct pending result when `PasswordSignInAsync` reports `RequiresTwoFactor`; the sample returns 202 with `{ "requiresTwoFactor": true }`. Identity issues a five-minute continuation cookie, which does not authorize application requests. Submit a TOTP authenticator code or a single-use recovery code to `/mfa/login` using that browser's cookies. Both invalid-code paths enforce account lockout. Caravel binds the continuation cookie to the security stamp, so revoking sessions also invalidates unfinished logins.

Enrollment requires an authenticated account and its current password. Show `sharedKey` only to that user, let them add it to their authenticator, then submit a current code to enable MFA. The framework delegates key generation and TOTP verification to Identity. There is no SMS fallback or custom cryptography.

Save the recovery codes when they are first returned. Enabling MFA, replacing recovery codes and disabling MFA sign the caller out and revoke other sessions. Replacing codes or disabling MFA requires both the current password and an authenticator code or an unused recovery code. To rotate an authenticator key, disable and enroll again; MFA is off between those steps. Each recovery code is consumed once: recovering a lost authenticator requires one code for sign-in and another for disabling MFA or replacing the remaining codes. Losing both the authenticator and the recovery codes needs an application-owned, verified account-recovery process; this package does not supply an administrative bypass.

## What the defaults do

| Setting | Default |
| --- | --- |
| Sign-in | Requires a confirmed email address |
| Email | Must be unique |
| Password | At least 12 characters, including a digit, a lowercase letter, an uppercase letter and a symbol |
| Failed sign-ins | Five failures lock the account for five minutes, when the login endpoint enables `lockoutOnFailure` |
| Authentication cookies | HTTPS only, HttpOnly, SameSite=Lax |
| Application cookie lifetime | Eight hours, without sliding extension |
| Security stamp | Checked against the user store on every authenticated request |

Checking the security stamp on every request makes an explicit stamp change take effect on the next request. It also means a database read per request. You can choose a longer interval with `Configure<SecurityStampValidatorOptions>`, accepting that existing cookies can remain valid until the next check.

Change Identity options in the callback to `AddCaravelIdentity`. Configure cookies afterward with `ConfigureApplicationCookie`. These are ordinary [Identity settings](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0), not a separate Caravel account system.

## Protect endpoints

```csharp
app.MapGet("/admin", () => TypedResults.Ok(new { message = "Welcome" }))
    .RequireAuthorization("Administrator");
```

Use `TypedResults` for API responses so ASP.NET Core can recognize the endpoint as an API and return **401** (sign-in required) or **403** (permission denied), rather than redirecting to a web login page. [How ASP.NET Core handles API authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/api-endpoint-auth?view=aspnetcore-10.0)

For password sign-in, call `SignInManager.PasswordSignInAsync` with `lockoutOnFailure: true`. Return the same failure response for unknown users, bad passwords, unconfirmed email and locked accounts. Handle two-factor sign-in explicitly if your app enables it; a two-factor-required result is not a successful login.

Cookie-authenticated mutations need antiforgery protection, including login and logout. `UseAntiforgery` alone doesn't validate an arbitrary JSON endpoint. The [Identity sample](../samples/Caravel.Identity/Program.cs) issues a token and explicitly validates it before every JSON mutation. Fetch a new token after signing in or out because tokens are tied to the current identity. [ASP.NET Core antiforgery guidance](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)

`SignOutAsync` clears the current browser's cookie. It doesn't revoke copies of that cookie. Use `UserManager.UpdateSecurityStampAsync(user)` when you need to invalidate previously issued cookies, such as after revoking account access. Password changes update the stamp too.

## Try the sample

The sample combines Identity, Clarion and Events. Signed-in users can create and update their own notes. Ownership comes from the authenticated user, and every read and update checks it. This is a user-ownership example, not a complete multi-tenant system.

From the repository root in PowerShell 7:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
New-Item -ItemType Directory -Force artifacts | Out-Null
$env:Caravel__IdentityDatabase = Join-Path (Get-Location) 'artifacts/identity-demo.db'
dotnet tool restore
dotnet ef database update --project samples/Caravel.Identity --context IdentityContext
dotnet ef database update --project samples/Caravel.Identity --context QueueDbContext

# Use a made-up account and a password created just for this demo.
$env:CARAVEL_DEMO_USER = 'demo@example.invalid'
$env:CARAVEL_DEMO_PASSWORD = Read-Host 'Demo password (12+ characters)' -MaskInput
try {
    dotnet run --project samples/Caravel.Identity -- --seed-demo-user
} finally {
    Remove-Item Env:CARAVEL_DEMO_PASSWORD, Env:CARAVEL_DEMO_USER
}
dotnet run --project samples/Caravel.Identity -- --urls https://localhost:7246
```

HTTPS requires your usual .NET development certificate. Demo-user provisioning works only in Development, marks the demo email as confirmed, and refuses duplicate accounts. The app never creates or migrates a database on startup. Don't use this provisioning path for real accounts.

| Endpoint | Purpose |
| --- | --- |
| `GET /auth/csrf` | Gets the token and header name to send with mutations; keep the returned cookie too |
| `POST /auth/login` | Signs in with a JSON `userName` and `password`; returns 202 when MFA is required |
| `POST /auth/logout` | Signs out the current browser |
| `POST /auth/password` | Changes the signed-in account's password using JSON `currentPassword` and `newPassword`, then signs out all its sessions |
| `POST /auth/logout-all` | Revokes the signed-in account's sessions and signs out this browser; no body is needed |
| `GET /auth/me` | Returns the signed-in user's identifier |
| `GET /admin` | Requires the Administrator role |
| `POST /notes/` | Creates a note from JSON `text` |
| `GET /notes/{id}`, `PUT /notes/{id}` | Reads or updates a note owned by the signed-in user |

The same sample also demonstrates [authenticated ingestion and reporting](BACKEND-SAMPLE.md): accept a counter event, save a durable job, process it safely after a retry, and query only your own results. Its queue uses a separate migration history in the same SQLite file, which is why you apply both migrations above.

The sample uses an in-memory mail capture transport and never sends real messages. Registration remains disabled unless `Caravel__Accounts__AllowRegistration=true`. Configure `Caravel__Accounts__ConfirmationPage` and `Caravel__Accounts__PasswordResetPage` for your own UI; their localhost defaults are placeholders, and the sample does not implement account screens. Captured mail is available through the `MailCapture` service to tests, not through a public HTTP inbox.

The sample's tests in `tests/Caravel.Identity.Tests` exercise registration, confirmation, recovery, TOTP enrollment, pending sign-in, lockout and session revocation against disposable databases.

## Change a password or revoke sessions

After signing in, fetch a fresh token from `/auth/csrf` and include its header and cookie with either account mutation. The server always selects the account from the authenticated principal; a submitted user identifier cannot select somebody else's account.

To change a password, send this shape to `POST /auth/password`:

```json
{
  "currentPassword": "the current password",
  "newPassword": "the new password"
}
```

Both fields are required and limited to 1,024 characters. The new password must also satisfy the configured Identity policy. Identity's [ChangePasswordAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.usermanager-1.changepasswordasync?view=aspnetcore-10.0) verifies the current password and applies the change. Invalid credentials, a weak password or an unsuccessful update return 400 without signing the caller out. A successful change returns 204, clears this browser's cookie and changes the account's security stamp. Sign in again with the new password and fetch a new antiforgery token.

Password changes and login share this sample's quota of ten requests per minute per application instance. A rejected request returns 429. This deliberately small demonstration quota is shared across callers, so one caller can temporarily exhaust it; choose suitable account, network and edge controls for your deployment. Password-change failures do not use the login endpoint's five-attempt account lockout.

`POST /auth/logout-all` requires a signed-in session and antiforgery token, but no password or body. It calls Identity's [UpdateSecurityStampAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.usermanager-1.updatesecuritystampasync?view=aspnetcore-10.0) and signs out the current browser after a successful update. Success returns 204; an unsuccessful update returns 409 so the caller can retry. It doesn't change the password or revoke another account's sessions, and remains available when the shared login quota is exhausted.

With the sample's default stamp checks, other sessions and copied old cookies are rejected on their next request after either operation succeeds. A longer stamp-validation interval delays that. Requests already in progress aren't canceled, and these routes don't affect API tokens or sessions at an external identity provider.

## What's not included yet

Caravel Auth supplies account APIs, not finished account screens. Passkeys, SMS authentication, external account linking and an administrator-assisted recovery workflow remain outside this implementation. For other ways to sign in, see:

- [OpenID Connect](OIDC-AUTHENTICATION.md), for signing in with an external identity provider such as Microsoft Entra ID
- [Service authentication](SERVICE-AUTHENTICATION.md), for APIs called with bearer tokens
- [Windows authentication](WINDOWS-AUTHENTICATION.md), for intranet apps

## Before you deploy

- Configure persistent, protected [Data Protection keys](HOSTING.md#keep-users-signed-in-across-deployments) so sign-ins survive restarts.
- Configure HTTPS and any reverse proxy's forwarded headers.
- Set rate limits that fit your users. The sample's limits are deliberately small.
- Check ownership on writes as well as reads. A query filter hides data, but it doesn't authorize changes.
- Pass the signed-in account to background jobs through the queue's tenant ID, and check it in the handler.
