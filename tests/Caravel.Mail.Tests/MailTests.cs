using System.Net;
using System.Net.Sockets;
using System.Text;
using Caravel.Queues;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Xunit;

namespace Caravel.Mail.Tests;

public sealed class MailTests
{
    private static CaravelMailMessage Message => new()
    {
        To = ["reader@example.test"], Subject = "A report", TextBody = "Plain text",
        HtmlBody = "<p>Report</p>", Attachments = [new("report.txt", Encoding.UTF8.GetBytes("Synthetic report"), "text/plain")]
    };

    [Fact]
    public async Task CaptureValidatesAndDefensivelyCopiesAttachmentsAndRecipients()
    {
        await using var provider = new ServiceCollection().AddCaravelMailCapture(o => o.From = "sender@example.test").BuildServiceProvider();
        var message = Message;
        await provider.GetRequiredService<IMailSender>().SendAsync(message);
        message.Attachments[0].Content[0] = 0;
        var capture = provider.GetRequiredService<MailCapture>();
        Assert.Equal((byte)'S', Assert.Single(capture.Messages).Attachments[0].Content[0]);
        capture.Messages[0].Attachments[0].Content[0] = 0;
        Assert.Equal((byte)'S', capture.Messages[0].Attachments[0].Content[0]);
        capture.Clear();
        Assert.Empty(capture.Messages);
    }

    [Theory]
    [InlineData("recipient@example.test\r\nBcc: victim@example.test")]
    [InlineData("name <recipient@example.test>")]
    [InlineData("one@example.test,two@example.test")]
    [InlineData("invalid")]
    public async Task InvalidAddressesAreRejectedBeforeDelivery(string recipient)
    {
        var capture = new MailCapture(new());
        await Assert.ThrowsAsync<ArgumentException>(() => capture.SendAsync(Message with { To = [recipient] }));
        Assert.Empty(capture.Messages);
    }

    [Fact]
    public async Task HeaderInjectionPathsAndSizeLimitsAreRejected()
    {
        var capture = new MailCapture(new() { MaxMessageBytes = 128 });
        foreach (var message in new[]
        {
            Message with { Subject = "hello\r\nBcc: attacker@example.test" },
            Message with { Attachments = [new("../private.txt", [1])] },
            Message with { Attachments = [new("report.txt", [1], "text/plain; charset=utf-8")] },
            Message with { TextBody = new string('x', 200) },
            Message with { To = [] },
            Message with { TextBody = null, HtmlBody = null }
        }) await Assert.ThrowsAsync<ArgumentException>(() => capture.SendAsync(message));
        Assert.Empty(capture.Messages);
    }

