using Caravel.Mail;
using Caravel.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caravel.Notifications;

public sealed record NotificationContent(string Subject, string Text, string? Html = null);
public sealed record NotificationRecipient(string? Email = null, string? PhoneNumber = null);

/// <summary>Application-owned routing; a channel must validate its destination before delivery.</summary>
public interface INotificationChannel
{
    Task SendAsync(NotificationContent content, NotificationRecipient recipient, CancellationToken cancellationToken = default);
}

/// <summary>Send one channel at a time so failures do not implicitly resend other channels.</summary>
public sealed class NotificationDispatcher(IServiceProvider services)
{
    public Task SendAsync(string channel, NotificationContent content, NotificationRecipient recipient,
        CancellationToken cancellationToken = default)
    {
        Validate(channel, content, recipient);
        return services.GetRequiredKeyedService<INotificationChannel>(channel).SendAsync(content, recipient, cancellationToken);
    }

    internal void Validate(string channel, NotificationContent content, NotificationRecipient recipient)
    {
        NotificationServices.ValidateChannel(channel);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(recipient);
        if (string.IsNullOrWhiteSpace(content.Subject) || content.Subject.Length > 256 || content.Subject.Any(char.IsControl))
            throw new ArgumentException("A subject of at most 256 characters without control characters is required.", nameof(content));
        if (string.IsNullOrWhiteSpace(content.Text) || content.Text.Length > 32 * 1024 || content.Html?.Length > 32 * 1024)
            throw new ArgumentException("Notification text is required and each body must be at most 32768 characters.", nameof(content));
        _ = services.GetRequiredKeyedService<INotificationChannel>(channel);
    }
}

public sealed class MailNotificationChannel(IMailSender mail) : INotificationChannel
{
    public Task SendAsync(NotificationContent content, NotificationRecipient recipient, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email)) throw new ArgumentException("An email destination is required.", nameof(recipient));
        return mail.SendAsync(new CaravelMailMessage
        {
            To = [recipient.Email], Subject = content.Subject, TextBody = content.Text, HtmlBody = content.Html
        }, cancellationToken);
    }
}

public sealed class SmsNotificationChannel(ISmsSender sms) : INotificationChannel
{
    public Task SendAsync(NotificationContent content, NotificationRecipient recipient, CancellationToken cancellationToken = default)
        => sms.SendAsync(new SmsMessage(recipient.PhoneNumber ?? "", content.Text), cancellationToken);
}

/// <summary>Versioned payload for one delivery; addresses and content are stored in the queue.</summary>
public sealed record NotificationDeliveryJob(string Channel, NotificationContent Content, NotificationRecipient Recipient);

public sealed class NotificationDeliveryHandler(NotificationDispatcher dispatcher) : IJobHandler<NotificationDeliveryJob>
{
    public Task HandleAsync(NotificationDeliveryJob job, JobContext context, CancellationToken cancellationToken)
        => dispatcher.SendAsync(job.Channel, job.Content, job.Recipient, cancellationToken);
}

public sealed class QueuedNotifications(IDatabaseQueue queue, NotificationDispatcher dispatcher)
{
    /// <summary>Use a distinct idempotency key for each channel. This is a trusted infrastructure API.</summary>
    public Task<EnqueueResult> EnqueueAsync(string channel, NotificationContent content, NotificationRecipient recipient,
        QueueDispatchOptions options, CancellationToken cancellationToken = default)
    {
        dispatcher.Validate(channel, content, recipient);
        return queue.EnqueueAsync(new NotificationDeliveryJob(channel, content, recipient), options, cancellationToken);
    }
}

public static class NotificationServices
{
    public static IServiceCollection AddCaravelNotificationChannel<TChannel>(this IServiceCollection services, string channel)
        where TChannel : class, INotificationChannel
    {
        ValidateChannel(channel);
        if (services.Any(entry => entry.ServiceType == typeof(INotificationChannel) && Equals(entry.ServiceKey, channel)))
            throw new InvalidOperationException("A notification channel with that name is already registered.");
        services.TryAddScoped<NotificationDispatcher>();
        services.AddKeyedScoped<INotificationChannel, TChannel>(channel);
        return services;
    }

    public static IServiceCollection AddCaravelMailNotifications(this IServiceCollection services)
        => services.AddCaravelNotificationChannel<MailNotificationChannel>("mail");

    public static IServiceCollection AddCaravelSmsNotifications(this IServiceCollection services)
        => services.AddCaravelNotificationChannel<SmsNotificationChannel>("sms");

    public static IServiceCollection AddCaravelQueuedNotifications(this IServiceCollection services)
    {
        services.AddScoped<QueuedNotifications>();
        return services.AddQueueJob<NotificationDeliveryJob, NotificationDeliveryHandler>("caravel.notification.deliver.v1");
    }

    internal static void ValidateChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 64
            || channel.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw new ArgumentException("Channel names use up to 64 lowercase letters, digits, dots, hyphens or underscores.", nameof(channel));
    }
}
