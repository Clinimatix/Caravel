using System.Text;
using MimeKit;

namespace Caravel.Mail;

public interface IMailSender
{
    Task SendAsync(CaravelMailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Message content, without transport credentials or application-controlled sender identity.</summary>
public sealed record CaravelMailMessage
{
    public required IReadOnlyList<string> To { get; init; }
    public required string Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
    public string? ReplyTo { get; init; }
    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];
}

/// <summary>Inline bytes only. Sending never opens an attachment path or downloads a URL.</summary>
public sealed record MailAttachment(string FileName, byte[] Content, string ContentType = "application/octet-stream");

public sealed class MailOptions
{
    public string From { get; set; } = "";
    public int MaxRecipients { get; set; } = 100;
    public int MaxMessageBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxAttachments { get; set; } = 10;

    internal MailOptions ValidatedCopy()
    {
        MailValidation.Address(From);
        if (MaxRecipients is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(MaxRecipients));
        if (MaxMessageBytes is < 1 or > 25 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
        if (MaxAttachments is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(MaxAttachments));
        return (MailOptions)MemberwiseClone();
    }
}

/// <summary>A redacted transport failure. SMTP acceptance is not proof of recipient delivery.</summary>
public sealed class MailDeliveryException() : Exception("The mail transport did not confirm delivery. Retrying may send a duplicate message.");

internal static class MailValidation
{
    public static CaravelMailMessage Snapshot(CaravelMailMessage message, MailOptions options)
    {
        ArgumentNullException.ThrowIfNull(message);
        Header(message.Subject, 512);
        ArgumentNullException.ThrowIfNull(message.To);
        ArgumentNullException.ThrowIfNull(message.Cc);
        ArgumentNullException.ThrowIfNull(message.Bcc);
        ArgumentNullException.ThrowIfNull(message.Attachments);
        if (message.To.Count == 0 || (long)message.To.Count + message.Cc.Count + message.Bcc.Count > options.MaxRecipients)
            throw new ArgumentException("The message requires a primary recipient and must fit the recipient limit.");
        if (message.TextBody is null && message.HtmlBody is null)
            throw new ArgumentException("A text or HTML body is required.");
        if (message.Attachments.Count > options.MaxAttachments)
            throw new ArgumentException("The message exceeds the attachment count limit.");
        long size = Encoding.UTF8.GetByteCount(message.Subject)
            + (long)Encoding.UTF8.GetByteCount(message.TextBody ?? "") + Encoding.UTF8.GetByteCount(message.HtmlBody ?? "");
        string[] Addresses(IReadOnlyList<string> values) => values.Select(value =>
        {
            Address(value);
            size += Encoding.UTF8.GetByteCount(value);
            return value;
        }).ToArray();
        var to = Addresses(message.To);
        var cc = Addresses(message.Cc);
        var bcc = Addresses(message.Bcc);
        if (message.ReplyTo is { } replyTo) { Address(replyTo); size += Encoding.UTF8.GetByteCount(replyTo); }
        foreach (var attachment in message.Attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment);
            Header(attachment.FileName, 255);
            if (attachment.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || attachment.FileName is "." or "..")
                throw new ArgumentException("Attachment names must be filenames without directory components.");
            Header(attachment.ContentType, 128);
            if (!ContentType.TryParse(attachment.ContentType, out var parsed) || parsed.Parameters.Count != 0)
                throw new ArgumentException("An attachment requires a media type without parameters.");
            ArgumentNullException.ThrowIfNull(attachment.Content);
            size += attachment.Content.LongLength + Encoding.UTF8.GetByteCount(attachment.FileName) + Encoding.UTF8.GetByteCount(attachment.ContentType);
        }
        if (size > options.MaxMessageBytes) throw new ArgumentException("The message exceeds the configured content byte limit.");
        return message with { To = to, Cc = cc, Bcc = bcc,
            Attachments = message.Attachments.Select(a => a with { Content = a.Content.ToArray() }).ToArray() };
    }

    public static void Address(string address)
    {
        Header(address, 320);
        if (!MailboxAddress.TryParse(address, out var parsed) || parsed.Address != address || !address.Contains('@'))
            throw new ArgumentException("Use one bare mailbox address without a display name.");
    }

    private static void Header(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new ArgumentException("Mail header values must be bounded, nonblank and free of control characters.");
    }
}
