# Transactional mail

`Clinimatix.Caravel.Mail` sends transactional messages through SMTP, captures them during development, and uses Caravel's durable queue for background delivery. Messages support text and HTML alternatives, recipients, reply-to addresses and attachments. The package uses MailKit for SMTP and MIME handling.

## Send a message

Configure the transport and sender once. Keep credentials in your application's secret configuration.

```csharp
using Caravel.Mail;

builder.Services.AddCaravelSmtpMail(smtp =>
{
    smtp.Host = builder.Configuration["Mail:Host"]!;
    smtp.Port = 587;
    smtp.UserName = builder.Configuration["Mail:UserName"];
    smtp.Password = builder.Configuration["Mail:Password"];
}, mail => mail.From = "notifications@example.com");

// Resolve IMailSender through constructor injection or an endpoint parameter.
await sender.SendAsync(new CaravelMailMessage
{
    To = ["reader@example.com"],
    Subject = "Your report is ready",
    TextBody = "Your requested report is attached.",
    HtmlBody = "<p>Your requested report is attached.</p>",
    Attachments = [new MailAttachment("report.txt", reportBytes, "text/plain")]
}, cancellationToken);
```

STARTTLS is required by default: a server that does not offer it fails instead of silently sending plaintext. Select `SmtpMailSecurity.TlsOnConnect` for implicit TLS, typically on port 465. Normal certificate validation stays enabled. `InsecureLoopback` is an explicit test-only option restricted to a literal loopback IP address and cannot carry SMTP credentials. The configured timeout bounds the complete exchange, with a default of 30 seconds.

`From` belongs to server configuration. `To`, `Cc`, `Bcc` and `ReplyTo` accept bare mailbox addresses, without display names or comma-separated lists. Headers reject control characters. Attachments contain bytes, a filename and a media type; sending never opens a file path or downloads a URL supplied in a message. Applications must still authorize recipients and attachment content before sending.

The default limits are 100 recipients, 10 attachments and 10 MiB of content. Set `MailOptions.MaxRecipients`, `MaxAttachments` and `MaxMessageBytes` during registration. The content limit counts UTF-8 header/body content and raw attachment bytes; MIME encoding increases the actual SMTP size. Choose a lower limit when a delivery provider imposes a wire-size limit.

## Reuse a template

Templates use named `{{placeholders}}`, with no expressions, reflection or raw-value escape hatch.

```csharp
var values = new Dictionary<string, string> { ["name"] = displayName };
var html = MailTemplate.RenderHtml("<p>Hello {{name}}, your report is ready.</p>", values);
var text = MailTemplate.RenderText("Hello {{name}}, your report is ready.", values);
```

`RenderHtml` HTML-encodes substituted values. `RenderText` preserves them. Substitution is a single pass, so placeholder syntax inside a value is not evaluated again. Missing values fail immediately. Templates and rendered output are limited to 1,048,576 characters; individual values are limited to 65,536 characters.

Templates are trusted application content, not user-authored HTML. HTML encoding does not validate URL schemes or make JavaScript/CSS contexts safe. Use placeholders for text content; build links from trusted application URLs and validate their schemes separately. There is no HTML sanitizer or template filesystem loader.

## Capture and assert

Replace SMTP registration with capture in development or tests:

```csharp
services.AddCaravelMailCapture(mail => mail.From = "sender@example.test");

await sender.SendAsync(message);
var captured = serviceProvider.GetRequiredService<MailCapture>();
Assert.Contains(captured.Messages, mail => mail.Subject == "Your report is ready");
captured.Clear();
```

Capture performs the same message validation, stores defensive snapshots and makes no network requests. It fails when full rather than silently dropping messages. It holds at most 100 messages, with a 25 MiB body/attachment storage bound; clear it between test cases. Capture contains message content and should never be exposed through a public diagnostics endpoint.

## Queue delivery

Configure the [database queue](QUEUES.md), its schema and a mail sender, then register mail jobs:

```csharp
services.AddCaravelQueuedMail();
services.AddCaravelQueueWorker("mail");

var result = await queuedMail.EnqueueAsync(message,
    new QueueDispatchOptions("mail", tenantId, "report-2026-01"), cancellationToken);
```

Resolve `QueuedMail` through dependency injection. Its versioned job name is `caravel.mail.send.v1`. Workers resolve the configured `IMailSender`, and normal queue retries, dead-letter handling and replay apply. Direct sending remains available through `IMailSender`; registering queued mail does not silently change it.

The queue's serialized payload limit also applies (64 KiB by default), including JSON and base64 attachment overhead. Prefer small transactional messages. For larger reports, send an authenticated application link or enqueue a small application job that loads authorized content. Never place credentials or unrestricted filesystem paths in a job.

Queue storage contains recipient addresses, message bodies and attachment bytes. Confirmation/reset links may contain bearer tokens. Restrict database access, protect backups, set retention appropriate to those messages, and do not log payloads. The package does not encrypt queue payloads or manage application retention policies.

## Delivery guarantees

SMTP acceptance is not proof of inbox delivery. A failed connection after acceptance can leave the outcome unknown; retrying may send a duplicate. Queue idempotency prevents duplicate enqueue operations with the same identity, but cannot make an external SMTP send exactly once. Keep messages suitable for repeat delivery and make linked application actions safe to repeat.

Transport failures throw `MailDeliveryException` with a redacted message and no server-response inner exception. Cancellation remains `OperationCanceledException`. No credentials, message bodies or server diagnostics are logged by this package. Use your provider's delivery records when investigating delivery beyond SMTP acceptance.

Native Identity confirmation/reset endpoints can use a normal `IEmailSender<TUser>` adapter around `IMailSender`; the [Identity sample](../samples/Caravel.Identity/) demonstrates this composition. Mail does not depend on ASP.NET Core or require a particular account UI.
