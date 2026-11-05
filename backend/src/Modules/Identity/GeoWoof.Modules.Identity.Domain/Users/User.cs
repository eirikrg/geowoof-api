using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Domain.Visibility;

namespace GeoWoof.Modules.Identity.Domain.Users;

/// <summary>Language for emails and push notifications.</summary>
public enum Language
{
    Es = 0,
    En = 1,
}

/// <summary>Password sign-in throttling: after <see cref="MaxFailedAttempts"/> failures the account is locked for <see cref="Duration"/>.</summary>
public sealed record LockoutPolicy(int MaxFailedAttempts, TimeSpan Duration);

/// <summary>Sign-in with an external provider (Google): the provider's stable subject id.</summary>
public sealed record ExternalLogin(string Provider, string Subject)
{
    public const string Google = "google";
}

public sealed record UserProfileChangedDomainEvent(Guid UserId, string? Username, string DisplayName) : IDomainEvent;

public sealed record UserDeletedDomainEvent(Guid UserId) : IDomainEvent;

public sealed record UserPrivacyChangedDomainEvent(Guid UserId, bool IsPrivate) : IDomainEvent;

/// <summary>
/// A GeoWoof account: credentials (password hash, external logins), email verification, sign-in
/// throttling and the public identity (username and display name). Sessions and verification codes
/// are separate aggregates that reference the user by id.
/// </summary>
public sealed class User : AggregateRoot
{
    public const int DefaultHideStartDistanceM = 200;

    public const int DisplayNameMaxLength = 50;
    public const int BioMaxLength = 300;
    public const int CityMaxLength = 100;

    private readonly List<ExternalLogin> _externalLogins = [];

    /// <summary>Constructor for EF Core.</summary>
    private User()
    {
        Email = null!;
        DisplayName = string.Empty;
    }

    private User(Guid id, Email email, string displayName, Language language, DateTimeOffset now)
        : base(id)
    {
        Email = email;
        DisplayName = NormalizeDisplayName(displayName);
        Language = language;
        CreatedAt = now;
    }

    public Email Email { get; private set; }

    public bool EmailVerified { get; private set; }

    public string? PasswordHash { get; private set; }

    public Username? Username { get; private set; }

    public string DisplayName { get; private set; }

    public string? Bio { get; private set; }

    public string? City { get; private set; }

    /// <summary>Image in the Media module.</summary>
    public Guid? AvatarAssetId { get; private set; }

    /// <summary>A private profile only shows its content to those who follow it, and following it needs a yes.</summary>
    public bool IsPrivate { get; private set; }

    /// <summary>Who can see this person on the live map. <c>null</c> until they choose, which counts as friends only.</summary>
    public LiveVisibility? LiveVisibility { get; private set; }

    /// <summary>How far they go from the start of a walk before the app shares their position.</summary>
    public int HideStartDistanceM { get; private set; } = DefaultHideStartDistanceM;

    public Language Language { get; private set; }

    public int FailedSignInAttempts { get; private set; }

    public DateTimeOffset? LockoutEndsAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<ExternalLogin> ExternalLogins => _externalLogins.AsReadOnly();

    public bool HasPassword => PasswordHash is not null;

    public string? GoogleSubject => _externalLogins.Find(login => login.Provider == ExternalLogin.Google)?.Subject;

    /// <summary>Onboarding is complete once the user has chosen a username.</summary>
    public bool HasCompletedOnboarding => Username is not null;

    public static bool IsValidDisplayName(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName) && displayName.Trim().Length <= DisplayNameMaxLength;

    public static User RegisterWithPassword(Guid id, Email email, string passwordHash, string displayName, Language language, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return new User(id, email, displayName, language, now) { PasswordHash = passwordHash };
    }

    /// <summary>Google has already verified the email address.</summary>
    public static User RegisterWithGoogle(Guid id, Email email, string googleSubject, string displayName, Language language, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(googleSubject);
        var user = new User(id, email, displayName, language, now) { EmailVerified = true };
        user._externalLogins.Add(new ExternalLogin(ExternalLogin.Google, googleSubject));
        return user;
    }

    public bool IsLockedOut(DateTimeOffset now) => LockoutEndsAt is { } endsAt && endsAt > now;

    public void RecordFailedSignIn(DateTimeOffset now, LockoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        FailedSignInAttempts++;
        if (FailedSignInAttempts >= policy.MaxFailedAttempts)
        {
            LockoutEndsAt = now + policy.Duration;
            FailedSignInAttempts = 0;
        }
    }

    public void RecordSuccessfulSignIn() => ClearLockout();

    public void ClearLockout()
    {
        FailedSignInAttempts = 0;
        LockoutEndsAt = null;
    }

    /// <summary>Sets a new password hash (change, reset, or an upgraded hash after sign-in).</summary>
    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void MarkEmailVerified() => EmailVerified = true;

    /// <summary>
    /// Links a Google account with the same email. When this account's email was never verified,
    /// someone else may have registered it first (pre-account hijacking): the password is removed
    /// and the method returns <c>true</c> so that every existing session is revoked.
    /// </summary>
    public bool LinkGoogle(string googleSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(googleSubject);
        if (GoogleSubject is not null)
        {
            return false;
        }

        var possibleHijack = !EmailVerified;
        if (possibleHijack)
        {
            PasswordHash = null;
            EmailVerified = true;
        }

        _externalLogins.Add(new ExternalLogin(ExternalLogin.Google, googleSubject));
        return possibleHijack;
    }

    public void ChangeUsername(Username username)
    {
        ArgumentNullException.ThrowIfNull(username);
        if (username == Username)
        {
            return;
        }

        Username = username;
        Raise(new UserProfileChangedDomainEvent(Id, username.Value, DisplayName));
    }

    public void ChangeDisplayName(string displayName)
    {
        var normalized = NormalizeDisplayName(displayName);
        if (string.Equals(normalized, DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        DisplayName = normalized;
        Raise(new UserProfileChangedDomainEvent(Id, Username?.Value, DisplayName));
    }

    public void ChangeLanguage(Language language) => Language = language;

    /// <summary>Replaces the public profile: empty values clear the field.</summary>
    public void UpdateProfile(string displayName, string? bio, string? city, Guid? avatarAssetId)
    {
        ChangeDisplayName(displayName);
        Bio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim();
        City = string.IsNullOrWhiteSpace(city) ? null : city.Trim();
        AvatarAssetId = avatarAssetId;
    }

    public void SetPrivate(bool isPrivate)
    {
        if (isPrivate != IsPrivate)
        {
            IsPrivate = isPrivate;
            Raise(new UserPrivacyChangedDomainEvent(Id, isPrivate));
        }
    }

    public void ChangeLiveSettings(LiveVisibility? liveVisibility, int? hideStartDistanceM)
    {
        LiveVisibility = liveVisibility ?? LiveVisibility;
        HideStartDistanceM = hideStartDistanceM ?? HideStartDistanceM;
    }

    /// <summary>Marks the account for deletion; the repository removes it in the same unit of work.</summary>
    public void Delete() => Raise(new UserDeletedDomainEvent(Id));

    private static string NormalizeDisplayName(string displayName) =>
        IsValidDisplayName(displayName)
            ? displayName.Trim()
            : throw new ArgumentException($"The display name must have 1 to {DisplayNameMaxLength} characters.", nameof(displayName));
}
