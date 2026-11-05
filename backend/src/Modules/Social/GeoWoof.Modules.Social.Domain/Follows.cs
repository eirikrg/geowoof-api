using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Social.Domain;

public enum FollowRequestStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
    Cancelled = 3,
}

public sealed record UserFollowedDomainEvent(Guid FollowerId, Guid FolloweeId) : IDomainEvent;

public sealed record FollowRequestedDomainEvent(Guid RequestId, Guid RequesterId, Guid TargetId) : IDomainEvent;

public sealed record FollowRequestAcceptedDomainEvent(Guid RequestId, Guid RequesterId, Guid TargetId) : IDomainEvent;

public sealed record FriendshipStartedDomainEvent(Guid UserAId, Guid UserBId) : IDomainEvent;

public sealed record FriendshipRemovedDomainEvent(Guid UserAId, Guid UserBId) : IDomainEvent;

public sealed record UserBlockedDomainEvent(Guid BlockerId, Guid BlockedId) : IDomainEvent;

/// <summary>
/// One person follows another. Two people who follow each other are friends: that is not stored,
/// so there is no second truth to keep in step.
/// </summary>
public sealed class Follow : AggregateRoot
{
    /// <summary>Constructor for EF Core.</summary>
    private Follow()
    {
    }

    private Follow(Guid id, Guid followerId, Guid followeeId, DateTimeOffset now)
        : base(id)
    {
        FollowerId = followerId;
        FolloweeId = followeeId;
        CreatedAt = now;
    }

    public Guid FollowerId { get; private set; }

    public Guid FolloweeId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <param name="id">The id of the new row.</param>
    /// <param name="followerId">Who follows.</param>
    /// <param name="followeeId">Who is followed.</param>
    /// <param name="theyFollowBack">Whether the other person already follows the follower: they become friends.</param>
    /// <param name="now">The current time.</param>
    public static Follow Start(Guid id, Guid followerId, Guid followeeId, bool theyFollowBack, DateTimeOffset now)
    {
        var follow = new Follow(id, followerId, followeeId, now);
        follow.Raise(new UserFollowedDomainEvent(followerId, followeeId));
        if (theyFollowBack)
        {
            follow.Raise(new FriendshipStartedDomainEvent(followerId, followeeId));
        }

        return follow;
    }

    /// <summary>Marks it for removal; the repository removes it in the same unit of work.</summary>
    /// <param name="theyFollowBack">Whether the other person follows back: they stop being friends.</param>
    public void End(bool theyFollowBack)
    {
        if (theyFollowBack)
        {
            Raise(new FriendshipRemovedDomainEvent(FollowerId, FolloweeId));
        }
    }
}

/// <summary>
/// Someone asks to follow a private profile. It only goes from <see cref="FollowRequestStatus.Pending"/>
/// to one of the others, and repeating the same answer succeeds, in case the first response was lost.
/// </summary>
public sealed class FollowRequest : AggregateRoot
{
    /// <summary>Constructor for EF Core.</summary>
    private FollowRequest()
    {
    }

    private FollowRequest(Guid id, Guid requesterId, Guid targetId, DateTimeOffset now)
        : base(id)
    {
        RequesterId = requesterId;
        TargetId = targetId;
        Status = FollowRequestStatus.Pending;
        CreatedAt = now;
    }

    public Guid RequesterId { get; private set; }

    public Guid TargetId { get; private set; }

    public FollowRequestStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RespondedAt { get; private set; }

    public static FollowRequest Send(Guid id, Guid requesterId, Guid targetId, DateTimeOffset now)
    {
        var request = new FollowRequest(id, requesterId, targetId, now);
        request.Raise(new FollowRequestedDomainEvent(id, requesterId, targetId));
        return request;
    }

    public Guid OtherThan(Guid userId) => userId == RequesterId ? TargetId : RequesterId;

    public Result Accept(DateTimeOffset now)
    {
        var wasPending = Status == FollowRequestStatus.Pending;
        var result = MoveTo(FollowRequestStatus.Accepted, now);
        if (wasPending && result.IsSuccess)
        {
            Raise(new FollowRequestAcceptedDomainEvent(Id, RequesterId, TargetId));
        }

        return result;
    }

    public Result Reject(DateTimeOffset now) => MoveTo(FollowRequestStatus.Rejected, now);

    public Result Cancel(DateTimeOffset now) => MoveTo(FollowRequestStatus.Cancelled, now);

    private Result MoveTo(FollowRequestStatus status, DateTimeOffset now)
    {
        if (Status == status)
        {
            return Result.Success();
        }

        if (Status != FollowRequestStatus.Pending)
        {
            return SocialErrors.RequestNotPending;
        }

        Status = status;
        RespondedAt = now;
        return Result.Success();
    }
}

/// <summary>One person blocked another. It hides each of them from the other, in both directions.</summary>
public sealed class UserBlock : AggregateRoot
{
    /// <summary>Constructor for EF Core.</summary>
    private UserBlock()
    {
    }

    private UserBlock(Guid id, Guid blockerId, Guid blockedId, DateTimeOffset now)
        : base(id)
    {
        BlockerId = blockerId;
        BlockedId = blockedId;
        CreatedAt = now;
    }

    public Guid BlockerId { get; private set; }

    public Guid BlockedId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static UserBlock Create(Guid id, Guid blockerId, Guid blockedId, DateTimeOffset now)
    {
        var block = new UserBlock(id, blockerId, blockedId, now);
        block.Raise(new UserBlockedDomainEvent(blockerId, blockedId));
        return block;
    }
}

public enum ReportTargetType
{
    User = 0,
}

public enum ReportReason
{
    Spam = 0,
    Harassment = 1,
    InappropriateContent = 2,
    Impersonation = 3,
    Other = 4,
}

public enum ReportStatus
{
    Open = 0,
}

/// <summary>Someone reports something to the moderators. It has no effect on the reported person by itself.</summary>
public sealed class Report : Entity
{
    public const int DetailsMaxLength = 500;

    /// <summary>Constructor for EF Core.</summary>
    private Report()
    {
    }

    private Report(Guid id, Guid reporterId, ReportTargetType targetType, Guid targetId, ReportReason reason, string? details, DateTimeOffset now)
        : base(id)
    {
        ReporterId = reporterId;
        TargetType = targetType;
        TargetId = targetId;
        Reason = reason;
        Details = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        Status = ReportStatus.Open;
        CreatedAt = now;
    }

    public Guid ReporterId { get; private set; }

    public ReportTargetType TargetType { get; private set; }

    public Guid TargetId { get; private set; }

    public ReportReason Reason { get; private set; }

    public string? Details { get; private set; }

    public ReportStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Report Create(Guid id, Guid reporterId, ReportTargetType targetType, Guid targetId, ReportReason reason, string? details, DateTimeOffset now) =>
        new(id, reporterId, targetType, targetId, reason, details, now);
}
