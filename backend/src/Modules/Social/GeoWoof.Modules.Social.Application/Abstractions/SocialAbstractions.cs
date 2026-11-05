using System.ComponentModel;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Abstractions;

// Ports of the Social application layer; the Infrastructure layer implements them.

/// <summary>A position in a list, from the most recent to the oldest.</summary>
public sealed record SocialPosition(DateTimeOffset At, Guid Id);

public enum RequestDirection
{
    Received = 0,
    Sent = 1,
}

public interface IFriendRequestRepository
{
    Task<FriendRequest?> GetByIdAsync(Guid requestId, CancellationToken cancellationToken);

    Task<FriendRequest?> GetPendingBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken);

    Task<IReadOnlyList<FriendRequest>> GetPendingPageAsync(Guid userId, RequestDirection direction, SocialPosition? after, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<FriendRequest>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(FriendRequest request);

    void Remove(FriendRequest request);
}

public interface IFriendshipRepository
{
    Task<Friendship?> GetBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken);

    Task<IReadOnlyList<Friendship>> GetPageAsync(Guid userId, SocialPosition? after, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<Friendship>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(Friendship friendship);

    void Remove(Friendship friendship);
}

public interface IUserBlockRepository
{
    Task<UserBlock?> GetAsync(Guid blockerId, Guid blockedId, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserBlock>> GetPageAsync(Guid blockerId, SocialPosition? after, int take, CancellationToken cancellationToken);

    /// <summary>In both directions.</summary>
    Task<IReadOnlyList<UserBlock>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(UserBlock block);

    void Remove(UserBlock block);
}

public interface IReportRepository
{
    Task<Report?> GetOpenAsync(Guid reporterId, ReportTargetType targetType, Guid targetId, CancellationToken cancellationToken);

    /// <summary>Those made by this person and those about them.</summary>
    Task<IReadOnlyList<Report>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(Report report);

    void Remove(Report report);
}

/// <summary>Who a person is connected to. It changes rarely and is read in almost every request.</summary>
[ImmutableObject(true)]
public sealed record SocialGraph(IReadOnlyList<Guid> Friends, IReadOnlyList<Guid> Blocked, IReadOnlyList<Guid> BlockedBy)
{
    public bool IsBlockedEitherWay(Guid userId) => Blocked.Contains(userId) || BlockedBy.Contains(userId);
}

/// <summary>Reads the graph from the database.</summary>
public interface ISocialGraphStore
{
    Task<SocialGraph> LoadAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Keeps the graph of each person. Whoever changes a relationship must invalidate both people after saving.</summary>
public interface ISocialGraphCache
{
    ValueTask<SocialGraph> GetAsync(Guid userId, CancellationToken cancellationToken);

    ValueTask InvalidateAsync(Guid first, Guid second, CancellationToken cancellationToken);
}

/// <summary>Saves the Social module's changes in one transaction (with its Outbox).</summary>
public interface ISocialUnitOfWork
{
    /// <exception cref="BuildingBlocks.Application.Persistence.UniqueConstraintViolationException">A concurrent request got there first.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets a failed save, so that the next read gets what is really stored.</summary>
    void DiscardChanges();
}
