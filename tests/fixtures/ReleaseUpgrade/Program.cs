using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Caravel.AspNetCore;
using Caravel.Auth;
using Caravel.Clarion;
using Caravel.Queues;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// This app is copied unchanged and compiled twice against two real package graphs.
// It explicitly creates schema only in seed; verify must open the original files.
if (args.Length != 3 || args[0] is not ("seed" or "verify"))
    throw new ArgumentException("Use seed|verify <synthetic-state-directory> <expected-package-version>.");
var phase = args[0];
var directory = Path.GetFullPath(args[1]);
var manifestPath = Path.Combine(directory, "baseline.json");
var identityPath = Path.Combine(directory, "identity.db");
var queuePath = Path.Combine(directory, "queue.db");
if (phase == "seed")
{
    if (Directory.Exists(directory)) throw new InvalidOperationException("Seed requires a new state directory.");
    Directory.CreateDirectory(directory);
}
else if (!File.Exists(manifestPath) || !File.Exists(identityPath) || !File.Exists(queuePath))
    throw new InvalidOperationException("Verify requires the original RC1 state; it never creates databases.");
var baseline = phase == "verify"
    ? JsonSerializer.Deserialize<Baseline>(await File.ReadAllTextAsync(manifestPath))!
    : new Baseline { Time = DateTimeOffset.UtcNow };
var clock = new TestClock(baseline.Time);
var assertions = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"Upgrade assertion failed: {description}");
    assertions++;
}
void IdentitySucceeded(IdentityResult result) => Check(result.Succeeded,
    "Identity operation: " + string.Join(", ", result.Errors.Select(error => error.Code)));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [], EnvironmentName = "Development", ContentRootPath = directory
});
builder.Logging.ClearProviders();
builder.WebHost.UseTestServer();
builder.AddCaravel(options => options.EnvironmentFile = Path.Combine(directory, "unused.env"));
builder.Services.AddSingleton<TimeProvider>(clock);
builder.Services.AddSingleton(new HandlerOptions { RecoverFailure = phase == "verify" });
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")))
    .SetApplicationName("Caravel.ReleaseUpgrade.Fixture");
builder.Services.AddClarion<AppDb>(options => options.UseSqlite(
    new SqliteConnectionStringBuilder { DataSource = identityPath }.ToString()));
builder.Services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(
    new SqliteConnectionStringBuilder { DataSource = queuePath }.ToString()));
builder.Services.AddCaravelIdentity<IdentityUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDb>();
builder.Services.AddCaravelDatabaseQueue(options => options.LeaseDuration = TimeSpan.FromSeconds(5));
builder.Services.AddQueueJob<RecordQuantity, RecordQuantityHandler>("upgrade.record.v1");
await using var app = builder.Build();
app.UseCaravel();
app.MapGet("/me", (ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier)!)
    .RequireAuthorization();
app.MapGet("/admin", () => "administrator").RequireAuthorization(policy => policy.RequireRole("Administrator"));

foreach (var assembly in new[] { typeof(IdentityExtensions).Assembly, typeof(IClarion).Assembly,
    typeof(IDatabaseQueue).Assembly, typeof(CaravelApplicationExtensions).Assembly, typeof(Caravel.Core.CaravelOptions).Assembly })
    Check(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0] == args[2],
        $"loaded {assembly.GetName().Name} is the requested package version");

