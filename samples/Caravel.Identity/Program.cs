using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Caravel.AspNetCore;
using Caravel.Auth;
using Caravel.Clarion;
using Caravel.Events;
using Caravel.Queues;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Caravel.IdentitySample;

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
        builder.AddCaravel();
        var database = builder.Configuration["Caravel:IdentityDatabase"];
        var provider = builder.Configuration["Caravel:DatabaseProvider"] ?? "sqlite";
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("Set Caravel__IdentityDatabase to a SQLite file path or the selected provider's connection string.");
        var connection = provider == "sqlite"
            ? new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(database) }.ToString()
            : database;
        builder.Services.AddHealthChecks().AddCheck("required-schema", new BackendReadinessCheck(provider, connection), timeout: TimeSpan.FromSeconds(5));
        var concurrency = builder.Configuration.GetValue("Caravel:Limits:Concurrency", 32);
        var loginPermits = builder.Configuration.GetValue("Caravel:Limits:LoginPermitLimit", 10);
        var loginWindow = builder.Configuration.GetValue("Caravel:Limits:LoginWindowSeconds", 60);
        if (concurrency is < 1 or > 1024 || loginPermits is < 1 or > 1000 || loginWindow is < 1 or > 3600)
            throw new InvalidOperationException("Use concurrency 1-1024, login permits 1-1000 and login window 1-3600 seconds.");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("application", _ => new ConcurrencyLimiterOptions
                { PermitLimit = concurrency, QueueLimit = 0 }));
            options.AddFixedWindowLimiter("login", options =>
            {
                options.PermitLimit = loginPermits;
                options.Window = TimeSpan.FromSeconds(loginWindow);
                options.QueueLimit = 0;
            });
        });
        builder.Services.AddClarion<IdentityContext>(options => SampleDatabase.Configure(options, provider, connection));
        builder.Services.AddDbContextFactory<QueueDbContext>(options => SampleDatabase.Configure(options, provider, connection, queue: true));
        builder.Services.AddCaravelDatabaseQueue();
        builder.Services.AddQueueJob<CounterEvent, CounterEventHandler>("counter.record.v1");
        if (builder.Configuration.GetValue<bool>("Caravel:RunWorker"))
            builder.Services.AddCaravelQueueWorker("counter");
        builder.Services.AddCaravelIdentity<IdentityUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<IdentityContext>();
        builder.Services.AddAuthorizationBuilder().AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));
        builder.Services.AddValidation();
        builder.Services.AddCaravelEvents().AddEventListener<NoteCreated, LogNoteCreated>();

        var app = builder.Build();
        app.UseHttpsRedirection();
        app.Use(async (context, next) =>
        {
            if (context.Request.ContentLength > 16 * 1024)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            await next(context);
        });
        // Exact probe paths bypass cookie validation, so liveness never depends on the user database.
        app.UseHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.UseHealthChecks("/health/ready", new HealthCheckOptions());
        app.UseCaravel();
        app.UseRateLimiter();

        app.MapGet("/", () => Results.Ok(new { sample = "Clinimatix Caravel Identity", guide = "docs/AUTHENTICATION.md" }));
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
            return result.Succeeded ? Results.NoContent() : Results.Unauthorized();
        }).AddEndpointFilter<RequireAntiforgery>().RequireRateLimiting("login");
        app.MapPost("/auth/logout", async (SignInManager<IdentityUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return TypedResults.NoContent();
        }).RequireAuthorization().AddEndpointFilter<RequireAntiforgery>();
        app.MapGet("/auth/me", (ClaimsPrincipal user) => TypedResults.Ok(new { id = user.FindFirstValue(ClaimTypes.NameIdentifier) }))
            .RequireAuthorization();
        app.MapGet("/admin", () => TypedResults.Ok(new { message = "Administrator access" })).RequireAuthorization("Administrator");

        var notes = app.MapGroup("/notes").RequireAuthorization();
        notes.MapGet("/{id:guid}", async Task<Results<Ok<NoteResponse>, NotFound>> (Guid id, ClaimsPrincipal user, IdentityContext db, CancellationToken token) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(note => note.Id == id && note.OwnerId == owner, token);
            return note is null ? TypedResults.NotFound() : TypedResults.Ok(new NoteResponse(note.Id, note.Text));
        });
        notes.MapPost("/", async (NoteRequest request, ClaimsPrincipal user, IClarion db, IEventDispatcher events, CancellationToken token) =>
        {
            var note = new Note { Id = Guid.NewGuid(), OwnerId = user.FindFirstValue(ClaimTypes.NameIdentifier)!, Text = request.Text };
            db.Models<Note>().Add(note);
            await db.SaveChangesAsync(token);
            // In-process notification only: persistence has already succeeded if a listener fails.
            await events.DispatchAsync(new NoteCreated(note.Id), token);
            return Results.Created($"/notes/{note.Id}", new { note.Id, note.Text });
        }).AddEndpointFilter<RequireAntiforgery>();
        notes.MapPut("/{id:guid}", async (Guid id, NoteRequest request, ClaimsPrincipal user, IdentityContext db, CancellationToken token) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var note = await db.Notes.SingleOrDefaultAsync(note => note.Id == id && note.OwnerId == owner, token);
            if (note is null) return Results.NotFound();
            note.Text = request.Text;
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        }).AddEndpointFilter<RequireAntiforgery>();

        app.MapCounterEndpoints();
        app.MapAccountEndpoints();

        if (await app.ExportCaravelRoutesAsync(args)) return;
        if (args.Contains("--seed-demo-user", StringComparer.Ordinal))
        {
            if (!app.Environment.IsDevelopment())
                throw new InvalidOperationException("Demo-user provisioning is available only in Development.");
            var userName = Environment.GetEnvironmentVariable("CARAVEL_DEMO_USER")
                ?? throw new InvalidOperationException("Set CARAVEL_DEMO_USER for the synthetic account.");
            var password = Environment.GetEnvironmentVariable("CARAVEL_DEMO_PASSWORD")
                ?? throw new InvalidOperationException("Set CARAVEL_DEMO_PASSWORD for the synthetic account.");
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var result = await users.CreateAsync(new IdentityUser { UserName = userName, Email = userName, EmailConfirmed = true }, password);
            if (!result.Succeeded)
                throw new InvalidOperationException("Demo-user creation failed: " + string.Join(", ", result.Errors.Select(error => error.Code)));
            Console.WriteLine("Synthetic demo user created. This sample does not send verification email.");
            return;
        }
        await app.RunAsync();
    }
}

public sealed record LoginRequest([property: Required, StringLength(256)] string UserName,
    [property: Required, StringLength(1024)] string Password);
public sealed record NoteRequest([property: Required, StringLength(500, MinimumLength = 1)] string Text);
public sealed record NoteResponse(Guid Id, string Text);
public sealed record NoteCreated(Guid NoteId);
public sealed class LogNoteCreated(ILogger<LogNoteCreated> logger) : IEventListener<NoteCreated>
{
    public Task HandleAsync(NoteCreated message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Synthetic note {NoteId} created", message.NoteId);
        return Task.CompletedTask;
    }
}

public sealed class RequireAntiforgery(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Invalid antiforgery token."); }
        return await next(context);
    }
}
