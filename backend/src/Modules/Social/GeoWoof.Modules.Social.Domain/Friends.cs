using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Social.Domain;

public enum FriendRequestStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
    Cancelled = 3,
}

public sealed record FriendRequestSentDomainEvent(Guid RequestId, Guid RequesterId, Guid AddresseeId) : IDomainEvent;

public sealed record FriendRequestAcceptedDomainEvent(Guid RequestId, Guid RequesterId, Guid AddresseeId) : IDomainEvent;

public sealed record FriendshipRemovedDomainEvent(Guid UserAId, Guid UserBId) : IDomainEvent;

public sealed record UserBlockedDomainEvent(Guid BlockerId, Guid BlockedId) : IDomainEvent;

/// <summary>An unordered pair of people, stored always in the same order so that a database index can make it unique.</summary>
public readonly record struct UserPair
{
    public UserPair(Guid first, Guid second)
    {
        Low = first.CompareTo(second) < 0 ? first : second;
        High = first.CompareTo(second) < 0 ? second : first;
    }

    public Guid Low { get; }

    public Guid High { get; }
}

/// <summary>
/// One person asks another to be friends. It only goes from <see cref="FriendRequestStatus.Pending"/>
/// to one of the others, and repeating the same answer succeeds, in case the first response was lost.
/// </summary>
public sealed class FriendRequest : AggregateRoot
{
    /// <summary>Constructor for EF Core.</summary>
    private FriendRequest()
    {
    }

    private FriendRequest(Guid id, Guid requesterId, Guid addresseeId, DateTimeOffset now)
        : base(id)
    {
        var pair = new UserPair(requesterId, addresseeId);
        RequesterId = requesterId;
        AddresseeId = addresseeId;
        UserLowId = pair.Low;
        UserHighId = pair.High;
        Status = FriendRequestStatus.Pending;
        CreatedAt = now;
    }

    public Guid RequesterId { get; private set; }

    public Guid AddresseeId { get; private set; }

    /// <summary>With <see cref="UserHighId"/>, the key of the only pending request between two people.</summary>
    public Guid UserLowId { get; private set; }

    public Guid UserHighId { get; private set; }

    public FriendRequestStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RespondedAt { get; private set; }

    public static FriendRequest Send(Guid id, Guid requesterId, Guid addresseeId, DateTimeOffset now)
    {
        var request = new FriendRequest(id, requesterId, addresseeId, now);
        request.Raise(new FriendRequestSentDomainEvent(id, requesterId, addresseeId));
        return request;
    }

    public Guid OtherThan(Guid userId) => userId == RequesterId ? AddresseeId : RequesterId;

    public Result Accept(DateTimeOffset now)
    {
        var wasPending = Status == FriendRequestStatus.Pending;
        var result = MoveTo(FriendRequestStatus.Accepted, now);
        if (wasPending && result.IsSuccess)
        {
            Raise(new FriendRequestAcceptedDomainEvent(Id, RequesterId, AddresseeId));
        }

        return result;
    }

    public Result Reject(DateTimeOffset now) => MoveTo(FriendRequestStatus.Rejected, now);

    public Result Cancel(DateTimeOffset now) => MoveTo(FriendRequestStatus.Cancelled, now);

    private Result MoveTo(FriendRequestStatus status, DateTimeOffset now)
    {
        if (Status == status)
        {
            return Result.Success();
        }

        if (Status != FriendRequestStatus.Pending)
        {
            return SocialErrors.RequestNotPending;
        }

        Status = status;
        RespondedAt = now;
        return Result.Success();
    }
}

/// <summary>Two people are friends. There is one row per pair, whoever asked.</summary>
public sealed class Friendship : AggregateRoot
{
    /// <summary>Constructor for EF Core.</summary>
    private Friendship()
    {
    }

    private Friendship(Guid id, UserPair pair, DateTimeOffset now)
        : base(id)
    {
        UserAId = pair.Low;
        UserBId = pair.High;
        CreatedAt = now;
    }

    public Guid UserAId { get; private set; }

    public Guid UserBId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Friendship Between(Guid id, Guid first, Guid second, DateTimeOffset now) => new(id, new UserPair(first, second), now);

    public Guid OtherThan(Guid userId) => userId == UserAId ? UserBId : UserAId;

    /// <summary>Marks the friendship for removal; the repository removes it in the same unit of work.</summary>
    public void End() => Raise(new FriendshipRemovedDomainEvent(UserAId, UserBId));
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