await using var scope = app.Services.CreateAsyncScope();
var services = scope.ServiceProvider;
var db = services.GetRequiredService<AppDb>();
var users = services.GetRequiredService<UserManager<IdentityUser>>();
var queue = services.GetRequiredService<IDatabaseQueue>();
var worker = services.GetRequiredService<QueueWorker>();
await using var queueDb = await services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
const string password = "Synthetic-Upgrade-Password-47!";
if (phase == "seed")
{
    // Explicit provisioning of these two disposable files, never a host startup side effect.
    Check(await db.Database.EnsureCreatedAsync(), "new Identity/data schema created explicitly");
    Check(await queueDb.Database.EnsureCreatedAsync(), "new queue schema created explicitly");
    var alice = new IdentityUser("alice") { Id = "alice", Email = "alice@example.invalid", EmailConfirmed = true };
    var bob = new IdentityUser("bob") { Id = "bob", Email = "bob@example.invalid", EmailConfirmed = true };
    IdentitySucceeded(await users.CreateAsync(alice, password));
    IdentitySucceeded(await users.CreateAsync(bob, password));
    IdentitySucceeded(await services.GetRequiredService<RoleManager<IdentityRole>>().CreateAsync(new IdentityRole("Administrator")));
    IdentitySucceeded(await users.AddToRoleAsync(alice, "Administrator"));
    IdentitySucceeded(await users.ResetAuthenticatorKeyAsync(alice));
    IdentitySucceeded(await users.SetTwoFactorEnabledAsync(alice, true));
    baseline.RecoveryCodes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(alice, 3))!.ToArray();
    baseline.AuthenticatorKey = (await users.GetAuthenticatorKeyAsync(alice))!;
    baseline.PasswordHash = alice.PasswordHash!;
    baseline.SecurityStamp = alice.SecurityStamp!;
    baseline.ResetToken = await users.GeneratePasswordResetTokenAsync(alice);
    IdentitySucceeded(await users.SetLockoutEndDateAsync(bob, DateTimeOffset.UtcNow.AddHours(1)));
    var principal = await services.GetRequiredService<IUserClaimsPrincipalFactory<IdentityUser>>().CreateAsync(alice);
    var cookies = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
    baseline.Cookie = cookies.Cookie.Name + "=" + cookies.TicketDataFormat.Protect(new AuthenticationTicket(principal,
        new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
        IdentityConstants.ApplicationScheme));
    db.Notes.AddRange(new Note { Id = 1, OwnerId = "alice", Text = "kept across package upgrade" },
        new Note { Id = 2, OwnerId = "bob", Text = "soft deleted before upgrade" });
    await services.GetRequiredService<IClarion>().SaveChangesAsync();
    db.Notes.Remove(await db.Notes.SingleAsync(note => note.Id == 2));
    await db.SaveChangesAsync();
    baseline.NoteCreatedAt = (await db.Notes.SingleAsync(note => note.Id == 1)).CreatedAt;
    baseline.Completed = (await queue.EnqueueAsync(new RecordQuantity(2), new("completed", "alice", "completed"))).JobId;
    Check(await worker.RunOnceAsync("completed"), "RC1 worker completed a persisted job");
    baseline.LostAck = (await queue.EnqueueAsync(new RecordQuantity(5), new("recover", "alice", "lost-ack"))).JobId;
    var lease = (await queue.TryClaimAsync("recover"))!;
    await services.GetRequiredService<RecordQuantityHandler>().HandleAsync(new RecordQuantity(5), lease.Context, CancellationToken.None);
    // End the RC1 process with its durable result committed and lease unacknowledged.
    baseline.Pending = (await queue.EnqueueAsync(new RecordQuantity(3), new("pending", "bob", "pending"))).JobId;
    baseline.Dead = (await queue.EnqueueAsync(new RecordQuantity(7, true), new("dead", "alice", "dead"))).JobId;
    for (var attempt = 0; attempt < 3; attempt++)
    {
        Check(await worker.RunOnceAsync("dead"), "RC1 failed job was attempted");
        clock.Advance(TimeSpan.FromMinutes(1));
    }
    Check((await queue.GetStatusAsync(baseline.Dead, "dead", "alice"))!.State == QueueJobState.DeadLetter, "RC1 dead letter persisted");
    baseline.IdentitySchema = await Schema(identityPath);
    baseline.QueueSchema = await Schema(queuePath);
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(baseline));
}
else
{
    // Reading and using the RC1 schema is intentional: this package-only upgrade needs no DDL.
    Check(await Schema(identityPath) == baseline.IdentitySchema, "original Identity/data schema exists");
    Check(await Schema(queuePath) == baseline.QueueSchema, "original queue schema exists");
    var alice = (await users.FindByIdAsync("alice"))!;
    var bob = (await users.FindByIdAsync("bob"))!;
    Check(alice.PasswordHash == baseline.PasswordHash && alice.SecurityStamp == baseline.SecurityStamp, "password hash and security stamp retained");
    Check(await users.CheckPasswordAsync(alice, password), "RC1 password authenticates on candidate");
    Check(!await users.CheckPasswordAsync(alice, "incorrect"), "incorrect password rejected");
    Check(await users.IsInRoleAsync(alice, "Administrator") && !await users.IsInRoleAsync(bob, "Administrator"), "roles retained and scoped");
    Check(await users.IsLockedOutAsync(bob), "persisted lockout retained");
    Check(await users.GetTwoFactorEnabledAsync(alice), "MFA remains enabled");
    Check(await users.GetAuthenticatorKeyAsync(alice) == baseline.AuthenticatorKey, "authenticator key retained");
    Check(await users.CountRecoveryCodesAsync(alice) == 3, "recovery codes retained");
    IdentitySucceeded(await users.RedeemTwoFactorRecoveryCodeAsync(alice, baseline.RecoveryCodes[0]));
    Check(!(await users.RedeemTwoFactorRecoveryCodeAsync(alice, baseline.RecoveryCodes[0])).Succeeded, "recovery code cannot be reused");
    Check(await users.VerifyUserTokenAsync(alice, users.Options.Tokens.PasswordResetTokenProvider,
        UserManager<IdentityUser>.ResetPasswordTokenPurpose, baseline.ResetToken), "RC1 protected reset token validates with persisted key ring");
    Check(await db.Notes.CountAsync() == 1 && await db.Notes.WithTrashed().CountAsync() == 2, "data and soft-delete filter retained");
    var note = await db.Notes.SingleAsync();
    Check(note.OwnerId == "alice" && note.Text == "kept across package upgrade" && note.CreatedAt == baseline.NoteCreatedAt, "application data and original timestamp retained");
    clock.Advance(TimeSpan.FromMinutes(10));
    note.Text = "updated on candidate";
    await services.GetRequiredService<IClarion>().SaveChangesAsync();
    Check(note.CreatedAt == baseline.NoteCreatedAt && note.UpdatedAt > note.CreatedAt, "candidate update preserves created timestamp");
    Check(await services.GetRequiredService<IClarion>().RestoreAsync<Note>([2]) is not null, "candidate can restore RC1 soft-deleted data");
    await db.SaveChangesAsync();
    Check(await db.Notes.CountAsync() == 2, "restoration persisted");
    Check((await queue.GetStatusAsync(baseline.Completed, "completed", "alice"))!.State == QueueJobState.Completed, "completed RC1 job retained");
    Check((await queue.GetStatusAsync(baseline.LostAck, "recover", "alice")) is { State: QueueJobState.Leased, Attempts: 1 }, "unacknowledged RC1 lease retained");
    Check((await queue.GetStatusAsync(baseline.Pending, "pending", "bob")) is { State: QueueJobState.Pending, Attempts: 0 }, "pending RC1 envelope retained");
    Check((await queue.GetStatusAsync(baseline.Dead, "dead", "alice")) is { State: QueueJobState.DeadLetter, Attempts: 3 }, "dead-letter attempts retained");
    Check(await queue.GetStatusAsync(baseline.Pending, "pending", "alice") is null, "queue status remains tenant scoped");
    var duplicate = await queue.EnqueueAsync(new RecordQuantity(5), new("recover", "alice", "lost-ack"));
    Check(duplicate.AlreadyEnqueued && duplicate.JobId == baseline.LostAck, "RC1 idempotency envelope deduplicates on candidate");
    try
    {
        await queue.EnqueueAsync(new RecordQuantity(99), new("recover", "alice", "lost-ack"));
        throw new InvalidOperationException("Changed payload unexpectedly accepted.");
    }
    catch (QueueIdempotencyConflictException) { assertions++; }
    Check(await worker.RunOnceAsync("recover"), "candidate recovers expired RC1 lease");
    Check((await queue.GetStatusAsync(baseline.LostAck, "recover", "alice")) is { State: QueueJobState.Completed, Attempts: 2 }, "lost-ack redelivery completes");
    Check(await worker.RunOnceAsync("pending"), "candidate handles pending RC1 wire payload");
    Check(!await queue.ReplayAsync(baseline.Dead, "dead", "bob"), "wrong tenant cannot replay");
    Check(await queue.ReplayAsync(baseline.Dead, "dead", "alice"), "candidate can replay persisted RC1 dead letter");
    Check(await worker.RunOnceAsync("dead"), "repaired handler completes replay");
    Check((await queue.GetStatusAsync(baseline.Dead, "dead", "alice")) is { State: QueueJobState.Completed, ReplayCount: 1 }, "replay count persisted");
    Check(await db.Receipts.CountAsync() == 4 && await db.Receipts.SumAsync(receipt => receipt.Quantity) == 17, "exactly one result per original job after lost-ack recovery");
    Check((await db.Receipts.SingleAsync(receipt => receipt.JobId == baseline.Pending)).OwnerId == "bob", "handler uses persisted envelope owner");
    foreach (var name in new[] { "completed", "recover", "pending", "dead" })
        Check(!await worker.RunOnceAsync(name), "completed queues do not redeliver");
    Check(await Schema(identityPath) == baseline.IdentitySchema && await Schema(queuePath) == baseline.QueueSchema, "package upgrade performed no schema changes");
}