    [Fact]
    public async Task CaptureHonorsCancellationAndItsStorageBound()
    {
        var capture = new MailCapture(new());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.SendAsync(Message, new(true)));
        Assert.Empty(capture.Messages);
        for (var index = 0; index < 100; index++) await capture.SendAsync(Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.SendAsync(Message));
        Assert.Equal(100, capture.Messages.Count);
    }

    [Fact]
    public void TemplatesEncodeValuesWithoutRecursionAndBoundExpandedOutput()
    {
        var values = new Dictionary<string, string> { ["name"] = "<script>\"&{{other}}" };
        Assert.Equal("<p>&lt;script&gt;&quot;&amp;{{other}}</p>", MailTemplate.RenderHtml("<p>{{name}}</p>", values));
        Assert.Equal("Hello <script>\"&{{other}}", MailTemplate.RenderText("Hello {{name}}", values));
        Assert.Throws<ArgumentException>(() => MailTemplate.RenderHtml("{{missing}}", values));
        Assert.Throws<ArgumentException>(() => MailTemplate.RenderText(string.Concat(Enumerable.Repeat("{{name}}", 17)),
            new Dictionary<string, string> { ["name"] = new string('x', 65536) }));
    }

    [Fact]
    public void SmtpRequiresExplicitTlsAndRejectsInsecureRemoteHostsOrCredentials()
    {
        Assert.Equal(SmtpMailSecurity.StartTls, new SmtpMailOptions().Security);
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddCaravelSmtpMail(
            o => { o.Host = "mail.example.test"; o.Security = SmtpMailSecurity.InsecureLoopback; }, m => m.From = "sender@example.test"));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddCaravelSmtpMail(
            o => { o.Host = "127.0.0.1"; o.Security = SmtpMailSecurity.InsecureLoopback; o.UserName = "user"; o.Password = "synthetic"; },
            m => m.From = "sender@example.test"));
    }

    [Fact]
    public async Task SmtpSendsMimeAlternativesAttachmentsAndBccEnvelopeWithoutBccHeader()
    {
        using var server = new SmtpFixture();
        var exchange = server.ReceiveAsync();
        await using var provider = SmtpProvider(server.Port);
        await provider.GetRequiredService<IMailSender>().SendAsync(Message with { Bcc = ["hidden@example.test"] });
        var received = await exchange;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(received.Body));
        using var mime = await MimeMessage.LoadAsync(stream);
        Assert.Equal("sender@example.test", Assert.Single(mime.From.Mailboxes).Address);
        Assert.Equal("Plain text", Assert.IsType<string>(mime.TextBody).TrimEnd());
        Assert.Equal("<p>Report</p>", Assert.IsType<string>(mime.HtmlBody).TrimEnd());
        var attachment = Assert.IsAssignableFrom<MimePart>(Assert.Single(mime.Attachments));
        Assert.Equal("report.txt", attachment.FileName);
        using var bytes = new MemoryStream();
        Assert.NotNull(attachment.Content);
        await attachment.Content.DecodeToAsync(bytes);
        Assert.Equal("Synthetic report", Encoding.UTF8.GetString(bytes.ToArray()));
        Assert.Contains(received.Commands, command => command.Contains("hidden@example.test"));
        Assert.Empty(mime.Bcc);
    }

    [Fact]
    public async Task SmtpServerErrorsAreRedactedAndNeverCapturedAsSuccess()
    {
        using var server = new SmtpFixture();
        var exchange = server.ReceiveAsync(reject: true);
        await using var provider = SmtpProvider(server.Port);
        var exception = await Assert.ThrowsAsync<MailDeliveryException>(() => provider.GetRequiredService<IMailSender>().SendAsync(Message));
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("synthetic-secret", exception.ToString());
        await exchange;
    }

    [Fact]
    public async Task StartTlsDoesNotDowngradeWhenServerOffersOnlyPlaintext()
    {
        using var server = new SmtpFixture();
        var exchange = server.ReceiveAsync();
        await using var provider = new ServiceCollection().AddCaravelSmtpMail(
            o => { o.Host = "127.0.0.1"; o.Port = server.Port; }, o => o.From = "sender@example.test").BuildServiceProvider();
        await Assert.ThrowsAsync<MailDeliveryException>(() => provider.GetRequiredService<IMailSender>().SendAsync(Message));
        var received = await exchange;
        Assert.DoesNotContain(received.Commands, line => line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancellationInterruptsAConnectedSmtpServerThatNeverSendsAGreeting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var provider = SmtpProvider(((IPEndPoint)listener.LocalEndpoint).Port);
        using var cancelled = new CancellationTokenSource();
        var sending = provider.GetRequiredService<IMailSender>().SendAsync(Message, cancelled.Token);
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DurableSendingUsesRegisteredWireTypeAndIdempotencyWithPayloadLimits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(connection));
        services.AddCaravelDatabaseQueue(options => options.MaxPayloadBytes = 2048);
        services.AddCaravelMailCapture(options => options.From = "sender@example.test").AddCaravelQueuedMail();
        await using var provider = services.BuildServiceProvider();
        await using var database = await provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        await database.Database.EnsureCreatedAsync();
        var queued = provider.GetRequiredService<QueuedMail>();
        var accepted = await queued.EnqueueAsync(Message, new("mail", "tenant-a", "report-1"));
        Assert.True((await queued.EnqueueAsync(Message, new("mail", "tenant-a", "report-1"))).AlreadyEnqueued);
        Assert.Empty(provider.GetRequiredService<MailCapture>().Messages);
        Assert.Equal("caravel.mail.send.v1", (await database.Jobs.SingleAsync()).JobType);
        Assert.True(await provider.GetRequiredService<QueueWorker>().RunOnceAsync("mail"));
        Assert.Single(provider.GetRequiredService<MailCapture>().Messages);
        Assert.Equal(QueueJobState.Completed, (await provider.GetRequiredService<IDatabaseQueue>()
            .GetStatusAsync(accepted.JobId, "mail", "tenant-a"))!.State);
        await Assert.ThrowsAsync<ArgumentException>(() => queued.EnqueueAsync(Message with { TextBody = new string('x', 3000) },
            new("mail", "tenant-a", "too-large")));
        Assert.Single(await database.Jobs.ToListAsync());
    }

    [Fact]
    public async Task RejectedSmtpDeliveryBecomesDeadLetterWithoutPersistingServerDiagnostic()
    {
        using var server = new SmtpFixture();
        var exchange = server.ReceiveAsync(reject: true);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(connection));
        services.AddCaravelDatabaseQueue(options => options.MaxAttempts = 1);
        services.AddCaravelSmtpMail(o => { o.Host = "127.0.0.1"; o.Port = server.Port; o.Security = SmtpMailSecurity.InsecureLoopback; },
            o => o.From = "sender@example.test").AddCaravelQueuedMail();
        await using var provider = services.BuildServiceProvider();
        await using var database = await provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        await database.Database.EnsureCreatedAsync();
        var accepted = await provider.GetRequiredService<QueuedMail>().EnqueueAsync(Message, new("mail", "tenant-a", "failed"));
        Assert.True(await provider.GetRequiredService<QueueWorker>().RunOnceAsync("mail"));
        var status = await provider.GetRequiredService<IDatabaseQueue>().GetStatusAsync(accepted.JobId, "mail", "tenant-a");
        Assert.Equal(QueueJobState.DeadLetter, status!.State);
        Assert.Equal(QueueFailure.HandlerFailed, status.LastFailure);
        await exchange;
    }

    private static ServiceProvider SmtpProvider(int port) => new ServiceCollection().AddCaravelSmtpMail(
        o => { o.Host = "127.0.0.1"; o.Port = port; o.Security = SmtpMailSecurity.InsecureLoopback; },
        o => o.From = "sender@example.test").BuildServiceProvider();

    private sealed class SmtpFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public SmtpFixture() => listener.Start();
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public async Task<(string Body, List<string> Commands)> ReceiveAsync(bool reject = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost test SMTP");
            var commands = new List<string>();
            var body = new StringBuilder();
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                commands.Add(line);
                if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 Send message");
                    while (await reader.ReadLineAsync(timeout.Token) is { } content && content != ".") body.AppendLine(content);
                    await writer.WriteLineAsync(reject ? "550 synthetic-secret rejection" : "250 Accepted");
                    if (reject) break;
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                { await writer.WriteLineAsync("221 Goodbye"); break; }
                else await writer.WriteLineAsync("250 localhost");
            }
            return (body.ToString(), commands);
        }
        public void Dispose() => listener.Stop();
    }
}
