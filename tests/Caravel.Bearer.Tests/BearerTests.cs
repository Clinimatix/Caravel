using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Caravel.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Caravel.Bearer.Tests;

public sealed class BearerTests
{
    [Theory]
    [InlineData("https://issuer.example.invalid", "valid", HttpStatusCode.OK)]
    [InlineData("https://secret-invalid-issuer.example.invalid", "issuer", HttpStatusCode.Unauthorized)]
    public async Task MetadataIssuerCannotBroadenTheConfiguredIssuerBoundary(
        string metadataIssuer, string tokenVariant, HttpStatusCode expected)
    {
        await using var api = await TestApi.Create(metadataIssuer);
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token(tokenVariant));
        using var response = await api.Client.GetAsync("/actor");
        Assert.Equal(expected, response.StatusCode);
        // Both signatures/issuers passed native metadata validation before the application's exact-issuer guard.
        Assert.Equal(metadataIssuer, Assert.Single(api.NativeValidatedIssuers));
        Assert.DoesNotContain("secret-invalid", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("audience-slash")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("missing-expiration")]
    [InlineData("future")]
    [InlineData("unsigned")]
    [InlineData("algorithm")]
    [InlineData("type")]
    [InlineData("missing-tenant")]
    [InlineData("duplicate-tenant")]
    [InlineData("missing-client")]
    [InlineData("empty-subject")]
    public async Task InvalidTokensAreChallengedWithoutLeakingValidationDetails(string variant)
    {
        await using var api = await TestApi.Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/actor");
        if (variant != "missing") request.Headers.Authorization = new("Bearer", api.Token(variant));
        using var response = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, challenge => challenge.Scheme == "Bearer");
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString());
        Assert.DoesNotContain("secret-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task VerifiedClaimsAndNativePoliciesDistinguish401And403()
    {
        await using var api = await TestApi.Create();
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token());
        var actor = await api.Client.GetFromJsonAsync<Actor>("/actor");
        Assert.Equal(new Actor("tenant-a", "service-a", "client-a"), actor);
        using var written = await api.Client.PostAsJsonAsync("/records", new WriteRecord("hello"));
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);

        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token(roles: ["records.read"]));
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.PostAsJsonAsync("/records", new WriteRecord("denied"))).StatusCode);
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token(roles: []));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.GetAsync("/actor")).StatusCode);
    }

    [Fact]
    public async Task OwnershipUsesVerifiedTenantAndSubjectForBothReadsAndWrites()
    {
        await using var api = await TestApi.Create();
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token());
        using var created = await api.Client.PostAsJsonAsync("/records",
            new WriteRecord("original", TenantId: "forged-tenant", Owner: "forged-owner"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var record = (await created.Content.ReadFromJsonAsync<StoredRecord>())!;
        Assert.Equal("tenant-a", record.TenantId);
        Assert.Equal("service-a", record.Owner);
        var path = $"/records/{record.Id}";

        foreach (var (tenant, subject) in new[]
        {
            ("tenant-b", "service-a"), ("tenant-a", "service-b"), ("TENANT-A", "service-a")
        })
        {
            api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token(tenant: tenant, subject: subject));
            Assert.Equal(HttpStatusCode.NotFound, (await api.Client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await api.Client.PutAsJsonAsync(path,
                new WriteRecord("cross-boundary overwrite", "tenant-a", "service-a"))).StatusCode);
        }
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token());
        Assert.Equal("original", (await api.Client.GetFromJsonAsync<StoredRecord>(path))!.Text);
        Assert.Equal(HttpStatusCode.OK, (await api.Client.PutAsJsonAsync(path, new WriteRecord("updated"))).StatusCode);
        Assert.Equal("updated", (await api.Client.GetFromJsonAsync<StoredRecord>(path))!.Text);
    }

    [Fact]
    public async Task BearerCredentialsAreNotReadFromQueryStringsOrCookies()
    {
        await using var api = await TestApi.Create();
        var token = api.Token();
        using var query = await api.Client.GetAsync("/actor?access_token=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/actor");
        request.Headers.Add("Cookie", "access_token=" + token);
        using var cookie = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, cookie.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentGrantWithdrawalDeniesTheSameUnexpiredTokenWithoutAffectingAnotherActor(bool remove)
    {
        await using var api = await TestApi.Create();
        var token = api.Token();
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/actor")).StatusCode);
        using var created = await api.Client.PostAsJsonAsync("/records", new WriteRecord("before withdrawal"));
        var record = (await created.Content.ReadFromJsonAsync<StoredRecord>())!;
        var key = new GrantKey(TestApi.Issuer, "service-a", "client-a", "tenant-a");
        if (remove) Assert.True(api.Grants.Rows.TryRemove(key, out _));
        else api.Grants.Rows[key] = api.Grants.Rows[key] with { Enabled = false };

        // Reuse the identical, still-valid token; no credential or provider state changes.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.GetAsync("/actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.GetAsync($"/records/{record.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.PostAsJsonAsync("/records", new WriteRecord("denied"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.PutAsJsonAsync($"/records/{record.Id}", new WriteRecord("denied"))).StatusCode);
        Assert.Equal("before withdrawal", Assert.Single(api.Records).Value.Text);
        Assert.Equal(token, api.Client.DefaultRequestHeaders.Authorization!.Parameter);

        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token(subject: "service-b"));
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await api.Client.PostAsJsonAsync("/records", new WriteRecord("other actor"))).StatusCode);
        Assert.Equal(2, api.Records.Count);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("subject")]
    [InlineData("client")]
    [InlineData("tenant")]
    [InlineData("tenant-case")]
    public async Task AGrantForAnotherExactIdentityCannotAuthorizeAValidToken(string mismatch)
    {
        await using var api = await TestApi.Create();
        api.Grants.Rows.Clear();
        var key = new GrantKey(TestApi.Issuer, "service-a", "client-a", "tenant-a");
        key = mismatch switch
        {
            "issuer" => key with { Issuer = "https://other.example.invalid" },
            "subject" => key with { Subject = "service-b" },
            "client" => key with { ClientId = "client-b" },
            "tenant" => key with { TenantId = "tenant-b" },
            _ => key with { TenantId = "TENANT-A" }
        };
        api.Grants.Rows[key] = new(true, true, true);
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.GetAsync("/actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.PostAsJsonAsync("/records", new WriteRecord("denied"))).StatusCode);
        Assert.Empty(api.Records);
    }

    [Fact]
    public async Task TokenPermissionsAndCurrentGrantPermissionsMustBothAllowTheOperation()
    {
        await using var api = await TestApi.Create();
        var key = new GrantKey(TestApi.Issuer, "service-a", "client-a", "tenant-a");
        api.Grants.Rows[key] = new(true, true, false);
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token());
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.PostAsJsonAsync("/records", new WriteRecord("denied by grant"))).StatusCode);
        api.Grants.Rows[key] = new(true, true, true);
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token(roles: ["records.read"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.PostAsJsonAsync("/records", new WriteRecord("denied by token"))).StatusCode);
        Assert.Empty(api.Records);
    }

    [Fact]
    public async Task AuthorizationStoreFailureFailsClosedWithoutLeakingDetailsOrPerformingTheMutation()
    {
        await using var api = await TestApi.Create();
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", api.Token());
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/actor")).StatusCode);
        api.Grants.FailReads = true;
        using var read = await api.Client.GetAsync("/actor");
        using var write = await api.Client.PostAsJsonAsync("/records", new WriteRecord("must not be stored"));
        foreach (var response in new[] { read, write })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("private-store-detail", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(api.Records);
        api.Grants.FailReads = false;
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/actor")).StatusCode);
    }

    public sealed record Actor(string TenantId, string Subject, string ClientId);
    public sealed record WriteRecord(string Text, string? TenantId = null, string? Owner = null);
    public sealed record StoredRecord(Guid Id, string TenantId, string Owner, string Text);

    private sealed record GrantKey(string Issuer, string Subject, string ClientId, string TenantId);
    private sealed record CurrentGrant(bool Enabled, bool CanRead, bool CanWrite);
    // Application-owned authorization fixture, not a framework enrollment or identity registry.
    private sealed class GrantStore
    {
        public ConcurrentDictionary<GrantKey, CurrentGrant> Rows { get; } = new();
        public bool FailReads { get; set; }
        public bool Allows(GrantKey key, string permission)
        {
            if (FailReads) throw new IOException("private-store-detail");
            return Rows.TryGetValue(key, out var grant) && grant.Enabled && permission switch
            {
                "records.read" => grant.CanRead,
                "records.write" => grant.CanWrite,
                _ => false
            };
        }
    }

    /// <summary>A test-only API and ephemeral issuer. Production applications obtain access tokens from an identity provider.</summary>
    private sealed class TestApi(WebApplication app, RSA signingRsa, RSA validationRsa,
        ConcurrentQueue<string> nativeValidatedIssuers, GrantStore grants,
        ConcurrentDictionary<Guid, StoredRecord> records) : IAsyncDisposable
    {
        public const string Issuer = "https://issuer.example.invalid";
        private const string Audience = "caravel-synthetic-api";
        public HttpClient Client { get; } = app.GetTestClient();
        public IReadOnlyCollection<string> NativeValidatedIssuers => nativeValidatedIssuers.ToArray();
        public GrantStore Grants => grants;
        public IReadOnlyDictionary<Guid, StoredRecord> Records => records;

        public static async Task<TestApi> Create(string? metadataIssuer = null)
        {
            var nativeValidatedIssuers = new ConcurrentQueue<string>();
            var grants = new GrantStore();
            foreach (var (tenant, subject) in new[]
            {
                ("tenant-a", "service-a"), ("tenant-b", "service-a"),
                ("tenant-a", "service-b"), ("TENANT-A", "service-a")
            }) grants.Rows[new(Issuer, subject, "client-a", tenant)] = new(true, true, true);
            var signingRsa = RSA.Create(2048);
            var validationRsa = RSA.Create();
            validationRsa.ImportParameters(signingRsa.ExportParameters(includePrivateParameters: false));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.AddCaravel();
            builder.Services.AddSingleton(grants);
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.SaveToken = false;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer, ValidateIssuer = true,
                    ValidAudience = Audience, ValidateAudience = true, IgnoreTrailingSlashWhenValidatingAudience = false,
                    ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                    IssuerSigningKey = metadataIssuer is null
                        ? new RsaSecurityKey(validationRsa) { KeyId = "synthetic-rsa" } : null,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"],
                    NameClaimType = "sub", RoleClaimType = "roles"
                };
                if (metadataIssuer is not null)
                {
                    var metadata = new OpenIdConnectConfiguration { Issuer = metadataIssuer };
                    metadata.SigningKeys.Add(new RsaSecurityKey(validationRsa) { KeyId = "synthetic-rsa" });
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                }
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        nativeValidatedIssuers.Enqueue(context.SecurityToken.Issuer);
                        if (!string.Equals(context.SecurityToken.Issuer, Issuer, StringComparison.Ordinal))
                        {
                            context.Fail("The access token issuer is not accepted.");
                            return Task.CompletedTask;
                        }
                        foreach (var type in new[] { "sub", "client_id", "tenant_id" })
                        {
                            var values = context.Principal!.FindAll(type).ToArray();
                            if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0].Value)
                                || values[0].Value.Length > 128 || values[0].Value.Any(char.IsControl))
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
                .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build())
                .AddPolicy("ReadRecords", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser().RequireRole("records.read")
                    .RequireAssertion(context => CurrentAccess(context, "records.read")))
                .AddPolicy("WriteRecords", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser().RequireRole("records.write")
                    .RequireAssertion(context => CurrentAccess(context, "records.write")));
            var records = new ConcurrentDictionary<Guid, StoredRecord>();
            var app = builder.Build();
            app.UseCaravel();
            app.Routes(routes =>
            {
                routes.Get("/actor", (ClaimsPrincipal principal) => TypedResults.Ok(Identity(principal))).Authorize("ReadRecords");
                routes.Post("/records", (ClaimsPrincipal principal, WriteRecord request) =>
                {
                    var identity = Identity(principal);
                    var record = new StoredRecord(Guid.NewGuid(), identity.TenantId, identity.Subject, request.Text);
                    records[record.Id] = record;
                    return TypedResults.Created($"/records/{record.Id}", record);
                }).Authorize("WriteRecords");
                routes.Get("/records/{id:guid}", (Guid id, ClaimsPrincipal principal) =>
                {
                    var identity = Identity(principal);
                    return records.TryGetValue(id, out var record) && Owns(identity, record)
                        ? Results.Ok(record) : Results.NotFound();
                }).Authorize("ReadRecords");
                routes.Put("/records/{id:guid}", (Guid id, ClaimsPrincipal principal, WriteRecord request) =>
                {
                    var identity = Identity(principal);
                    if (!records.TryGetValue(id, out var record) || !Owns(identity, record)) return Results.NotFound();
                    var updated = record with { Text = request.Text };
                    return records.TryUpdate(id, updated, record) ? Results.Ok(updated) : Results.Conflict();
                }).Authorize("WriteRecords");
            });
            await app.StartAsync();
            var api = new TestApi(app, signingRsa, validationRsa, nativeValidatedIssuers, grants, records);
            api.Client.BaseAddress = new Uri("https://localhost");
            return api;
        }

        public string Token(string variant = "valid", string tenant = "tenant-a", string subject = "service-a", string[]? roles = null)
        {
            if (variant == "malformed") return "secret-invalid-token";
            var claims = new List<Claim>
            {
                new("sub", variant == "empty-subject" ? " " : subject),
                new("jti", Guid.NewGuid().ToString())
            };
            if (variant != "missing-tenant") claims.Add(new("tenant_id", tenant));
            if (variant != "missing-client") claims.Add(new("client_id", "client-a"));
            if (variant == "duplicate-tenant") claims.Add(new("tenant_id", "secret-invalid-tenant"));
            claims.AddRange((roles ?? ["records.read", "records.write"]).Select(role => new Claim("roles", role)));
            using var incorrectRsa = variant == "signature" ? RSA.Create(2048) : null;
            var key = new RsaSecurityKey(incorrectRsa ?? signingRsa) { KeyId = "synthetic-rsa" };
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = variant == "issuer" ? "https://secret-invalid-issuer.example.invalid" : Issuer,
                Audience = variant switch { "audience" => "secret-invalid-audience", "audience-slash" => Audience + "/", _ => Audience },
                Subject = new ClaimsIdentity(claims), IssuedAt = DateTime.UtcNow.AddMinutes(-5),
                NotBefore = variant == "future" ? DateTime.UtcNow.AddMinutes(2) : DateTime.UtcNow.AddMinutes(-2),
                Expires = variant switch { "missing-expiration" => null, "expired" => DateTime.UtcNow.AddMinutes(-1), _ => DateTime.UtcNow.AddMinutes(5) },
                TokenType = variant == "type" ? "JWT" : "at+jwt",
                SigningCredentials = variant == "unsigned" ? null : new SigningCredentials(key,
                    variant == "algorithm" ? SecurityAlgorithms.RsaSha384 : SecurityAlgorithms.RsaSha256)
            };
            return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = variant != "missing-expiration" }.CreateToken(descriptor);
        }

        private static Actor Identity(ClaimsPrincipal principal) => new(
            principal.FindFirst("tenant_id")!.Value, principal.FindFirst("sub")!.Value, principal.FindFirst("client_id")!.Value);
        private static bool CurrentAccess(AuthorizationHandlerContext context, string permission)
        {
            if (context.User.Identity?.IsAuthenticated != true || context.Resource is not HttpContext http) return false;
            var actor = Identity(context.User);
            return http.RequestServices.GetRequiredService<GrantStore>()
                .Allows(new(Issuer, actor.Subject, actor.ClientId, actor.TenantId), permission);
        }
        private static bool Owns(Actor actor, StoredRecord record) =>
            string.Equals(actor.TenantId, record.TenantId, StringComparison.Ordinal)
                && string.Equals(actor.Subject, record.Owner, StringComparison.Ordinal);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            signingRsa.Dispose();
            validationRsa.Dispose();
        }
    }
}
