namespace GeoWoof.Modules.Social.Contracts;

/// <summary>How a person relates to another. A block counts in both directions and wins over everything else.</summary>
public enum Relationship
{
    Self = 0,

    /// <summary>They follow each other.</summary>
    Friend = 1,

    /// <summary>The viewer follows that person, who does not follow back.</summary>
    Follower = 2,

    None = 3,
    Blocked = 4,
}

/// <summary>In-process API of the Social module for other modules. The answers come from a cache.</summary>
public interface IRelationshipsApi
{
    Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken);

    /// <summary>The people who must not appear to this user: those they blocked and those who blocked them.</summary>
    Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The people this user follows, whose private profiles they can see.</summary>
    Task<IReadOnlyCollection<Guid>> GetFollowingIdsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Only those this person has blocked, and not those who have blocked them.</summary>
    Task<IReadOnlyCollection<Guid>> GetBlockedUserIdsAsync(Guid userId, CancellationToken cancellationToken);
}
