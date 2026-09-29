using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Caravel.OidcSample;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Caravel.Oidc.Tests;

public sealed class OidcApplicationTests
{
    [Theory]
    [InlineData("Oidc:Authority", "")]
    [InlineData("Oidc:Issuer", "")]
    [InlineData("Oidc:ClientId", "")]
    [InlineData("Oidc:ClientSecret", "")]
    [InlineData("Oidc:Authority", "http://issuer.example.invalid")]
    [InlineData("Oidc:Issuer", "https://issuer.example.invalid/?secret=private")]
    public async Task MissingOrUnsafeConfigurationFailsBeforeServingRequests(string key, string value)
    {
        await using var factory = new OidcFactory();
        factory.Settings[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => factory.Client());
        Assert.Contains(key, error.Message);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task NativeCodeFlowUsesPkceAndCookiesWithFixedLocalRedirectAndNoSavedTokens()
    {
        await using var factory = new OidcFactory();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        var login = await factory.BeginLogin(client, "?returnUrl=https://outside.example.invalid");
        Assert.Equal("code", login.Query["response_type"]);
        Assert.Equal("S256", login.Query["code_challenge_method"]);
        Assert.Equal("openid profile", login.Query["scope"]);
        Assert.NotEmpty(login.Query["state"]);
        Assert.NotEmpty(login.Query["nonce"]);
        Assert.Equal("https://localhost/signin-oidc", login.Query["redirect_uri"]);
        Assert.Equal(2, login.Cookies.Length);
        Assert.All(login.Cookies, cookie =>
        {
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        });
        var response = await factory.FinishLogin(client, login);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/me", response.Headers.Location!.OriginalString);
        Assert.Equal(1, factory.Backchannel.TokenRequests);
        var actor = (await client.GetFromJsonAsync<Actor>("/auth/me"))!;
        Assert.Equal(OidcFactory.Issuer, actor.Issuer);
        Assert.Equal("synthetic-admin", actor.Subject);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin")).StatusCode);
        var session = Assert.Single(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(SessionName + "=", StringComparison.Ordinal));
        var cookieValue = session[(SessionName.Length + 1)..].Split(';')[0];
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookieValue));
        Assert.NotNull(ticket);
        Assert.Empty(ticket.Properties.GetTokens());
        Assert.Equal(TimeSpan.FromMinutes(15), ticket.Properties.ExpiresUtc - ticket.Properties.IssuedUtc);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("correlation")]
    [InlineData("provider-error")]
    public async Task CallbackMustBelongToTheBrowserThatStartedLogin(string variant)
    {
        await using var factory = new OidcFactory();
        using var client = factory.Client();
        using var otherBrowser = factory.Client();
        var login = await factory.BeginLogin(client);
        var body = new Dictionary<string, string> { ["state"] = login.Query["state"], ["code"] = login.Code };
        if (variant == "state") body["state"] = "tampered-secret-state";
        if (variant == "provider-error")
        {
            body.Remove("code");
            body["error"] = "access_denied";
            body["error_description"] = "private-provider-detail";
        }
        var target = variant == "correlation" ? otherBrowser : client;
        using var response = await target.PostAsync("/signin-oidc", new FormUrlEncodedContent(body));
        await AssertRejected(response, target);
        Assert.Equal(0, factory.Backchannel.TokenRequests);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("nonce-cookie")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("missing-subject")]
    [InlineData("duplicate-subject")]
    [InlineData("metadata-issuer")]
    public async Task InvalidIdentityTokenNeverCreatesAnApplicationSession(string variant)
    {
        await using var factory = new OidcFactory(variant);
        using var client = factory.Client();
        var login = await factory.BeginLogin(client);
        HttpClient target = client;
        if (variant == "nonce-cookie")
        {
            target = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
            // Preserve native correlation, but withhold the nonce cookie from this callback.
            target.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Cookies,
                cookie => cookie.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)).Split(';')[0]);
        }
        try
        {
            using var response = await factory.FinishLogin(target, login);
            await AssertRejected(response, target);
            Assert.Equal(1, factory.Backchannel.TokenRequests);
        }
        finally { if (target != client) target.Dispose(); }
    }

    [Fact]
    public async Task ValidIdentityWithoutAdministratorRoleIsForbiddenAndCookieHasFixedExpiry()
    {
        await using var factory = new OidcFactory("no-role");
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Redirect, (await factory.FinishLogin(client, await factory.BeginLogin(client))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task LogoutRequiresAuthenticationAndAntiforgeryAndClearsOnlyTheCurrentBrowser()
    {
        await using var factory = new OidcFactory();
        using var client = factory.Client();
        using var otherBrowser = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/auth/logout", null)).StatusCode);
        await factory.FinishLogin(client, await factory.BeginLogin(client));
        await factory.FinishLogin(otherBrowser, await factory.BeginLogin(otherBrowser));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        var csrf = (await client.GetFromJsonAsync<Csrf>("/auth/csrf"))!;
        client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        var response = await client.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherBrowser.GetAsync("/auth/me")).StatusCode);
    }

    private const string SessionName = "__Host-Caravel.Oidc.Session";
    private sealed record Actor(string Issuer, string Subject);
    private sealed record Csrf(string Token, string HeaderName);
    private sealed record Login(string Code, Dictionary<string, string> Query, string[] Cookies);

    private static async Task AssertRejected(HttpResponseMessage response, HttpClient client)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sign-in could not be completed.", body);
        Assert.DoesNotContain("private", body);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith(SessionName + "=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    private sealed class OidcFactory(string variant = "valid") : WebApplicationFactory<Program>
    {
        public const string Issuer = "https://issuer.example.invalid";
        public Dictionary<string, string> Settings { get; } = new()
        {
            ["Oidc:Authority"] = Issuer,
            ["Oidc:Issuer"] = Issuer,
            ["Oidc:ClientId"] = "synthetic-client",
            ["Oidc:ClientSecret"] = "synthetic-secret"
        };
        public TestClock Clock { get; } = new();
        public OidcBackchannel Backchannel { get; } = new(variant);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var setting in Settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options => options.TimeProvider = Clock);
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = Backchannel);
            });
        }

        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        public async Task<Login> BeginLogin(HttpClient client, string query = "")
        {
            using var response = await client.GetAsync("/auth/login" + query);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(Issuer + "/authorize", response.Headers.Location!.GetLeftPart(UriPartial.Path));
            var parameters = QueryHelpers.ParseQuery(response.Headers.Location.Query)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            var code = Guid.NewGuid().ToString("N");
            Backchannel.Codes.Add(code, parameters);
            return new(code, parameters, response.Headers.GetValues("Set-Cookie").ToArray());
        }

        public Task<HttpResponseMessage> FinishLogin(HttpClient client, Login login) => client.PostAsync("/signin-oidc",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = login.Query["state"], ["code"] = login.Code }));

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Backchannel.Dispose();
        }
    }

    // A protocol fixture, not a runnable issuer: native OIDC discovery, JWKS and token requests stay in-process.
    private sealed class OidcBackchannel(string variant) : HttpMessageHandler
    {
        private readonly RSA key = RSA.Create(2048);
        private readonly RSA wrongKey = RSA.Create(2048);
        public Dictionary<string, Dictionary<string, string>> Codes { get; } = [];
        public int TokenRequests { get; private set; }
        private string MetadataIssuer => variant == "metadata-issuer" ? "https://private-other-issuer.example.invalid" : OidcFactory.Issuer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("issuer.example.invalid", request.RequestUri.Host);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/.well-known/openid-configuration":
                    return Json(new
                    {
                        issuer = MetadataIssuer,
                        authorization_endpoint = OidcFactory.Issuer + "/authorize",
                        token_endpoint = OidcFactory.Issuer + "/token",
                        jwks_uri = OidcFactory.Issuer + "/keys",
                        response_types_supported = new[] { "code" },
                        subject_types_supported = new[] { "public" },
                        id_token_signing_alg_values_supported = new[] { "RS256" },
                        code_challenge_methods_supported = new[] { "S256" }
                    });
                case "/keys":
                    var rsa = key.ExportParameters(false);
                    return Json(new { keys = new[] { new { kty = "RSA", kid = "synthetic-key", use = "sig", alg = "RS256",
                        n = Base64UrlEncoder.Encode(rsa.Modulus), e = Base64UrlEncoder.Encode(rsa.Exponent) } } });
                case "/token":
                    TokenRequests++;
                    Assert.Equal(HttpMethod.Post, request.Method);
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.Equal("authorization_code", form["grant_type"]);
                    Assert.Equal("synthetic-client", form["client_id"]);
                    Assert.Equal("synthetic-secret", form["client_secret"]);
                    Assert.Equal("https://localhost/signin-oidc", form["redirect_uri"]);
                    Assert.True(Codes.Remove(form["code"].ToString(), out var login));
                    Assert.Equal(login["code_challenge"], Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))));
                    return Json(new { access_token = "synthetic-unused-access-token", token_type = "Bearer", expires_in = 300,
                        id_token = Token(login["nonce"]) });
                default: throw new InvalidOperationException("Unexpected OIDC backchannel destination.");
            }
        }

        private string Token(string nonce)
        {
            var claims = new Dictionary<string, object>
            {
                ["sub"] = "synthetic-admin", ["name"] = "Synthetic administrator",
                ["nonce"] = variant == "nonce" ? "private-wrong-nonce" : nonce
            };
            if (variant != "no-role") claims["roles"] = "Administrator";
            if (variant == "missing-subject") claims.Remove("sub");
            if (variant == "duplicate-subject") claims["sub"] = new[] { "synthetic-admin", "other-actor" };
            var now = DateTime.UtcNow;
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = variant == "issuer" ? "https://private-wrong-issuer.example.invalid" : MetadataIssuer,
                Audience = variant == "audience" ? "private-wrong-client" : "synthetic-client",
                Claims = claims,
                NotBefore = now.AddMinutes(-2), IssuedAt = now.AddMinutes(-2),
                Expires = variant == "expired" ? now.AddMinutes(-1) : now.AddMinutes(5),
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(variant == "signature" ? wrongKey : key)
                    { KeyId = "synthetic-key" }, SecurityAlgorithms.RsaSha256),
                TokenType = "JWT"
            });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        protected override void Dispose(bool disposing)
        {
            if (disposing) { key.Dispose(); wrongKey.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }
}
