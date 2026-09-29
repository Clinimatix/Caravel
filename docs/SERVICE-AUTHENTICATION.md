# Authenticate service requests with bearer tokens

When other services or apps call your API with access tokens, use ASP.NET Core's JWT bearer handler directly. It checks that each token was issued by the identity provider you trust, and ASP.NET Core authorization policies then decide what the caller may do. You don't need a Caravel wrapper for any of this.

This guide is for APIs that receive bearer tokens. For browser sign-in, see [local accounts](AUTHENTICATION.md) or [OpenID Connect](OIDC-AUTHENTICATION.md). Caravel doesn't issue tokens; your identity provider does. Microsoft's [JWT bearer guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0) covers the handler in depth.

## Configure token validation

Add `Microsoft.AspNetCore.Authentication.JwtBearer` to your app, matching your ASP.NET Core version.

The example below expects access tokens that:

- are signed with RS256 and have `typ: at+jwt`
- come from one exact issuer, for your API's audience, with an expiration time
- carry exactly one `sub`, `client_id` and `tenant_id` claim
- list permissions in `roles`, such as `records.read` and `records.write`

That's the example's token format, not a universal standard. Check what your provider issues and adjust the claim names, but don't accept extra algorithms or token types just to get a token through.

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

var authority = builder.Configuration["Api:Authority"]
    ?? throw new InvalidOperationException("Configure Api:Authority.");
var issuer = builder.Configuration["Api:Issuer"]
    ?? throw new InvalidOperationException("Configure Api:Issuer.");
