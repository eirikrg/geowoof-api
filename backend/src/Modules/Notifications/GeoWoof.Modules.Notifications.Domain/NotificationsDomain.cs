using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Notifications.Domain;

public static class NotificationsErrors
{
    public static readonly Error NotFound = Error.NotFound("notifications.not_found", "That notification does not exist.");

    public static readonly Error InvalidCursor = Error.Validation("request.invalid_cursor", "The cursor is not valid.");
}

public enum NotificationType
{
    UserFollowed = 0,
    FollowRequested = 1,
    FollowRequestAccepted = 2,
    FriendshipStarted = 3,
    PostLiked = 4,
    CommentAdded = 5,
    PostReposted = 6,
    UserMentioned = 7,
}

/// <summary>Something that someone did and someone else should know about.</summary>
public sealed class Notification : Entity
{
    /// <summary>Constructor for EF Core.</summary>
    private Notification()
    {
    }

    private Notification(Guid id, Guid recipientId, Guid actorId, NotificationType type, Guid? postId, Guid? commentId, Guid? requestId, DateTimeOffset now)
        : base(id)
    {
        RecipientId = recipientId;
        ActorId = actorId;
        Type = type;
        PostId = postId;
        CommentId = commentId;
        RequestId = requestId;
        DedupeKey = KeyOf(recipientId, actorId, type, postId, commentId, requestId);
        CreatedAt = now;
    }

    public Guid RecipientId { get; private set; }

    public Guid ActorId { get; private set; }

    public NotificationType Type { get; private set; }

    public Guid? PostId { get; private set; }

    public Guid? CommentId { get; private set; }

    public Guid? RequestId { get; private set; }

    /// <summary>The same act has the same key, so that repeating it does not tell anyone twice.</summary>
    public string DedupeKey { get; private set; } = string.Empty;

    public DateTimeOffset? ReadAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static string KeyOf(Guid recipientId, Guid actorId, NotificationType type, Guid? postId, Guid? commentId, Guid? requestId) =>
        $"{recipientId:N}:{actorId:N}:{type}:{postId:N}:{commentId:N}:{requestId:N}";

    public static Notification Create(Guid id, Guid recipientId, Guid actorId, NotificationType type, Guid? postId, Guid? commentId, Guid? requestId, DateTimeOffset now) =>
        new(id, recipientId, actorId, type, postId, commentId, requestId, now);

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}

/// <summary>Where to send a push. A device belongs to the last account that registered it.</summary>
public sealed class DeviceToken : Entity
{
    public const int TokenMaxLength = 4096;

    /// <summary>Constructor for EF Core.</summary>
    private DeviceToken()
    {
    }

    private DeviceToken(Guid id, string token, Guid userId, string platform, DateTimeOffset now)
        : base(id)
    {
        Token = token;
        UserId = userId;
        Platform = platform;
        UpdatedAt = now;
    }

    public string Token { get; private set; } = string.Empty;

    public Guid UserId { get; private set; }

    public string Platform { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DeviceToken Register(Guid id, string token, Guid userId, string platform, DateTimeOffset now) =>
        new(id, token, userId, platform, now);

    public void AssignTo(Guid userId, string platform, DateTimeOffset now)
    {
        UserId = userId;
        Platform = platform;
        UpdatedAt = now;
    }
}
