using GeoWoof.BuildingBlocks.Domain.Entities;

namespace GeoWoof.Modules.Identity.Domain.Sessions;

public enum RevocationReason
{
    /// <summary>Exchanged for its successor in a normal refresh.</summary>
    Rotated = 0,

    /// <summary>Replaced through the reuse grace period (the client lost the previous response).</summary>
    Superseded = 1,

    SignedOut = 2,
    ReuseDetected = 3,
    PasswordChanged = 4,
    AccountSecured = 5,
}

/// <summary>What presenting a refresh token means, given its state.</summary>
public enum RefreshTokenState
{
    Active = 0,

    /// <summary>Rotated moments ago: most likely a retry after a lost response, not an attack.</summary>
    RecentlyRotated = 1,

    /// <summary>Rotated a while ago and presented again: the token was probably stolen.</summary>
    Reused = 2,

    Revoked = 3,
    Expired = 4,
}

/// <summary>
/// One opaque refresh token of a session. Tokens of the same device form a family: every refresh
/// rotates the token, and presenting an old one again revokes the whole family.
/// Only a SHA-256 hash of the token is stored.
/// </summary>
public sealed class RefreshToken : Entity
{
    /// <summary>Constructor for EF Core.</summary>
    private RefreshToken()
    {
        TokenHash = string.Empty;
    }

    private RefreshToken(Guid id, Guid userId, Guid familyId, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset familyExpiresAt)
        : base(id)
    {
        UserId = userId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt < familyExpiresAt ? expiresAt : familyExpiresAt;
        FamilyExpiresAt = familyExpiresAt;
    }

    public Guid UserId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Absolute limit of the session: after it the user must sign in again.</summary>
    public DateTimeOffset FamilyExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public RevocationReason? RevocationReason { get; private set; }

    public Guid? ReplacedById { get; private set; }

    /// <summary>First token of a new session (sign-in, registration, password change).</summary>
    public static RefreshToken StartFamily(Guid userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime, TimeSpan familyLifetime) =>
        new(Guid.CreateVersion7(now), userId, Guid.CreateVersion7(now), tokenHash, now, now + lifetime, now + familyLifetime);

    /// <summary>Next token of the same session; it never outlives the family.</summary>
    public RefreshToken Successor(string tokenHash, DateTimeOffset now, TimeSpan lifetime) =>
        new(Guid.CreateVersion7(now), UserId, FamilyId, tokenHash, now, now + lifetime, FamilyExpiresAt);

    public RefreshTokenState Evaluate(DateTimeOffset now, TimeSpan reuseGracePeriod)
    {
        if (now >= FamilyExpiresAt)
        {
            return RefreshTokenState.Expired;
        }

        return (RevokedAt, RevocationReason) switch
        {
            (null, _) => now >= ExpiresAt ? RefreshTokenState.Expired : RefreshTokenState.Active,
            ({ } revokedAt, Sessions.RevocationReason.Rotated) when now - revokedAt <= reuseGracePeriod => RefreshTokenState.RecentlyRotated,
            (_, Sessions.RevocationReason.Rotated) => RefreshTokenState.Reused,
            _ => RefreshTokenState.Revoked,
        };
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public void MarkRotated(RefreshToken successor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(successor);
        Revoke(now, Sessions.RevocationReason.Rotated);
        ReplacedById = successor.Id;
    }

    public void Revoke(DateTimeOffset now, RevocationReason reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevocationReason = reason;
    }
}
