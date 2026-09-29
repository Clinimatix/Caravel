# Sign in with an identity provider

Use OpenID Connect (OIDC) when people should sign in to your Caravel web app with an account they already have, such as a Microsoft Entra ID, Okta or Auth0 account. The identity provider handles passwords and sign-in policies; your app checks the result and starts its own secure browser session.

You don't need a Caravel package for this. ASP.NET Core's `AddCookie` and `AddOpenIdConnect` already do the work, and the [Caravel.Oidc sample](../samples/Caravel.Oidc/Program.cs) shows a careful, locked-down setup for an administration site. If your API receives access tokens instead of browser sign-ins, see [service authentication](SERVICE-AUTHENTICATION.md).

## Run the sample with your provider

The sample expects:

- a confidential web client using the authorization code flow with PKCE
- an HTTPS authority and one exact issuer
- ID tokens signed with RS256, with `typ: JWT` and a single stable `sub` claim
- an optional `roles` claim for application roles

Check that your provider matches. If it doesn't, adjust the configuration to fit rather than loosening validation until a token passes.

Register a web application with your provider and set its redirect URI to `https://localhost:7247/signin-oidc`. ASP.NET Core handles that route, so you don't write a callback endpoint. Then add these settings, keeping the secret out of source control (user secrets work well locally):

| Setting | Value |
| --- | --- |
| `Oidc:Authority` | The provider's HTTPS authority, used for discovery and signing keys |
| `Oidc:Issuer` | The exact issuer expected in ID tokens |
| `Oidc:ClientId` | Your registered app's client ID |
| `Oidc:ClientSecret` | Its client secret |

All four are required. If one is missing, or a URL isn't HTTPS, the app stops at startup and names the setting without printing its value.

Then run the sample from the repository root:

```powershell
dotnet run --project samples/Caravel.Oidc -- --urls https://localhost:7247
```

Open `/auth/login` in a browser to sign in. Afterward, `/auth/me` shows your issuer and subject.

The sample uses ASP.NET Core's built-in OIDC handler, which also uses pushed authorization requests (PAR) when the provider supports them. For an app that only targets Microsoft Entra ID, Microsoft recommends [Microsoft.Identity.Web](https://learn.microsoft.com/en-us/entra/msal/dotnet/microsoft-identity-web/), which builds on the same platform features. See Microsoft's [OIDC guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0) for more.

## Endpoints

| Endpoint | Behavior |
| --- | --- |
| `GET /` | A public description of the sample |
| `GET /auth/login` | Starts sign-in, and always returns to `/auth/me` afterward |
| `GET /auth/me` | Shows the signed-in issuer and subject |
| `GET /admin` | Requires the `Administrator` role |
| `GET /auth/csrf` | Returns an antiforgery token for the signed-in browser |
| `POST /auth/logout` | Checks the antiforgery token and signs out of the app |

Callers can't choose where sign-in returns them, which rules out open redirects. API routes return 401 when nobody is signed in and 403 when the user lacks a role; only `/auth/login` redirects to the provider. A fallback policy requires sign-in for every endpoint unless you mark it anonymous.

**Identify users by issuer and subject.** The `(issuer, sub)` pair is the stable identity. Names and email addresses can change, so treat them as display information. Roles must come from the provider, never from a profile field users can edit. Your app still decides which records each user can see and change.

## Sessions and sign-out

The app's session cookie is HTTPS-only, HttpOnly and SameSite=Lax, and lasts a fixed 15 minutes without sliding renewal. The handler's temporary sign-in cookies keep ASP.NET Core's defaults so the provider's redirect back to your app works.

The sample asks only for the `openid` and `profile` scopes and doesn't store ID, access or refresh tokens in the session. It validates the token's signature, lifetime, audience and exact issuer, and adds an extra check so discovery metadata can't widen the accepted issuer. Failed sign-ins return a plain 400 response without leaking provider errors or token contents.

To sign out, call `/auth/csrf` while signed in, then send `POST /auth/logout` with the returned token in the returned header name, keeping the browser's cookies. Success returns 204 and clears this browser's session.

This signs you out of **the app only**. The provider session is still active, so signing in again may succeed immediately. It also doesn't revoke copies of the cookie or sessions in other browsers. Role changes at the provider take effect at the next sign-in, when the cookie expires. If you need federated sign-out or immediate revocation, add them for your provider.

## Before you deploy

- Configure persistent, protected [Data Protection keys](HOSTING.md#keep-users-signed-in-across-deployments).
- Configure HTTPS and any reverse proxy's forwarded headers.
- Review redirect URIs, client secret rotation, claim and role mapping, and the session lifetime with your provider.
- Try sign-in end to end with your real provider and browsers. The automated tests use a simulated provider.

## Run the tests

```powershell
dotnet test tests/Caravel.Oidc.Tests/Caravel.Oidc.Tests.csproj
```

The tests run the real sample and ASP.NET Core's OIDC handlers in-process against a simulated provider that serves discovery, signing keys and token responses. Nothing contacts a real identity provider. They cover configuration errors, the full code flow with PKCE, rejection of bad state, nonce, signature, issuer, audience, expiry and subject values, role checks, cookie lifetime and sign-out.
