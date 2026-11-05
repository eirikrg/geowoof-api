using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.Modules.Social.Contracts;

[EventType("social.friend_request_sent.v1")]
public sealed record FriendRequestSent(Guid RequestId, Guid RequesterId, Guid AddresseeId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

[EventType("social.friend_request_accepted.v1")]
public sealed record FriendRequestAccepted(Guid RequestId, Guid RequesterId, Guid AddresseeId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>Two people stopped being friends, also when one of them blocked the other.</summary>
[EventType("social.friendship_removed.v1")]
public sealed record FriendshipRemoved(Guid UserAId, Guid UserBId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

[EventType("social.user_blocked.v1")]
public sealed record UserBlocked(Guid BlockerId, Guid BlockedId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);
