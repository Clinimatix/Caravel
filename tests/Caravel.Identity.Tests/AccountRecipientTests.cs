using System.Net;
using System.Net.Http.Json;
using Caravel.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    [Theory]
    [InlineData("alice", "forgot-password", "/account/reset-password")]
    [InlineData("unconfirmed", "resend-confirmation", "/account/confirm")]
    public async Task Recovery_mail_uses_stored_recipient_for_normalized_lookup(string account, string endpoint, string path)
    {
        await using var factory = AccountFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Csrf(client);
        var response = await client.PostAsJsonAsync("/auth/" + endpoint,
            new { email = (account + "@example.invalid").ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        var message = Assert.Single(factory.Services.GetRequiredService<MailCapture>().Messages);
        Assert.Equal(account + "@example.invalid", Assert.Single(message.To));
        var body = Assert.IsType<string>(message.TextBody);
        var uri = new Uri(body[body.IndexOf("https://", StringComparison.Ordinal)..]);
        Assert.Equal(path, uri.AbsolutePath);
        Assert.Equal(account + "@example.invalid", QueryHelpers.ParseQuery(uri.Query)["email"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(MessageToken(message, path)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Recovery_mail_without_stored_address_keeps_generic_response_and_sends_nothing(string? storedEmail)
    {
        await using var factory = AccountFactory();
        var state = new StoredEmailOverride(storedEmail);
        factory.ExtraServices = services =>
        {
            services.AddSingleton(state);
            services.AddScoped<UserManager<IdentityUser>, StoredEmailUserManager>();
        };
        await factory.InitializeAsync();
        state.Enabled = true;
        using var client = factory.Client();
        await Csrf(client);
        foreach (var request in new[] { ("alice", "forgot-password"), ("unconfirmed", "resend-confirmation"), ("absent", "forgot-password") })
        {
            var response = await client.PostAsJsonAsync("/auth/" + request.Item2,
                new { email = request.Item1 + "@example.invalid" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(factory.Services.GetRequiredService<MailCapture>().Messages);
    }

    private sealed class StoredEmailOverride(string? value)
    {
        public bool Enabled { get; set; }
        public string? Value { get; } = value;
    }

    private sealed class StoredEmailUserManager(IServiceProvider services, StoredEmailOverride state)
        : UserManager<IdentityUser>(services.GetRequiredService<IUserStore<IdentityUser>>(),
            services.GetRequiredService<IOptions<IdentityOptions>>(), services.GetRequiredService<IPasswordHasher<IdentityUser>>(),
            services.GetServices<IUserValidator<IdentityUser>>(), services.GetServices<IPasswordValidator<IdentityUser>>(),
            services.GetRequiredService<ILookupNormalizer>(), services.GetRequiredService<IdentityErrorDescriber>(), services,
            services.GetRequiredService<ILogger<UserManager<IdentityUser>>>())
    {
        public override Task<string?> GetEmailAsync(IdentityUser user) =>
            state.Enabled ? Task.FromResult(state.Value) : base.GetEmailAsync(user);
    }
}
