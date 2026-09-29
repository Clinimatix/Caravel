using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Caravel.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Caravel.Provider.Tests;

public sealed class AuthorizationProviderContracts
{
    [Fact]
    public Task Sqlite_authorization_contract() => Exercise("sqlite");

    [ProviderFact("CARAVEL_TEST_SQLSERVER")]
    public Task SqlServer_authorization_contract() => Exercise("sqlserver");

    [ProviderFact("CARAVEL_TEST_POSTGRES")]
    public Task Postgres_authorization_contract() => Exercise("postgres");

    private const string Issuer = "https://issuer.example.invalid";
    private const string Audience = "synthetic-grant-api";

    private static async Task Exercise(string provider)
    {
        var databaseName = "caravel_auth_test_" + Guid.NewGuid().ToString("N");
        using var signingKey = RSA.Create(2048);
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(signingKey.ExportParameters(false));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.AddCaravel();
        builder.Services.AddDbContext<GrantContext>(options => ProviderTestDatabase.Configure(options, provider, databaseName));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.SaveToken = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer, ValidateIssuer = true,
                ValidAudience = Audience, ValidateAudience = true,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(publicKey),
                ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"],
                NameClaimType = "sub", RoleClaimType = "roles"
            };
            options.Events.OnTokenValidated = context =>
            {
                foreach (var type in new[] { "sub", "client_id", "tenant_id" })
                {
                    var values = context.Principal!.FindAll(type).ToArray();
                    if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0].Value)
                        || values[0].Value.Length > 64 || values[0].Value.Any(char.IsControl))
                        context.Fail("The service identity profile is invalid.");
                }
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("Read", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireRole("records.read")
                .RequireAssertion(context => HasCurrentGrant(context, write: false)))
            .AddPolicy("Write", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireRole("records.write")
                .RequireAssertion(context => HasCurrentGrant(context, write: true)));

        await using var app = builder.Build();
        app.UseCaravel();
        var endpointExecutions = 0;
        app.MapGet("/records", () => { Interlocked.Increment(ref endpointExecutions); return TypedResults.NoContent(); })
            .RequireAuthorization("Read");
        app.MapPost("/records", () => { Interlocked.Increment(ref endpointExecutions); return TypedResults.NoContent(); })
            .RequireAuthorization("Write");

        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<GrantContext>();
        var allocated = false;
        try
        {
            Assert.False(await database.GetService<IRelationalDatabaseCreator>().ExistsAsync(),
                "Refusing to reuse an existing database.");
            allocated = true; // EnsureCreated can allocate this owned database before schema creation fails.
            Assert.True(await database.Database.EnsureCreatedAsync());
            var grant = new ActorGrant();
            database.Grants.AddRange(grant, new ActorGrant { Subject = "service-b" });
            await database.SaveChangesAsync();
            await app.StartAsync();
            using var client = app.GetTestClient();
            client.BaseAddress = new Uri("https://localhost");
            var token = Token(signingKey);
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            await Check(HttpStatusCode.NoContent, HttpStatusCode.NoContent);

            grant.Enabled = false;
            await database.SaveChangesAsync();
            await Check(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(signingKey, subject: "service-b"));
            await Check(HttpStatusCode.NoContent, HttpStatusCode.NoContent);

            database.Grants.Remove(grant);
            await database.SaveChangesAsync();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            await Check(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden);

            // SQL Server ignores trailing spaces, and these SQLite/SQL Server columns ignore case.
            // The post-query ordinal checks must reject a broad database match as well as an absent row.
            foreach (var mismatch in new[]
            {
                new ActorGrant { Issuer = "https://other.example.invalid" },
                new ActorGrant { Issuer = Issuer.ToUpperInvariant() },
                new ActorGrant { Issuer = Issuer + " " },
                new ActorGrant { Subject = "SERVICE-A" },
                new ActorGrant { Subject = "service-a " },
                new ActorGrant { ClientId = "CLIENT-A" },
                new ActorGrant { ClientId = "client-a " },
                new ActorGrant { TenantId = "tenant-b" },
                new ActorGrant { TenantId = "TENANT-A" },
                new ActorGrant { TenantId = "tenant-a " }
            })
            {
                database.Grants.Add(mismatch);
                await database.SaveChangesAsync();
                var candidate = await Lookup(database, "service-a", "client-a", "tenant-a", CancellationToken.None);
                var caseOnly = string.Equals(mismatch.Issuer, Issuer, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(mismatch.Subject, "service-a", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(mismatch.ClientId, "client-a", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(mismatch.TenantId, "tenant-a", StringComparison.OrdinalIgnoreCase);
                var trailingSpace = mismatch.Issuer.EndsWith(' ') || mismatch.Subject.EndsWith(' ')
                    || mismatch.ClientId.EndsWith(' ') || mismatch.TenantId.EndsWith(' ');
                if ((caseOnly && provider is "sqlite" or "sqlserver") || (trailingSpace && provider == "sqlserver"))
                    Assert.NotNull(candidate); // Prove this provider really exercised the ordinal guard.
                await Check(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden);
                database.Grants.Remove(mismatch);
                await database.SaveChangesAsync();
            }

            grant = new ActorGrant { CanWrite = false };
            database.Grants.Add(grant);
            await database.SaveChangesAsync();
            await Check(HttpStatusCode.NoContent, HttpStatusCode.Forbidden);
            grant.CanWrite = true;
            await database.SaveChangesAsync();
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(signingKey, write: false));
            await Check(HttpStatusCode.NoContent, HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            await Check(HttpStatusCode.NoContent, HttpStatusCode.NoContent);
            Assert.True(new JsonWebToken(token).ValidTo > DateTime.UtcNow);

            // Actual provider lookup failure, confined to the table in this test's newly created database.
            await database.Database.ExecuteSqlRawAsync(provider == "sqlserver"
                ? "DROP TABLE [ActorGrants]" : "DROP TABLE \"ActorGrants\"");
            await Check(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError);

            async Task Check(HttpStatusCode expectedRead, HttpStatusCode expectedWrite)
            {
                var before = endpointExecutions;
                using var read = await client.GetAsync("/records");
                using var write = await client.PostAsync("/records", null);
                Assert.Equal(expectedRead, read.StatusCode);
                Assert.Equal(expectedWrite, write.StatusCode);
                var allowed = (expectedRead == HttpStatusCode.NoContent ? 1 : 0) + (expectedWrite == HttpStatusCode.NoContent ? 1 : 0);
                Assert.Equal(before + allowed, endpointExecutions);
                if (expectedRead == HttpStatusCode.InternalServerError)
                {
                    Assert.DoesNotContain("ActorGrants", await read.Content.ReadAsStringAsync());
                    Assert.DoesNotContain(databaseName, await write.Content.ReadAsStringAsync());
                }
            }
        }
        finally
        {
            try { await app.StopAsync(); }
            finally { if (allocated) await database.Database.EnsureDeletedAsync(); }
        }
    }

    private static async Task<bool> HasCurrentGrant(AuthorizationHandlerContext context, bool write)
    {
        if (context.User.Identity?.IsAuthenticated != true || context.Resource is not HttpContext http) return false;
        var subject = context.User.FindFirstValue("sub")!;
        var client = context.User.FindFirstValue("client_id")!;
        var tenant = context.User.FindFirstValue("tenant_id")!;
        var database = http.RequestServices.GetRequiredService<GrantContext>();
        var grant = await Lookup(database, subject, client, tenant, http.RequestAborted);
        return grant is { Enabled: true } && (write ? grant.CanWrite : grant.CanRead)
            && string.Equals(grant.Issuer, Issuer, StringComparison.Ordinal)
            && string.Equals(grant.Subject, subject, StringComparison.Ordinal)
            && string.Equals(grant.ClientId, client, StringComparison.Ordinal)
            && string.Equals(grant.TenantId, tenant, StringComparison.Ordinal);
    }

    private static Task<ActorGrant?> Lookup(GrantContext database, string subject, string client, string tenant, CancellationToken token) =>
        database.Grants.AsNoTracking().SingleOrDefaultAsync(grant => grant.Issuer == Issuer
            && grant.Subject == subject && grant.ClientId == client && grant.TenantId == tenant, token);

    private static string Token(RSA key, string subject = "service-a", bool write = true) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, TokenType = "at+jwt",
            IssuedAt = DateTime.UtcNow.AddMinutes(-1), NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject, ["client_id"] = "client-a", ["tenant_id"] = "tenant-a",
                ["roles"] = write ? new[] { "records.read", "records.write" } : new[] { "records.read" }
            },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)
        });

    public sealed class ActorGrant
    {
        public string Issuer { get; set; } = AuthorizationProviderContracts.Issuer;
        public string Subject { get; set; } = "service-a";
        public string ClientId { get; set; } = "client-a";
        public string TenantId { get; set; } = "tenant-a";
        public bool Enabled { get; set; } = true;
        public bool CanRead { get; set; } = true;
        public bool CanWrite { get; set; } = true;
    }

    public sealed class GrantContext(DbContextOptions<GrantContext> options) : DbContext(options)
    {
        public DbSet<ActorGrant> Grants => Set<ActorGrant>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            var entity = model.Entity<ActorGrant>().ToTable("ActorGrants");
            entity.HasKey(grant => new { grant.Issuer, grant.Subject, grant.ClientId, grant.TenantId });
            entity.Property(grant => grant.Issuer).HasMaxLength(200);
            foreach (var name in new[] { nameof(ActorGrant.Subject), nameof(ActorGrant.ClientId), nameof(ActorGrant.TenantId) })
                entity.Property<string>(name).HasMaxLength(64);
            var collation = Database.IsSqlite() ? "NOCASE" : Database.IsSqlServer() ? "Latin1_General_100_CI_AS" : null;
            if (collation is not null)
                foreach (var name in new[] { nameof(ActorGrant.Issuer), nameof(ActorGrant.Subject), nameof(ActorGrant.ClientId), nameof(ActorGrant.TenantId) })
                    entity.Property<string>(name).UseCollation(collation);
        }
    }
}
