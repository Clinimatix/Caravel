using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enabling_mfa_revokes_existing_sessions_even_when_recovery_generation_fails(bool throwFailure)
    {
        await using var factory = AccountFactory();
        factory.ExtraServices = services =>
        {
            services.AddSingleton(new RecoveryGenerationFailure(throwFailure));
            services.AddScoped<UserManager<IdentityUser>, RecoveryFailureUserManager>();
        };
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        await Csrf(client);
        var setup = await client.PostAsJsonAsync("/auth/mfa/setup", new { password = Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<MfaSetup>())!.SharedKey;
        using var existing = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(existing, "alice", Password)).StatusCode);
        await Csrf(client);
        var request = new { password = Password, code = AuthenticatorCode(key) };
        if (throwFailure)
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsJsonAsync("/auth/mfa/enable", request)).StatusCode);
        else
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/mfa/enable", request)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await existing.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(client, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/mfa/login", new { code = AuthenticatorCode(key) })).StatusCode);
    }

    [Fact]
    public async Task Expired_native_confirmation_and_password_reset_tokens_are_rejected()
    {
        await using var factory = AccountFactory();
        factory.ExtraServices = services => services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromMilliseconds(1));
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/auth/register", new { email = "new@example.invalid", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/auth/forgot-password", new { email = "alice@example.invalid" })).StatusCode);
        var messages = factory.Services.GetRequiredService<MailCapture>().Messages;
        var confirmation = MessageToken(messages[0], "/account/confirm");
        var reset = MessageToken(messages[1], "/account/reset-password");
        await Task.Delay(20);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/confirm-email", new { email = "new@example.invalid", token = confirmation })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/reset-password", new { email = "alice@example.invalid", token = reset, newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "new", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
    }

    [Fact]
    public async Task Mail_failures_keep_generic_responses_and_input_and_rate_limits_apply()
    {
        await using var factory = AccountFactory();
        factory.ExtraServices = services => services.AddScoped<IEmailSender<IdentityUser>, UnavailableAccountMail>();
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/auth/register", new { email = "new@example.invalid", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/auth/resend-confirmation", new { email = "new@example.invalid" })).StatusCode);
        foreach (var email in new[] { "alice@example.invalid", "unknown@example.invalid" })
        {
            var response = await client.PostAsJsonAsync("/auth/forgot-password", new { email });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/reset-password", new { email = "alice@example.invalid", token = new string('x', 4097), newPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/register", new { email = "not-an-email", password = Password })).StatusCode);

        await using var limited = AccountFactory();
        limited.Settings["Caravel:Limits:LoginPermitLimit"] = "1";
        await limited.InitializeAsync();
        using var caller = limited.Client();
        await Csrf(caller);
        Assert.Equal(HttpStatusCode.Accepted, (await caller.PostAsJsonAsync("/auth/forgot-password", new { email = "unknown@example.invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await caller.PostAsJsonAsync("/auth/register", new { email = "new@example.invalid", password = Password })).StatusCode);
    }

    [Fact]
    public async Task Pending_mfa_expires_and_concurrent_recovery_redemption_succeeds_once()
    {
        await using var factory = AccountFactory();
        await factory.InitializeAsync();
        var (key, codes) = await Enroll(factory);
        using var expired = factory.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await Login(expired, "alice", Password)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.PostAsJsonAsync("/auth/mfa/login", new { code = AuthenticatorCode(key) })).StatusCode);
        using var first = factory.Client();
        using var second = factory.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await Login(first, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(second, "alice", Password)).StatusCode);
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/auth/mfa/login", new { code = " ", recoveryCode = codes[0] }),
            second.PostAsJsonAsync("/auth/mfa/login", new { recoveryCode = codes[0] }));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Incorrect_enrollment_codes_lock_the_account_despite_correct_password()
    {
        await using var factory = AccountFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/mfa/setup", new { password = Password })).StatusCode);
        await Csrf(client);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/mfa/enable", new { password = Password, code = "invalid" })).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync("alice"))!));
    }

    [Fact]
    public async Task Registration_is_opt_in_and_requires_antiforgery()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        var request = new { email = "new@example.invalid", password = Password };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/register", request)).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/auth/register", request)).StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<MailCapture>().Messages);
    }

    [Fact]
    public async Task Registration_confirmation_and_recovery_use_trusted_links_and_native_single_use_reset_tokens()
    {
        await using var factory = AccountFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Csrf(client);
        client.DefaultRequestHeaders.Host = "untrusted.example.invalid";
        var registration = new { email = "new@example.invalid", password = Password };
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/auth/register", registration)).StatusCode);
        var mail = factory.Services.GetRequiredService<MailCapture>();
        var confirmation = MessageToken(Assert.Single(mail.Messages), "/account/confirm");
        client.DefaultRequestHeaders.Host = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "new", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/confirm-email", new { email = registration.email, token = "invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/confirm-email", new { email = registration.email, token = confirmation })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "new", Password)).StatusCode);
        using var recovery = factory.Client();
        await Csrf(recovery);
        foreach (var email in new[] { registration.email, "unknown@example.invalid", "unconfirmed@example.invalid" })
            Assert.Equal(HttpStatusCode.Accepted, (await recovery.PostAsJsonAsync("/auth/forgot-password", new { email })).StatusCode);
        Assert.Equal(2, mail.Messages.Count);
        var token = MessageToken(mail.Messages[1], "/account/reset-password");
        Assert.Equal(HttpStatusCode.BadRequest, (await recovery.PostAsJsonAsync("/auth/reset-password", new { email = "bob@example.invalid", token, newPassword = NewPassword })).StatusCode);
        var reset = new { email = registration.email, token, newPassword = NewPassword };
        Assert.Equal(HttpStatusCode.NoContent, (await recovery.PostAsJsonAsync("/auth/reset-password", reset)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await recovery.PostAsJsonAsync("/auth/reset-password", reset)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(recovery, "new", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(recovery, "new", NewPassword)).StatusCode);
        using var duplicate = factory.Client();
        await Csrf(duplicate);
        Assert.Equal(HttpStatusCode.Accepted, (await duplicate.PostAsJsonAsync("/auth/register", registration)).StatusCode);
        Assert.Equal(2, mail.Messages.Count);
    }

    [Fact]
    public async Task Totp_enrollment_requires_password_and_code_then_pending_cookie_and_one_use_recovery()
    {
        await using var factory = AccountFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/mfa/setup", new { password = "wrong" })).StatusCode);
        var setup = await client.PostAsJsonAsync("/auth/mfa/setup", new { password = Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<MfaSetup>())!.SharedKey;
        Assert.NotEmpty(key);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/mfa/enable", new { password = Password, code = "invalid" })).StatusCode);
        var enabled = await client.PostAsJsonAsync("/auth/mfa/enable", new { password = Password, code = AuthenticatorCode(key) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var codes = (await enabled.Content.ReadFromJsonAsync<MfaCodes>())!.RecoveryCodes;
        Assert.Equal(10, codes.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        var pending = await Login(client, "alice", Password);
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        Assert.DoesNotContain(pending.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Identity.Application="));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/mfa/login", new { code = AuthenticatorCode(key) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        using var recovery = factory.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await Login(recovery, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await recovery.PostAsJsonAsync("/auth/mfa/login", new { recoveryCode = codes[0] })).StatusCode);
        using var reuse = factory.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await Login(reuse, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reuse.PostAsJsonAsync("/auth/mfa/login", new { recoveryCode = codes[0] })).StatusCode);
    }

    [Fact]
    public async Task Mfa_changes_require_both_factors_revoke_sessions_and_rotate_keys_and_recovery_codes()
    {
        await using var factory = AccountFactory();
        await factory.InitializeAsync();
        var (key, originalCodes) = await Enroll(factory);
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await Login(client, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/mfa/login", new { code = AuthenticatorCode(key) })).StatusCode);
        using var pending = factory.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await Login(pending, "alice", Password)).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/mfa/disable", new { password = Password })).StatusCode);
        var rotation = await client.PostAsJsonAsync("/auth/mfa/recovery-codes", new { password = Password, code = AuthenticatorCode(key) });
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
        var newCodes = (await rotation.Content.ReadFromJsonAsync<MfaCodes>())!.RecoveryCodes;
        Assert.Empty(originalCodes.Intersect(newCodes));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending.PostAsJsonAsync("/auth/mfa/login", new { code = AuthenticatorCode(key) })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(client, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/mfa/login", new { recoveryCode = originalCodes[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/mfa/login", new { recoveryCode = newCodes[0] })).StatusCode);
        await Csrf(client);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/mfa/disable", new { password = Password, code = AuthenticatorCode(key) })).StatusCode);
        await Csrf(client);
        // A second unused recovery code permits recovery after losing the authenticator.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/mfa/disable", new { password = Password, recoveryCode = newCodes[1] })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        await Csrf(client);
        var newSetup = await client.PostAsJsonAsync("/auth/mfa/setup", new { password = Password });
        Assert.NotEqual(key, (await newSetup.Content.ReadFromJsonAsync<MfaSetup>())!.SharedKey);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var alice = (await users.FindByIdAsync("alice"))!;
        Assert.False(await users.GetTwoFactorEnabledAsync(alice));
        Assert.Equal(0, await users.CountRecoveryCodesAsync(alice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_second_factors_lock_out_even_with_a_valid_pending_cookie(bool recovery)
    {
        await using var factory = AccountFactory();
        await factory.InitializeAsync();
        var (key, codes) = await Enroll(factory);
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await Login(client, "alice", Password)).StatusCode);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/mfa/login",
                new { code = recovery ? null : "invalid", recoveryCode = recovery ? "invalid" : null })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/mfa/login", new { recoveryCode = codes[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "alice", Password)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync("alice"))!));
    }

    [Fact]
    public async Task Password_reset_invalidates_pending_mfa_but_preserves_enrollment()
    {
        await using var factory = AccountFactory();
        await factory.InitializeAsync();
        var (key, _) = await Enroll(factory);
        using var pending = factory.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await Login(pending, "alice", Password)).StatusCode);
        using var reset = factory.Client();
        await Csrf(reset);
        Assert.Equal(HttpStatusCode.Accepted, (await reset.PostAsJsonAsync("/auth/forgot-password", new { email = "alice@example.invalid" })).StatusCode);
        var token = MessageToken(Assert.Single(factory.Services.GetRequiredService<MailCapture>().Messages), "/account/reset-password");
        Assert.Equal(HttpStatusCode.NoContent, (await reset.PostAsJsonAsync("/auth/reset-password", new { email = "alice@example.invalid", token, newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending.PostAsJsonAsync("/auth/mfa/login", new { code = AuthenticatorCode(key) })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(reset, "alice", NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reset.PostAsJsonAsync("/auth/mfa/login", new { code = AuthenticatorCode(key) })).StatusCode);
    }

    private static IdentityFactory AccountFactory()
    {
        var factory = new IdentityFactory();
        factory.Settings["Caravel:Accounts:AllowRegistration"] = "true";
        factory.Settings["Caravel:Accounts:ConfirmationPage"] = "https://accounts.example.invalid/account/confirm";
        factory.Settings["Caravel:Accounts:PasswordResetPage"] = "https://accounts.example.invalid/account/reset-password";
        factory.Settings["Caravel:Limits:LoginPermitLimit"] = "100";
        return factory;
    }

    private static string MessageToken(CaravelMailMessage message, string path)
    {
        var body = Assert.IsType<string>(message.TextBody);
        var uri = new Uri(body[body.IndexOf("https://", StringComparison.Ordinal)..]);
        Assert.Equal("accounts.example.invalid", uri.Host);
        Assert.Equal(path, uri.AbsolutePath);
        return QueryHelpers.ParseQuery(uri.Query)["token"].ToString();
    }

    private static async Task<(string Key, string[] Codes)> Enroll(IdentityFactory factory)
    {
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        await Csrf(client);
        var setup = await client.PostAsJsonAsync("/auth/mfa/setup", new { password = Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<MfaSetup>())!.SharedKey;
        await Csrf(client);
        var enabled = await client.PostAsJsonAsync("/auth/mfa/enable", new { password = Password, code = AuthenticatorCode(key) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        return (key, (await enabled.Content.ReadFromJsonAsync<MfaCodes>())!.RecoveryCodes);
    }

    private static string AuthenticatorCode(string key)
    {
        // Test-only access to Identity's own implementation; production never generates TOTP codes.
        var assembly = typeof(AuthenticatorTokenProvider<IdentityUser>).Assembly;
        var bytes = (byte[])assembly.GetType("Microsoft.AspNetCore.Identity.Base32", throwOnError: true)!
            .GetMethod("FromBase32", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, [key])!;
        var value = (int)assembly.GetType("Microsoft.AspNetCore.Identity.Rfc6238AuthenticationService", throwOnError: true)!
            .GetMethod("ComputeTotp", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [bytes, (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), null])!;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record MfaSetup(string SharedKey);
    private sealed record MfaCodes(string[] RecoveryCodes);
    private sealed record RecoveryGenerationFailure(bool Throw);
    private sealed class RecoveryFailureUserManager(IServiceProvider services, RecoveryGenerationFailure failure)
        : UserManager<IdentityUser>(services.GetRequiredService<IUserStore<IdentityUser>>(),
            services.GetRequiredService<IOptions<IdentityOptions>>(), services.GetRequiredService<IPasswordHasher<IdentityUser>>(),
            services.GetServices<IUserValidator<IdentityUser>>(), services.GetServices<IPasswordValidator<IdentityUser>>(),
            services.GetRequiredService<ILookupNormalizer>(), services.GetRequiredService<IdentityErrorDescriber>(), services,
            services.GetRequiredService<ILogger<UserManager<IdentityUser>>>())
    {
        public override Task<IEnumerable<string>?> GenerateNewTwoFactorRecoveryCodesAsync(IdentityUser user, int number) =>
            failure.Throw ? throw new InvalidOperationException("Synthetic recovery-code store failure.") : Task.FromResult<IEnumerable<string>?>(null);
    }
    private sealed class UnavailableAccountMail : IEmailSender<IdentityUser>
    {
        public Task SendConfirmationLinkAsync(IdentityUser user, string email, string confirmationLink) => throw new InvalidOperationException("Synthetic mail service unavailable.");
        public Task SendPasswordResetLinkAsync(IdentityUser user, string email, string resetLink) => throw new InvalidOperationException("Synthetic mail service unavailable.");
        public Task SendPasswordResetCodeAsync(IdentityUser user, string email, string resetCode) => throw new InvalidOperationException("Synthetic mail service unavailable.");
    }
}
