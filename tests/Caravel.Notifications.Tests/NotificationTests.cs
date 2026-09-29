using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Caravel.Mail;
using Caravel.Notifications;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Notifications.Tests;

public sealed class NotificationTests
{
    [Fact]
    public async Task Capture_has_a_fixed_memory_bound_and_can_be_cleared()
    {
        var capture = new CaptureSmsSender();
        var message = new SmsMessage("+15555550123", "Synthetic alert");
        for (var index = 0; index < 100; index++) await capture.SendAsync(message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.SendAsync(message));
        Assert.Equal(100, capture.Messages.Count);
        capture.Clear();
        await capture.SendAsync(message);
        Assert.Single(capture.Messages);
    }

    [Fact]
    public async Task Channels_are_explicit_and_capture_transports_never_contact_a_provider()
    {
        var mail = new MailCapture();
        var services = new ServiceCollection();
        services.AddSingleton<IMailSender>(mail);
        services.AddCaravelMailNotifications().AddCaravelSmsCapture().AddCaravelSmsNotifications();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        var content = new NotificationContent("Report ready", "Your report is ready.", "<p>Your report is ready.</p>");
        var recipient = new NotificationRecipient("reader@example.invalid", "+15555550123");
        await dispatcher.SendAsync("mail", content, recipient);
        Assert.Single(mail.Messages);
        var capture = provider.GetRequiredService<CaptureSmsSender>();
        Assert.Empty(capture.Messages);
        await dispatcher.SendAsync("sms", content, recipient);
        Assert.Equal(new SmsMessage(recipient.PhoneNumber!, content.Text), Assert.Single(capture.Messages));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.SendAsync("unknown", content, recipient));
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.SendAsync("sms", content, new(PhoneNumber: "not-a-number")));
        Assert.Single(capture.Messages);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.SendAsync("sms", content, recipient, cancelled.Token));
        Assert.Single(capture.Messages);
    }

    [Fact]
    public async Task Each_channel_retries_independently_and_duplicate_acceptance_does_not_repeat_delivery()
    {
        var directory = Directory.CreateTempSubdirectory("caravel-notification-tests-");
        try
        {
            var services = new ServiceCollection();
            var clock = new TestClock();
            var mail = new MailCapture();
            var sms = new FailOnceSms();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IMailSender>(mail);
            services.AddSingleton<ISmsSender>(sms);
            services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(directory.FullName, "queue.db"), Pooling = false }.ToString()));
            services.AddCaravelDatabaseQueue(options => options.RetryDelay = TimeSpan.FromSeconds(1));
            services.AddCaravelMailNotifications().AddCaravelSmsNotifications().AddCaravelQueuedNotifications();
            await using var provider = services.BuildServiceProvider(validateScopes: true);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync())
                await db.Database.EnsureCreatedAsync();
            using var scope = provider.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<QueuedNotifications>();
            var content = new NotificationContent("Ready", "Your synthetic report is ready.");
            var recipient = new NotificationRecipient("reader@example.invalid", "+15555550123");
            var options = new QueueDispatchOptions("notifications", "tenant-a", "report-1-mail");
            var accepted = await queue.EnqueueAsync("mail", content, recipient, options);
            var duplicate = await queue.EnqueueAsync("mail", content, recipient, options);
            Assert.True(duplicate.AlreadyEnqueued);
            Assert.Equal(accepted.JobId, duplicate.JobId);
            await queue.EnqueueAsync("sms", content, recipient, options with { IdempotencyKey = "report-1-sms" });
            var worker = provider.GetRequiredService<QueueWorker>();
            Assert.True(await worker.RunOnceAsync("notifications"));
            Assert.True(await worker.RunOnceAsync("notifications"));
            Assert.Single(mail.Messages);
            Assert.Empty(sms.Messages);
            clock.Now += TimeSpan.FromSeconds(2);
            Assert.True(await worker.RunOnceAsync("notifications"));
            Assert.False(await worker.RunOnceAsync("notifications"));
            Assert.Single(mail.Messages);
            Assert.Single(sms.Messages);
            Assert.True((await queue.EnqueueAsync("mail", content, recipient, options)).AlreadyEnqueued);
            Assert.False(await worker.RunOnceAsync("notifications"));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("+15555550123", "")]
    [InlineData("5555550123", "hello")]
    [InlineData("+05555550123", "hello")]
    [InlineData("+15555550123\r\n", "hello")]
    public async Task Invalid_sms_never_enters_capture(string phone, string body)
    {
        var capture = new CaptureSmsSender();
        await Assert.ThrowsAsync<ArgumentException>(() => capture.SendAsync(new(phone, body)));
        Assert.Empty(capture.Messages);
    }

    [Fact]
    public async Task Twilio_request_uses_fixed_https_endpoint_form_encoding_and_no_provider_error_body()
    {
        string? capturedUri = null, capturedForm = null;
        AuthenticationHeaderValue? authorization = null;
        var status = HttpStatusCode.Created;
        using var client = new HttpClient(new StubHandler(async (request, token) =>
        {
            capturedUri = request.RequestUri!.AbsoluteUri;
            capturedForm = await request.Content!.ReadAsStringAsync(token);
            authorization = request.Headers.Authorization;
            Assert.Equal(HttpMethod.Post, request.Method);
            return new HttpResponseMessage(status) { Content = new StringContent("private provider diagnostic") };
        }));
        var options = new TwilioSmsOptions { AccountSid = "AC" + new string('a', 32), AuthToken = "synthetic-token", From = "+15555550100" };
        var sender = new TwilioSmsSender(client, options);
        await sender.SendAsync(new("+15555550123", "A & B"));
        Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{options.AccountSid}/Messages.json", capturedUri);
        Assert.Contains("To=%2B15555550123", capturedForm);
        Assert.Contains("Body=A+%26+B", capturedForm);
        Assert.Equal("Basic", authorization!.Scheme);
        Assert.Equal(options.AccountSid + ":synthetic-token", Encoding.ASCII.GetString(Convert.FromBase64String(authorization.Parameter!)));
        status = HttpStatusCode.TooManyRequests;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(new("+15555550123", "hello")));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("private provider diagnostic", error.ToString());
        Assert.DoesNotContain(options.AuthToken, error.ToString());
        status = HttpStatusCode.TemporaryRedirect;
        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(new("+15555550123", "hello")));
    }

    [Fact]
    public async Task Twilio_cancellation_interrupts_an_in_flight_http_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));
        var sender = new TwilioSmsSender(client, new TwilioSmsOptions
        { AccountSid = "AC" + new string('a', 32), AuthToken = "synthetic-token", From = "+15555550100" });
        using var cancelled = new CancellationTokenSource();
        var sending = sender.SendAsync(new("+15555550123", "Synthetic alert"), cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Invalid_options_and_duplicate_channel_names_fail_at_registration()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddCaravelTwilioSms(options => options.AccountSid = "../other"));
        services.AddCaravelMailNotifications();
        Assert.Throws<InvalidOperationException>(() => services.AddCaravelMailNotifications());
        Assert.Throws<ArgumentException>(() => services.AddCaravelNotificationChannel<MailNotificationChannel>("mail\n"));
    }

    private sealed class MailCapture : IMailSender
    {
        public List<CaravelMailMessage> Messages { get; } = [];
        public Task SendAsync(CaravelMailMessage message, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Messages.Add(message); return Task.CompletedTask; }
    }

    private sealed class FailOnceSms : ISmsSender
    {
        private bool failed;
        public List<SmsMessage> Messages { get; } = [];
        public Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
        {
            if (!failed) { failed = true; throw new HttpRequestException("Synthetic transient failure."); }
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
