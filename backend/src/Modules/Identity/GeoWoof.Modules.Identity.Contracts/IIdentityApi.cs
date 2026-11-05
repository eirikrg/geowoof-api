namespace GeoWoof.Modules.Identity.Contracts;

/// <summary>What other people see of an account in a list. It never includes the email.</summary>
public sealed record UserSummary(Guid Id, string Username, string DisplayName, string? City, Guid? AvatarAssetId, bool IsPrivate)
{
    /// <summary>What a private profile shows to those who do not follow it (docs/api.md, phase 5).</summary>
    public UserSummary Restricted() => this with { DisplayName = Username, City = null };
}

/// <summary>In-process API of the Identity module for other modules.</summary>
public interface IIdentityApi
{
    /// <summary>The account with that email, for sample data that belongs to known accounts.</summary>
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Those accounts that exist and have finished the onboarding. It knows nothing about blocks.</summary>
    Task<IReadOnlyDictionary<Guid, UserSummary>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
