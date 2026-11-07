using Caravel.Mail;
using Caravel.Notifications;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Notifications.Tests;

public sealed class TransactionalDeliveryTests
{
    private static CaravelMailMessage Message => new()
    { To = ["reader@example.test"], Subject = "Synthetic invitation", TextBody = "Open the synthetic workspace." };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_discards_intent_and_commit_relays_once(bool notification)
    {
        await using var fixture = await Fixture.Create();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var app = scope.ServiceProvider.GetRequiredService<ApplicationDatabase>();
            await using var transaction = await app.Database.BeginTransactionAsync();
            await Stage(scope.ServiceProvider, notification, "rollback");
            await app.SaveChangesAsync();
            Assert.Empty(fixture.Sender.Capture.Messages);
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await fixture.Services.GetRequiredService<OutboxRelay<ApplicationDatabase>>().RunOnceAsync());
        Assert.False(await fixture.Services.GetRequiredService<QueueWorker>().RunOnceAsync("mail"));
        OutboxStageResult staged;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var app = scope.ServiceProvider.GetRequiredService<ApplicationDatabase>();
            staged = await Stage(scope.ServiceProvider, notification, "committed");
            Assert.True((await Stage(scope.ServiceProvider, notification, "committed")).AlreadyStaged);
            Assert.Equal(0, await fixture.Services.GetRequiredService<OutboxRelay<ApplicationDatabase>>().RunOnceAsync());
            await app.SaveChangesAsync();
        }
        var relay = fixture.Services.GetRequiredService<OutboxRelay<ApplicationDatabase>>();
        Assert.Equal(1, await relay.RunOnceAsync());
        Assert.Equal(0, await relay.RunOnceAsync());
        var worker = fixture.Services.GetRequiredService<QueueWorker>();
        Assert.True(await worker.RunOnceAsync("mail"));
        Assert.False(await worker.RunOnceAsync("mail"));
        Assert.Single(fixture.Sender.Capture.Messages);
        var status = await fixture.Status(staged.OutboxId);
        Assert.Equal(QueueJobState.Completed, status.State);
        Assert.Equal(1, status.Attempts);
        Assert.Null(await fixture.Services.GetRequiredService<IDatabaseQueue>().GetStatusAsync(staged.OutboxId, "mail", "other-tenant"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_transport_retries_then_dead_letters_and_explicit_replay_recovers(bool notification)
    {
        await using var fixture = await Fixture.Create();
        fixture.Sender.Fail = true;
        OutboxStageResult staged;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            staged = await Stage(scope.ServiceProvider, notification, "retry");
            await scope.ServiceProvider.GetRequiredService<ApplicationDatabase>().SaveChangesAsync();
        }
        await fixture.Services.GetRequiredService<OutboxRelay<ApplicationDatabase>>().RunOnceAsync();
        var worker = fixture.Services.GetRequiredService<QueueWorker>();
        Assert.True(await worker.RunOnceAsync("mail"));
        var pending = await fixture.Status(staged.OutboxId);
        Assert.Equal(QueueJobState.Pending, pending.State);
        Assert.Equal(QueueFailure.HandlerFailed, pending.LastFailure);
        Assert.Empty(fixture.Sender.Capture.Messages);
        Assert.False(await worker.RunOnceAsync("mail"));
        fixture.Clock.Now += TimeSpan.FromSeconds(2);
        Assert.True(await worker.RunOnceAsync("mail"));
        var failed = await fixture.Status(staged.OutboxId);
        Assert.Equal(QueueJobState.DeadLetter, failed.State);
        Assert.Equal(2, failed.Attempts);
        Assert.DoesNotContain("private diagnostic", failed.ToString());
        var queue = fixture.Services.GetRequiredService<IDatabaseQueue>();
        Assert.False(await queue.ReplayAsync(staged.OutboxId, "mail", "other-tenant"));
        fixture.Sender.Fail = false;
        Assert.True(await queue.ReplayAsync(staged.OutboxId, "mail", "tenant-a"));
        Assert.True(await worker.RunOnceAsync("mail"));
        Assert.Single(fixture.Sender.Capture.Messages);
        var completed = await fixture.Status(staged.OutboxId);
        Assert.Equal(QueueJobState.Completed, completed.State);
        Assert.Equal(1, completed.ReplayCount);
    }

    [Fact]
    public async Task Invalid_mail_and_cancelled_staging_leave_no_tracked_intent()
    {
        await using var fixture = await Fixture.Create();
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var mail = services.GetRequiredService<QueuedMail>();
        var outbox = services.GetRequiredService<QueueOutbox<ApplicationDatabase>>();
        await Assert.ThrowsAsync<ArgumentException>(() => mail.StageAsync(outbox, Message with { To = [] }, new("mail", "tenant-a", "bad")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mail.StageAsync(outbox, Message, new("mail", "tenant-a", "cancelled"), new(true)));
        var notifications = services.GetRequiredService<QueuedNotifications>();
        await Assert.ThrowsAsync<ArgumentException>(() => notifications.StageAsync(outbox, "mail", new("", "body"), new("reader@example.test"), new("mail", "tenant-a", "bad-notification")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notifications.StageAsync(outbox, "mail", new("subject", "body"), new("reader@example.test"), new("mail", "tenant-a", "cancelled-notification"), new(true)));
        Assert.Empty(services.GetRequiredService<ApplicationDatabase>().ChangeTracker.Entries<OutboxMessage>());
    }

    [Fact]
    public async Task Staged_mail_is_a_snapshot_and_reused_identity_rejects_changed_content()
    {
        await using var fixture = await Fixture.Create();
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var recipients = new[] { "reader@example.test" };
        var bytes = new byte[] { 1, 2, 3 };
        var message = Message with { To = recipients, Attachments = [new("synthetic.bin", bytes)] };
        var outbox = services.GetRequiredService<QueueOutbox<ApplicationDatabase>>();
        var dispatch = new QueueDispatchOptions("mail", "tenant-a", "snapshot");
        var mail = services.GetRequiredService<QueuedMail>();
        await mail.StageAsync(outbox, message, dispatch);
        recipients[0] = "changed@example.test";
        bytes[0] = 9;
        await Assert.ThrowsAsync<QueueIdempotencyConflictException>(() => mail.StageAsync(outbox, message, dispatch));
        await services.GetRequiredService<ApplicationDatabase>().SaveChangesAsync();
        await fixture.Services.GetRequiredService<OutboxRelay<ApplicationDatabase>>().RunOnceAsync();
        await fixture.Services.GetRequiredService<QueueWorker>().RunOnceAsync("mail");
        var captured = Assert.Single(fixture.Sender.Capture.Messages);
        Assert.Equal("reader@example.test", Assert.Single(captured.To));
        Assert.Equal((byte)1, Assert.Single(captured.Attachments).Content[0]);
    }
    private static Task<OutboxStageResult> Stage(IServiceProvider services, bool notification, string key)
    {
        var outbox = services.GetRequiredService<QueueOutbox<ApplicationDatabase>>();
        var dispatch = new QueueDispatchOptions("mail", "tenant-a", key);
        return notification
            ? services.GetRequiredService<QueuedNotifications>().StageAsync(outbox, "mail", new(Message.Subject, Message.TextBody!), new(Message.To[0]), dispatch)
            : services.GetRequiredService<QueuedMail>().StageAsync(outbox, Message, dispatch);
    }

    public sealed class ApplicationDatabase(DbContextOptions<ApplicationDatabase> options) : DbContext(options)
    { protected override void OnModelCreating(ModelBuilder builder) => builder.AddCaravelOutbox(); }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class SyntheticSender : IMailSender
    {
        public MailCapture Capture { get; } = new(new());
        public bool Fail { get; set; }
        public Task SendAsync(CaravelMailMessage message, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("private diagnostic");
            return Capture.SendAsync(message, cancellationToken);
        }
    }
    private sealed class Fixture(DirectoryInfo directory, ServiceProvider services, SyntheticSender sender, Clock clock) : IAsyncDisposable
    {
        public ServiceProvider Services => services;
        public SyntheticSender Sender => sender;
        public Clock Clock => clock;
        public async Task<QueueJobStatus> Status(Guid id) => (await services.GetRequiredService<IDatabaseQueue>().GetStatusAsync(id, "mail", "tenant-a"))!;
        public static async Task<Fixture> Create()
        {
            var directory = Directory.CreateTempSubdirectory("caravel-transactional-mail-");
            var services = new ServiceCollection();
            var clock = new Clock();
            var sender = new SyntheticSender();
            services.AddSingleton<TimeProvider>(clock);
            services.AddCaravelMailCapture(options => options.From = "sender@example.test").AddCaravelQueuedMail();
            services.AddSingleton<IMailSender>(sender);
            services.AddDbContextFactory<ApplicationDatabase>(options => options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "app.db")};Pooling=False"));
            services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "queue.db")};Pooling=False"));
            services.AddCaravelDatabaseQueue(options => { options.MaxAttempts = 2; options.RetryDelay = TimeSpan.FromSeconds(1); });
            services.AddCaravelOutbox<ApplicationDatabase>();
            services.AddCaravelMailNotifications().AddCaravelQueuedNotifications();
            var provider = services.BuildServiceProvider(validateScopes: true);
            await using (var app = await provider.GetRequiredService<IDbContextFactory<ApplicationDatabase>>().CreateDbContextAsync())
                await app.Database.EnsureCreatedAsync();
            await using (var queue = await provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync())
                await queue.Database.EnsureCreatedAsync();
            return new(directory, provider, sender, clock);
        }
        public async ValueTask DisposeAsync() { await services.DisposeAsync(); directory.Delete(true); }
    }
}