await app.StartAsync();
using (var client = app.GetTestClient())
{
    client.BaseAddress = new Uri("https://localhost");
    client.DefaultRequestHeaders.Add("Cookie", baseline.Cookie);
    Check((await client.GetAsync("/me")).StatusCode == HttpStatusCode.OK, "original cookie authenticates through Caravel pipeline");
    Check(await client.GetStringAsync("/me") == "alice", "cookie principal retains its account");
    Check((await client.GetAsync("/admin")).StatusCode == HttpStatusCode.OK, "original cookie role authorizes");
    if (phase == "verify")
    {
        IdentitySucceeded(await users.UpdateSecurityStampAsync((await users.FindByIdAsync("alice"))!));
        var rejected = await client.GetAsync("/me");
        Check(rejected.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, "session revocation rejects pre-upgrade cookie");
    }
}
await app.StopAsync();
var result = new { Phase = phase, PackageVersion = args[2], Assertions = assertions,
    SchemaChanged = false, Identity = true, Clarion = true, DurableQueue = true };
await File.WriteAllTextAsync(Path.Combine(directory, phase + "-result.json"), JsonSerializer.Serialize(result));
Console.WriteLine(JsonSerializer.Serialize(result));

static async Task<string> Schema(string path)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name";
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<string>();
    while (await reader.ReadAsync()) rows.Add(reader.GetString(0) + ":" + reader.GetString(1) + ":" + reader.GetString(2));
    return string.Join("\n", rows);
}

