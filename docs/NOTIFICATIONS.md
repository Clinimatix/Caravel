# Notifications and SMS

`Clinimatix.Caravel.Notifications` sends a notification through an explicitly selected channel. Use mail for a formatted receipt, SMS for a short alert, or register an application-owned channel. The application decides who receives a notification and whether they have agreed to receive it.

Channels are optional. Mail uses [Caravel Mail](MAIL.md); SMS uses a replaceable `ISmsSender`. A capture transport supports development and tests without sending anything externally. The Twilio adapter submits outbound SMS through its HTTPS API. Its protocol and error handling are tested with a simulated HTTP service; live carrier delivery requires your own provider and sender setup.

## Send a notification

```csharp
services.AddCaravelMailCapture(options => options.From = "sender@example.invalid");
services.AddCaravelSmsCapture();
services.AddCaravelMailNotifications();
services.AddCaravelSmsNotifications();

// Resolve inside an application/request scope.
var notifications = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
var content = new NotificationContent("Report ready", "Your report is ready.");
var recipient = new NotificationRecipient(
    Email: "reader@example.invalid", PhoneNumber: "+15555550123");
await notifications.SendAsync("mail", content, recipient, cancellationToken);
```

`mail` uses the subject, text and optional HTML. `sms` uses the text and phone number. Unknown channels fail explicitly. An email address or phone number is a delivery route, not a tenant identity or authorization decision. Do not expose this infrastructure API as an unauthenticated sending endpoint.

Mail's sender address and SMS's originating number are configured by the application. Recipients cannot override them in a notification. SMS requires international numbers beginning with `+` and caps message text at 1600 characters; encoding and length can result in multiple billable SMS segments.

## Queue each delivery separately

Configure the database queue and its schema as described in [Queues](QUEUES.md), then register:

```csharp
services.AddCaravelQueuedNotifications();
services.AddCaravelQueueWorker("notifications");

var queued = scope.ServiceProvider.GetRequiredService<QueuedNotifications>();
await queued.EnqueueAsync("mail", content, recipient,
    new QueueDispatchOptions("notifications", authorizedTenant, "report-42-mail"), cancellationToken);
await queued.EnqueueAsync("sms", content, recipient,
    new QueueDispatchOptions("notifications", authorizedTenant, "report-42-sms"), cancellationToken);
```

Each channel gets its own job and idempotency key. If SMS fails after mail succeeds, retrying the SMS job does not resend the mail job. There is no implicit fan-out, fallback provider or transaction spanning several external deliveries. Queue acceptance means the job is stored, not that a recipient has received it. Channel-specific destination validation runs when the worker sends the message; applications should validate user-supplied delivery routes before accepting them.

The stable job name is `caravel.notification.deliver.v1`. Its `NotificationDeliveryJob` can also be staged through the [transactional outbox](QUEUES.md) when notification intent must commit together with application data. Keep the channel name and payload compatible while queued jobs remain.

Queues retain destination addresses and message content. Choose access controls and retention accordingly. The queue's configured payload limit applies, including JSON overhead. Prefer short notifications linking to authenticated application content rather than placing large or sensitive records in messages.

Delivery attempts can repeat. A provider may accept a message before the connection or worker fails; retrying can send a duplicate. A completed queue job establishes that the sending adapter returned successfully. It does not establish handset delivery, inbox placement or reading. Delivery-status webhooks and provider-level reconciliation are separate capabilities.

## Use Twilio for SMS

Use configuration or a secret store to supply credentials; do not put them in source code:

```csharp
services.AddCaravelTwilioSms(options =>
{
    options.AccountSid = configuration["Sms:AccountSid"]!;
    options.AuthToken = configuration["Sms:AuthToken"]!;
    options.From = configuration["Sms:From"]!;
});
services.AddCaravelSmsNotifications();
```

The adapter uses a fixed Twilio HTTPS endpoint, form-encoded messages, a 30-second timeout and no redirects. It does not log provider response bodies or perform automatic HTTP retries; a failed request is surfaced to the caller or queue worker. Successful submission is distinct from delivery. Account eligibility, sender registration, destination permissions and consent must be configured with the provider. See [Twilio's message API](https://www.twilio.com/docs/messaging/api/message-resource).

SMS sending and MFA are separate concerns. Caravel's [account flows](AUTHENTICATION.md) use authenticator-app codes and recovery codes for MFA; enabling an SMS adapter does not enable SMS authentication.

## Add a channel or SMS provider

Implement `INotificationChannel.SendAsync` for an additional channel and register it with `AddCaravelNotificationChannel<YourChannel>("your-channel")`. Channel names are case-sensitive lowercase names. Each channel validates its own destinations and owns its provider-specific representation.

For another SMS service, implement `ISmsSender.SendAsync(SmsMessage, CancellationToken)` and register it in DI. This keeps application notification code independent of the SMS vendor. Honor cancellation, reject invalid destinations and propagate failures without including credentials or message content in errors.

## Development and testing

`CaptureSmsSender.Messages` returns a snapshot of captured messages; `Clear()` clears it. It validates numbers and message bounds but sends nothing, and rejects new messages when its 100-message capture is full. Captures are in-memory test/development tools, not durable production inboxes. Mail has its own capture transport and template tests.

The notification tests exercise explicit routing, malformed destinations, cancellation, independent queued retries and the Twilio request/error contract. They never contact a real provider.

## Later channel work

The following remain planned, not implemented: additional first-party SMS adapters such as Vonage or Azure Communication Services; database/in-app inboxes; browser and mobile push; chat, WhatsApp and RCS channels; delivery receipts; and notification preferences or quiet-hour policies. These should remain optional integrations with explicit delivery semantics, not a mandatory provider network.
