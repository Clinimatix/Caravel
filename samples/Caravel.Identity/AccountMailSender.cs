using Caravel.Mail;
using Microsoft.AspNetCore.Identity;

namespace Caravel.IdentitySample;

/// <summary>Adapts native Identity email delivery without coupling the Auth package to a transport.</summary>
public sealed class AccountMailSender(IMailSender mail) : IEmailSender<IdentityUser>
{
    public Task SendConfirmationLinkAsync(IdentityUser user, string email, string confirmationLink) =>
        Send(email, "Confirm your email", "Confirm your email using this link: " + confirmationLink);

    public Task SendPasswordResetLinkAsync(IdentityUser user, string email, string resetLink) =>
        Send(email, "Reset your password", "Reset your password using this link: " + resetLink);

    public Task SendPasswordResetCodeAsync(IdentityUser user, string email, string resetCode) =>
        Send(email, "Reset your password", "Your password reset code: " + resetCode);

    private Task Send(string email, string subject, string text) => mail.SendAsync(new CaravelMailMessage
        { To = [email], Subject = subject, TextBody = text });
}
