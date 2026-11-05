using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.Modules.Identity.Domain.Sessions;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;

namespace GeoWoof.Modules.Identity.Application.Abstractions;

// Ports of the Identity application layer; the Infrastructure layer implements them.

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken);

    Task<User?> GetByExternalLoginAsync(string provider, string subject, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(Email email, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken);

    /// <summary>Those accounts that exist and have finished the onboarding.</summary>
    Task<IReadOnlyList<User>> GetPublicByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>
    /// The accounts that match the text, from the best match to the worst, after a position. A private
    /// profile is only found by its name if it is among <paramref name="followedIds"/>.
    /// </summary>
    Task<IReadOnlyList<RelevancePosition>> SearchAsync(string text, IReadOnlyCollection<Guid> excludedIds, IReadOnlyCollection<Guid> followedIds, RelevancePosition after, int take, CancellationToken cancellationToken);

    void Add(User user);

    void Remove(User user);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> GetActiveByFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(RefreshToken token);

    Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IVerificationCodeRepository
{
    /// <summary>The most recently issued code, used or not.</summary>
    Task<VerificationCode?> GetLatestAsync(Guid userId, VerificationPurpose purpose, CancellationToken cancellationToken);

    Task<IReadOnlyList<VerificationCode>> GetUsableAsync(Guid userId, VerificationPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(VerificationCode code);

    Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Saves the Identity module's changes in one transaction (with its Outbox).</summary>
public interface IIdentityUnitOfWork
{
    /// <exception cref="BuildingBlocks.Application.Persistence.UniqueConstraintViolationException">A unique value is already used.</exception>
    /// <exception cref="BuildingBlocks.Application.Persistence.ConcurrencyConflictException">A row changed since it was read.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public enum PasswordCheck
{
    Failed = 0,
    Succeeded = 1,

    /// <summary>Correct, but hashed with older parameters: store a new hash.</summary>
    SucceededNeedsRehash = 2,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordCheck Verify(string passwordHash, string password);

    /// <summary>Spends the same time as a real verification, so a missing account cannot be told apart by timing.</summary>
    void SimulateVerification(string password);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public interface IAccessTokenIssuer
{
    AccessToken Issue(User user, DateTimeOffset now);
}

/// <summary>Random secrets and their hashes: refresh tokens (SHA-256) and one-time codes (keyed HMAC).</summary>
public interface ISecretGenerator
{
    string NewRefreshToken();

    string HashRefreshToken(string refreshToken);

    /// <summary>A uniformly random 6-digit code.</summary>
    string NewVerificationCode();

    /// <summary>Keyed hash bound to the user and purpose, so a leaked table cannot be brute-forced offline.</summary>
    string HashVerificationCode(Guid userId, VerificationPurpose purpose, string code);
}

/// <summary>Claims of a validated Google ID token.</summary>
public sealed record GoogleIdentity(string Subject, string Email, bool EmailVerified, string? Name);

public interface IGoogleIdTokenValidator
{
    /// <summary>False when no Google client id is configured (ADR-0012).</summary>
    bool IsEnabled { get; }

    /// <summary>Returns the identity, or <c>null</c> if the token is invalid, expired or for another audience.</summary>
    Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken);
}

public sealed record EmailMessage(string To, string Subject, string TextBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