public sealed class AppDb(DbContextOptions<AppDb> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Receipt>().HasKey(receipt => receipt.JobId);
        builder.ApplyClarionConventions();
    }
}
public sealed class Note : ITimestamped, ISoftDeletable
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
public sealed class Receipt
{
    public Guid JobId { get; set; }
    public string OwnerId { get; set; } = "";
    public int Quantity { get; set; }
}
public sealed record RecordQuantity(int Quantity, bool InitiallyFails = false);
public sealed class HandlerOptions { public bool RecoverFailure { get; init; } }
public sealed class RecordQuantityHandler(AppDb db, HandlerOptions options) : IJobHandler<RecordQuantity>
{
    public async Task HandleAsync(RecordQuantity job, JobContext context, CancellationToken token)
    {
        if (job.InitiallyFails && !options.RecoverFailure) throw new InvalidOperationException("Synthetic handler failure.");
        var existing = await db.Receipts.SingleOrDefaultAsync(receipt => receipt.JobId == context.JobId, token);
        if (existing is not null)
        {
            if (existing.Quantity != job.Quantity || existing.OwnerId != context.TenantId)
                throw new InvalidOperationException("Conflicting result envelope.");
            return;
        }
        db.Receipts.Add(new Receipt { JobId = context.JobId, OwnerId = context.TenantId, Quantity = job.Quantity });
        await db.SaveChangesAsync(token);
    }
}
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan interval) => now += interval;
}
public sealed class Baseline
{
    public DateTimeOffset Time { get; set; }
    public DateTimeOffset NoteCreatedAt { get; set; }
    public string PasswordHash { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public string AuthenticatorKey { get; set; } = "";
    public string[] RecoveryCodes { get; set; } = [];
    public string ResetToken { get; set; } = "";
    public string Cookie { get; set; } = "";
    public string IdentitySchema { get; set; } = "";
    public string QueueSchema { get; set; } = "";
    public Guid Completed { get; set; }
    public Guid LostAck { get; set; }
    public Guid Pending { get; set; }
    public Guid Dead { get; set; }
}
