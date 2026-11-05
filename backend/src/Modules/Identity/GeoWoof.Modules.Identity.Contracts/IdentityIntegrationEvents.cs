using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.Modules.Identity.Contracts;

/// <summary>The public profile of a user changed (username or display name). Lets other modules refresh their caches.</summary>
[EventType("identity.user_profile_updated.v1")]
public sealed record UserProfileUpdated(Guid UserId, string? Username, string DisplayName, DateTimeOffset OccurredAt)
    : IntegrationEvent(OccurredAt);

/// <summary>Someone made their profile public or private.</summary>
[EventType("identity.user_privacy_changed.v1")]
public sealed record UserPrivacyChanged(Guid UserId, bool IsPrivate, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>A user account was deleted. Every module must delete or anonymise the user's data.</summary>
[EventType("identity.user_deleted.v1")]
public sealed record UserDeleted(Guid UserId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);
