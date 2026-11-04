using System.ComponentModel.DataAnnotations;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;

namespace GeoWoof.Modules.Identity.Application;

/// <summary>Session, lockout and one-time code settings (configuration section <c>Auth</c>). Defaults follow ADR-0005.</summary>
public sealed class AuthSettings
{
    public const string SectionName = "Auth";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Absolute session limit: after it the user must sign in again.</summary>
    public TimeSpan RefreshTokenFamilyLifetime { get; set; } = TimeSpan.FromDays(180);

    /// <summary>A token rotated less than this long ago may be presented again without revoking the session.</summary>
    public TimeSpan RefreshTokenReuseGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    [Range(1, 100)]
    public int MaxFailedSignInAttempts { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan VerificationCodeLifetime { get; set; } = TimeSpan.FromMinutes(15);

    [Range(1, 20)]
    public int VerificationCodeMaxAttempts { get; set; } = 5;

    public TimeSpan VerificationCodeResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

    public LockoutPolicy Lockout => new(MaxFailedSignInAttempts, LockoutDuration);

    public VerificationCodePolicy VerificationCodes =>
        new(VerificationCodeLifetime, VerificationCodeMaxAttempts, VerificationCodeResendCooldown);
}
