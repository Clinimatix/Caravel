namespace Caravel.Auth;

/// <summary>Options for the explicitly mapped cookie account endpoints.</summary>
public sealed class AccountEndpointOptions
{
    /// <summary>Self-registration is off unless the application deliberately enables it.</summary>
    public bool AllowRegistration { get; init; }
    /// <summary>A trusted HTTPS application page which submits the confirmation token.</summary>
    public required Uri ConfirmationPage { get; init; }
    /// <summary>A trusted HTTPS application page which submits the reset token and new password.</summary>
    public required Uri PasswordResetPage { get; init; }
    /// <summary>An application-registered rate limiting policy applied to every account endpoint.</summary>
    public required string RateLimitPolicy { get; init; }

    internal void Validate()
    {
        foreach (var address in new[] { ConfirmationPage, PasswordResetPage })
            if (address is null || !address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps ||
                address.UserInfo.Length != 0 || address.Fragment.Length != 0 || address.Query.Length != 0)
                throw new ArgumentException("Account callback pages must be absolute HTTPS URLs without credentials, query strings or fragments.");
        ArgumentException.ThrowIfNullOrWhiteSpace(RateLimitPolicy);
    }
}