var audience = builder.Configuration["Api:Audience"]
    ?? throw new InvalidOperationException("Configure Api:Audience.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidateIssuer = true,
            ValidAudience = audience,
            ValidateAudience = true,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidTypes = ["at+jwt"],
            NameClaimType = "sub",
            RoleClaimType = "roles"
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (!string.Equals(context.SecurityToken.Issuer, issuer, StringComparison.Ordinal))
                {
                    context.Fail("The access token issuer is not accepted.");
                    return Task.CompletedTask;
                }
                foreach (var claimType in new[] { "sub", "client_id", "tenant_id" })
                {
                    var claims = context.Principal!.FindAll(claimType).ToArray();
                    if (claims.Length != 1 || string.IsNullOrWhiteSpace(claims[0].Value)
                        || claims[0].Value.Length > 128 || claims[0].Value.Any(char.IsControl))
                    {
                        context.Fail("The access token does not match the service identity profile.");
                        break;
                    }
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().Build())
    .AddPolicy("ReadRecords", policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireRole("records.read"))
    .AddPolicy("WriteRecords", policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireRole("records.write"));
```

Settings come from your app's configuration. The authority provides HTTPS metadata and public signing keys.

Why the extra issuer check in `OnTokenValidated`? ASP.NET Core can also accept the issuer advertised in the provider's metadata, so the handler compares the token's issuer with `Api:Issuer` exactly. That keeps the API locked to one issuer even if the metadata says something different. The 30-second clock skew is a deliberate small tolerance.

`UseCaravel()` adds authentication before authorization. In a plain ASP.NET Core app, call `UseAuthentication()` before `UseAuthorization()`. Serve the API over HTTPS and configure forwarded headers if it sits behind a proxy. `RequireHttpsMetadata` only secures the metadata download, not incoming requests.

## Apply policies and ownership checks

Protect each endpoint with a policy:

```csharp
app.MapGet("/records/{id:guid}", ReadOwnedRecord)
    .RequireAuthorization("ReadRecords");
app.MapPut("/records/{id:guid}", UpdateOwnedRecord)
    .RequireAuthorization("WriteRecords");
```

Policies decide *what kind* of thing a caller may do; your handlers decide *which records*. Take the caller's subject and tenant from the validated `ClaimsPrincipal`, include both in every lookup and update, and ignore any owner or tenant fields in the request body. Otherwise a valid caller from one tenant could reach another tenant's data by guessing an ID.

A few more things to get right:

- **Claims must come from the provider.** A signed token doesn't make a user-editable custom attribute trustworthy. If your provider doesn't manage tenant membership, check the caller against your own records (see the next section).
- **Compare identifiers exactly.** If your database compares text case-insensitively, a lookup can match more than you intended. Choose collations deliberately, and compare in code as well.
- **One issuer, one set of subjects.** If you ever accept several issuers, map their identities explicitly; the same `sub` from two issuers isn't the same caller.

Responses follow the usual conventions: **401** with a bearer challenge for a missing or invalid token, **403** when a valid token lacks the permission, and, in the example, **404** for another owner's record so its existence isn't revealed.

Clients send tokens in the `Authorization: Bearer ...` header. The handler doesn't read them from query strings or cookies, and you shouldn't add that without a good reason: URLs and cookies leak in different ways. If one app serves both cookie-based pages and a bearer-token API, give the bearer scheme its own name and require it explicitly on API policies. Cookie routes still need antiforgery protection.

## Check that the caller still has access

A valid token says who the caller is and what they were allowed to do *when the token was issued*. It stays valid until it expires. If you need to withdraw access sooner, also check a **current grant** on each request: a record in your own database that says this caller may still act for this tenant.

Grants belong in your app's normal data model, and ASP.NET Core policies can check them. Caravel doesn't add a separate identity registry. In this example, a grant is identified by issuer, subject, client ID and tenant. Create grants only through an administrative process you control, never just because a caller presents a token.

Here's the earlier `WriteRecords` policy with a grant check added. `AppDbContext.ActorGrants` and its fields are illustrative types in *your* app, not Caravel APIs. Give the read policy an equivalent check so withdrawal applies to reads too.

```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("WriteRecords", policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireRole("records.write")
        .RequireAssertion(async context =>
        {
            if (context.User.Identity?.IsAuthenticated != true
                || context.Resource is not HttpContext http)
                return false;

            // The bearer handler already checked these claims and the exact issuer.
            var subject = context.User.FindFirstValue("sub")!;
            var client = context.User.FindFirstValue("client_id")!;
            var tenant = context.User.FindFirstValue("tenant_id")!;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var grant = await db.ActorGrants.AsNoTracking().SingleOrDefaultAsync(
                grant => grant.Issuer == issuer && grant.Subject == subject
                    && grant.ClientId == client && grant.TenantId == tenant,
                http.RequestAborted);

            return grant is { Enabled: true, CanWriteRecords: true }
                && string.Equals(grant.Issuer, issuer, StringComparison.Ordinal)
                && string.Equals(grant.Subject, subject, StringComparison.Ordinal)
                && string.Equals(grant.ClientId, client, StringComparison.Ordinal)
                && string.Equals(grant.TenantId, tenant, StringComparison.Ordinal);
        }));
```

How this behaves:

- **Both must agree.** The token's permission and the grant's permission are both required; a grant can't add a permission the token lacks.
- **Exact matching.** Put a unique index on the grant identity. The final ordinal comparisons stop a case-insensitive database match from widening it.
- **Withdrawal is immediate.** Disabling or removing a grant returns **403** on the next request, even with the same unexpired token. Other callers' grants are unaffected.
- **Fail closed.** If the grant lookup fails, the request fails too. Never fall back to "allow". The example returns a generic 500; you could map a known outage to 503 instead.
- **No caching.** The example checks the database each time. If you add caching, replicas or other routes, make sure they respect withdrawal.

Withdrawing a grant doesn't cancel requests already in progress or jobs already queued. Decide how your app should handle those.

**Re-enabling access needs care.** Disabling a client at the identity provider doesn't necessarily invalidate tokens it already issued. If you re-enable the same grant, those old tokens can work again while validation still accepts them. Before restoring access, use provider revocation that the API actually enforces, enforce an issued-after cutoff, or wait until old tokens can no longer pass validation, including configured clock tolerance.

## Before you deploy

- Decide how each client gets its credentials and tokens, using a standard flow your provider supports. Don't embed a shared secret in software you distribute to users.
- Plan credential rotation and how to cut off a compromised client quickly.
- Grant each client only the permissions it needs.
- Test against your real provider: metadata download, key rotation and token lifetimes.

## Run the tests

```powershell
dotnet test tests/Caravel.Bearer.Tests/Caravel.Bearer.Tests.csproj
```

The tests run the real JWT bearer handler behind `UseCaravel()` in an in-process test server, with throwaway signing keys and tokens generated for each run. They cover wrong issuers, audiences, signatures, lifetimes, algorithms and token types; missing or duplicated claims; 401 and 403 responses; tenant and owner checks on reads and writes (including forged body fields); and the current-grant checks above, including a simulated database outage.

`tests/Caravel.Provider.Tests` runs the grant query itself against SQLite, SQL Server and PostgreSQL, including case-insensitive columns, to confirm the exact-match guard works on real databases. See [Testing Caravel](TESTING.md#test-against-sql-server-and-postgresql).
