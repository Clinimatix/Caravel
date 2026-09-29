using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Caravel.Notifications;

public sealed record SmsMessage(string To, string Body);

public interface ISmsSender
{
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}

/// <summary>In-memory development/test transport; never sends to a carrier.</summary>
public sealed class CaptureSmsSender : ISmsSender
{
    private readonly Queue<SmsMessage> messages = new();
    private readonly object gate = new();
    public IReadOnlyList<SmsMessage> Messages { get { lock (gate) return messages.ToArray(); } }

    public Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SmsValidation.Message(message);
        lock (gate)
        {
            if (messages.Count >= 100) throw new InvalidOperationException("SMS capture is full. Clear it before capturing more messages.");
            messages.Enqueue(message);
        }
        return Task.CompletedTask;
    }

    public void Clear() { lock (gate) messages.Clear(); }
}

public sealed class TwilioSmsOptions
{
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string From { get; set; } = "";

    internal TwilioSmsOptions ValidatedCopy()
    {
        if (!Regex.IsMatch(AccountSid, "\\AAC[0-9a-fA-F]{32}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("A valid Twilio account SID is required.");
        if (string.IsNullOrWhiteSpace(AuthToken) || AuthToken.Length > 256 || AuthToken.Any(c => char.IsControl(c) || c > 127))
            throw new ArgumentException("A valid Twilio authentication token is required.");
        SmsValidation.Phone(From);
        return (TwilioSmsOptions)MemberwiseClone();
    }
}

/// <summary>Submits to Twilio; acceptance does not establish delivery to a handset.</summary>
public sealed class TwilioSmsSender : ISmsSender
{
    private readonly HttpClient client;
    private readonly TwilioSmsOptions options;

    public TwilioSmsSender(HttpClient client, TwilioSmsOptions options)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = (options ?? throw new ArgumentNullException(nameof(options))).ValidatedCopy();
    }

    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        SmsValidation.Message(message);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{options.AccountSid}/Messages.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes(options.AccountSid + ":" + options.AuthToken)));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["To"] = message.To, ["From"] = options.From, ["Body"] = message.Body });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The SMS provider rejected the request.", null, response.StatusCode);
    }
}

public static class SmsServices
{
    public static IServiceCollection AddCaravelSmsCapture(this IServiceCollection services)
    {
        services.AddSingleton<CaptureSmsSender>();
        services.AddSingleton<ISmsSender>(provider => provider.GetRequiredService<CaptureSmsSender>());
        return services;
    }

    public static IServiceCollection AddCaravelTwilioSms(this IServiceCollection services, Action<TwilioSmsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new TwilioSmsOptions();
        configure(options);
        services.AddSingleton(options.ValidatedCopy());
        services.AddHttpClient<TwilioSmsSender>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<ISmsSender>(provider => provider.GetRequiredService<TwilioSmsSender>());
        return services;
    }
}

internal static class SmsValidation
{
    internal static void Phone(string phone)
    {
        if (phone is null || !Regex.IsMatch(phone, "\\A\\+[1-9][0-9]{1,14}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Use an international phone number beginning with '+' followed by up to 15 digits.");
    }

    internal static void Message(SmsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Phone(message.To);
        if (string.IsNullOrWhiteSpace(message.Body) || message.Body.Length > 1600 || message.Body.Contains('\0'))
            throw new ArgumentException("SMS text must contain 1 to 1600 characters and no null characters.", nameof(message));
    }
}
