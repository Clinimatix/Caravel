using System.Net;
using MailKit.Security;
using MimeKit;

namespace Caravel.Mail;

public enum SmtpMailSecurity { StartTls, TlsOnConnect, InsecureLoopback }

public sealed class SmtpMailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public SmtpMailSecurity Security { get; set; } = SmtpMailSecurity.StartTls;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    internal SmtpMailOptions ValidatedCopy()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsControl)
            || Uri.CheckHostName(Host) == UriHostNameType.Unknown) throw new ArgumentException("A valid SMTP host is required.");
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (!Enum.IsDefined(Security)) throw new ArgumentOutOfRangeException(nameof(Security));
        if (Security == SmtpMailSecurity.InsecureLoopback && (!IPAddress.TryParse(Host, out var address) || !IPAddress.IsLoopback(address)))
            throw new ArgumentException("Unencrypted SMTP is allowed only with an explicit loopback IP address.");
        if ((UserName is null) != (Password is null) || UserName is { Length: 0 } || Password is { Length: 0 })
            throw new ArgumentException("SMTP authentication requires both a user name and password.");
        if (Security == SmtpMailSecurity.InsecureLoopback && UserName is not null)
            throw new ArgumentException("SMTP credentials require encrypted transport.");
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(Timeout));
        return (SmtpMailOptions)MemberwiseClone();
    }
}

internal sealed class SmtpMailSender(MailOptions options, SmtpMailOptions smtp) : IMailSender
{
    public async Task SendAsync(CaravelMailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = MailValidation.Snapshot(message, options);
        using var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(options.From));
        foreach (var address in snapshot.To) mime.To.Add(MailboxAddress.Parse(address));
        foreach (var address in snapshot.Cc) mime.Cc.Add(MailboxAddress.Parse(address));
        foreach (var address in snapshot.Bcc) mime.Bcc.Add(MailboxAddress.Parse(address));
        if (snapshot.ReplyTo is { } replyTo) mime.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        mime.Subject = snapshot.Subject;
        var builder = new BodyBuilder { TextBody = snapshot.TextBody, HtmlBody = snapshot.HtmlBody };
        foreach (var attachment in snapshot.Attachments)
            builder.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        mime.Body = builder.ToMessageBody();
        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = checked((int)smtp.Timeout.TotalMilliseconds) };
        using var timeout = new CancellationTokenSource(smtp.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var security = smtp.Security switch
        {
            SmtpMailSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpMailSecurity.TlsOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.None
        };
        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, security, linked.Token).ConfigureAwait(false);
            if (smtp.UserName is not null)
                await client.AuthenticateAsync(smtp.UserName, smtp.Password!, linked.Token).ConfigureAwait(false);
            await client.SendAsync(mime, linked.Token).ConfigureAwait(false);
            // A failed QUIT after successful acceptance must not turn delivery into an avoidable duplicate retry.
            try { await client.DisconnectAsync(true, linked.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or MailKit.ProtocolException or MailKit.CommandException or OperationCanceledException) { }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new MailDeliveryException(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or MailKit.ProtocolException or MailKit.CommandException
            or System.Net.Sockets.SocketException or AuthenticationException or System.Security.Authentication.AuthenticationException
            or NotSupportedException)
        {
            // Server diagnostics can contain recipients, credentials or content. Keep the public failure redacted.
            throw new MailDeliveryException();
        }
    }
}
