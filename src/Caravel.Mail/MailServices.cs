using System.Collections.Concurrent;
using Caravel.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace Caravel.Mail;

public static class MailServiceExtensions
{
    public static IServiceCollection AddCaravelSmtpMail(this IServiceCollection services,
        Action<SmtpMailOptions> configureSmtp, Action<MailOptions> configureMail)
    {
        var smtp = new SmtpMailOptions();
        configureSmtp(smtp);
        services.AddSingleton(smtp.ValidatedCopy());
        AddOptions(services, configureMail);
        services.AddSingleton<IMailSender, SmtpMailSender>();
        return services;
    }

    /// <summary>Capture messages in memory without network delivery. Explicit development/test registration only.</summary>
    public static IServiceCollection AddCaravelMailCapture(this IServiceCollection services, Action<MailOptions> configure)
    {
        AddOptions(services, configure);
        services.AddSingleton<MailCapture>();
        services.AddSingleton<IMailSender>(provider => provider.GetRequiredService<MailCapture>());
        return services;
    }

    /// <summary>Add mail jobs to an independently configured database queue. A worker and schema are still required.</summary>
    public static IServiceCollection AddCaravelQueuedMail(this IServiceCollection services)
    {
        services.AddQueueJob<SendMailJob, SendMailJobHandler>("caravel.mail.send.v1");
        services.AddSingleton<QueuedMail>();
        return services;
    }

    private static void AddOptions(IServiceCollection services, Action<MailOptions> configure)
    {
        var options = new MailOptions();
        configure(options);
        services.AddSingleton(options.ValidatedCopy());
    }
}

/// <summary>Bounded in-memory development capture. Returned messages are defensive snapshots.</summary>
public sealed class MailCapture(MailOptions options) : IMailSender
{
    private readonly ConcurrentQueue<CaravelMailMessage> messages = new();
    private readonly object gate = new();
    private long capturedBytes;
    public IReadOnlyList<CaravelMailMessage> Messages => messages.Select(message => MailValidation.Snapshot(message, options)).ToArray();

    public Task SendAsync(CaravelMailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = MailValidation.Snapshot(message, options);
        var bytes = (long)(snapshot.TextBody?.Length ?? 0) * sizeof(char)
            + (long)(snapshot.HtmlBody?.Length ?? 0) * sizeof(char) + snapshot.Attachments.Sum(attachment => attachment.Content.LongLength);
        lock (gate)
        {
            // ponytail: bounded process-local capture; use a dedicated test mail service for shared capture.
            if (messages.Count >= 100 || capturedBytes + bytes > 25 * 1024 * 1024)
                throw new InvalidOperationException("Mail capture is full. Clear it before capturing more messages.");
            messages.Enqueue(snapshot);
            capturedBytes += bytes;
        }
        return Task.CompletedTask;
    }

    public void Clear() { lock (gate) { messages.Clear(); capturedBytes = 0; } }
}

public sealed class QueuedMail(IDatabaseQueue queue, MailOptions options)
{
    public Task<EnqueueResult> EnqueueAsync(CaravelMailMessage message, QueueDispatchOptions dispatch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return queue.EnqueueAsync(new SendMailJob(MailValidation.Snapshot(message, options)), dispatch, cancellationToken);
    }
}

/// <summary>Version-one persisted wire payload. It contains message content, never transport secrets.</summary>
public sealed record SendMailJob(CaravelMailMessage Message);

internal sealed class SendMailJobHandler(IMailSender sender) : IJobHandler<SendMailJob>
{
    public Task HandleAsync(SendMailJob job, JobContext context, CancellationToken cancellationToken) =>
        sender.SendAsync(job.Message, cancellationToken);
}
