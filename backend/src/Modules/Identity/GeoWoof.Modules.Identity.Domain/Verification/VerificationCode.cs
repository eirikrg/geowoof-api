using System.Security.Cryptography;
using System.Text;
using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Identity.Domain.Verification;

public enum VerificationPurpose
{
    EmailVerification = 0,
    PasswordReset = 1,
}

/// <summary>Limits of the one-time codes sent by email.</summary>
public sealed record VerificationCodePolicy(TimeSpan Lifetime, int MaxAttempts, TimeSpan ResendCooldown);

/// <summary>
/// A 6-digit one-time code sent by email. Only a keyed hash is stored. It expires, allows a few
/// attempts, and issuing a new code for the same purpose invalidates the previous one.
/// </summary>
public sealed class VerificationCode : Entity
{
    /// <summary>Constructor for EF Core.</summary>
    private VerificationCode()
    {
        CodeHash = string.Empty;
    }

    private VerificationCode(Guid id, Guid userId, VerificationPurpose purpose, string codeHash, DateTimeOffset now, TimeSpan lifetime)
        : base(id)
    {
        UserId = userId;
        Purpose = purpose;
        CodeHash = codeHash;
        CreatedAt = now;
        ExpiresAt = now + lifetime;
    }

    public Guid UserId { get; private set; }

    public VerificationPurpose Purpose { get; private set; }

    public string CodeHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Set when the code is used or superseded by a newer one.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    public static VerificationCode Issue(Guid userId, VerificationPurpose purpose, string codeHash, DateTimeOffset now, VerificationCodePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        ArgumentNullException.ThrowIfNull(policy);
        return new VerificationCode(Guid.CreateVersion7(now), userId, purpose, codeHash, now, policy.Lifetime);
    }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;

    /// <summary>How long until another code may be requested; zero when allowed now.</summary>
    public TimeSpan CooldownRemaining(DateTimeOffset now, VerificationCodePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var remaining = CreatedAt + policy.ResendCooldown - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Checks a candidate (already hashed with the same key). Every attempt counts, right or wrong.</summary>
    public Result Redeem(string candidateHash, DateTimeOffset now, VerificationCodePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidateHash);
        ArgumentNullException.ThrowIfNull(policy);

        if (ConsumedAt is not null)
        {
            return IdentityErrors.CodeInvalid;
        }

        if (now >= ExpiresAt)
        {
            return IdentityErrors.CodeExpired;
        }

        if (Attempts >= policy.MaxAttempts)
        {
            return IdentityErrors.CodeAttemptsExceeded;
        }

        Attempts++;
        var matches = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(CodeHash), Encoding.UTF8.GetBytes(candidateHash));
        if (!matches)
        {
            return Attempts >= policy.MaxAttempts ? IdentityErrors.CodeAttemptsExceeded : IdentityErrors.CodeInvalid;
        }

        ConsumedAt = now;
        return Result.Success();
    }

    public void Invalidate(DateTimeOffset now) => ConsumedAt ??= now;
}
