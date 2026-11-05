using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.Modules.Social.Contracts;

[EventType("social.user_followed.v1")]
public sealed record UserFollowed(Guid FollowerId, Guid FolloweeId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>Someone asked to follow a private profile.</summary>
[EventType("social.follow_requested.v1")]
public sealed record FollowRequested(Guid RequestId, Guid RequesterId, Guid TargetId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

[EventType("social.follow_request_accepted.v1")]
public sealed record FollowRequestAccepted(Guid RequestId, Guid RequesterId, Guid TargetId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>Two people now follow each other.</summary>
[EventType("social.friendship_started.v1")]
public sealed record FriendshipStarted(Guid UserAId, Guid UserBId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>Two people stopped following each other, also when one of them blocked the other.</summary>
[EventType("social.friendship_removed.v1")]
public sealed record FriendshipRemoved(Guid UserAId, Guid UserBId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

[EventType("social.user_blocked.v1")]
public sealed record UserBlocked(Guid BlockerId, Guid BlockedId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

[EventType("social.post_created.v1")]
public sealed record PostCreated(Guid PostId, Guid AuthorId, string Visibility, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>Someone liked a post for the first time.</summary>
[EventType("social.post_liked.v1")]
public sealed record PostLiked(Guid PostId, Guid PostAuthorId, Guid UserId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

[EventType("social.comment_added.v1")]
public sealed record CommentAdded(Guid CommentId, Guid PostId, Guid PostAuthorId, Guid AuthorId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>Someone reposted a post for the first time.</summary>
[EventType("social.post_reposted.v1")]
public sealed record PostReposted(Guid RepostId, Guid PostId, Guid PostAuthorId, Guid UserId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>A mention that counts: that person can see what was written. <paramref name="CommentId"/> is <c>null</c> in a post.</summary>
[EventType("social.user_mentioned.v1")]
public sealed record UserMentioned(Guid MentionedUserId, Guid AuthorId, Guid PostId, Guid? CommentId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);
