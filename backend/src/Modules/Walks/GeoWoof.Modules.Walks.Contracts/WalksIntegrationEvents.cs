using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.Modules.Walks.Contracts;

[EventType("walks.walk_recorded.v1")]
public sealed record WalkRecorded(Guid WalkId, Guid OwnerId, IReadOnlyList<Guid> PetIds, int DistanceM, DateTimeOffset StartedAt, DateTimeOffset OccurredAt)
    : IntegrationEvent(OccurredAt);

/// <summary>A walk was deleted, also when its owner deleted the account.</summary>
[EventType("walks.walk_deleted.v1")]
public sealed record WalkDeleted(Guid WalkId, Guid OwnerId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);
