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

public enum PeopleList
{
    Friends = 0,
    Followers = 1,
    Following = 2,
}

/// <summary>A person in a list, with when the relationship started. <paramref name="Id"/> keeps the order stable.</summary>
public sealed record PersonRow(Guid UserId, DateTimeOffset Since, Guid Id);

public sealed record SocialCounts(int Followers, int Following, int Friends);

public interface IFollowRepository
{
    Task<Follow?> GetAsync(Guid followerId, Guid followeeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PersonRow>> GetPageAsync(Guid userId, PeopleList list, SocialPosition? after, int take, CancellationToken cancellationToken);

    Task<SocialCounts> GetCountsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>In both directions.</summary>
    Task<IReadOnlyList<Follow>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(Follow follow);

    void Remove(Follow follow);
}

public interface IFollowRequestRepository
{
    Task<FollowRequest?> GetByIdAsync(Guid requestId, CancellationToken cancellationToken);

    Task<FollowRequest?> GetPendingAsync(Guid requesterId, Guid targetId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FollowRequest>> GetPendingPageAsync(Guid userId, RequestDirection direction, SocialPosition? after, int take, CancellationToken cancellationToken);

    /// <summary>All those waiting for an answer from this person.</summary>
    Task<IReadOnlyList<FollowRequest>> GetAllPendingForAsync(Guid targetId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FollowRequest>> GetAllOfUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(FollowRequest request);

    void Remove(FollowRequest request);
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

/// <summary>
/// What a person has decided about others. It changes rarely and is read in almost every request.
/// Their followers are not here: someone can have many, and what a viewer needs is in the graph of each of them.
/// </summary>
[ImmutableObject(true)]
public sealed record SocialGraph(IReadOnlyList<Guid> Following, IReadOnlyList<Guid> Blocked, IReadOnlyList<Guid> BlockedBy)
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
