namespace GeoWoof.Modules.Social.Contracts;

/// <summary>How a person relates to another. A block counts in both directions and wins over a friendship.</summary>
public enum Relationship
{
    Self = 0,
    Friend = 1,
    None = 2,
    Blocked = 3,
}

/// <summary>In-process API of the Social module for other modules. The answers come from a cache.</summary>
public interface IRelationshipsApi
{
    Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken);

    /// <summary>The people who must not appear to this user: those they blocked and those who blocked them.</summary>
    Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken);
}
