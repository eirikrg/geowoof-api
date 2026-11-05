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
