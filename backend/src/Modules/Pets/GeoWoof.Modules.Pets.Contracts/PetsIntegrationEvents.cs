using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.Modules.Pets.Contracts;

[EventType("pets.pet_created.v1")]
public sealed record PetCreated(Guid PetId, Guid OwnerId, string Name, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

[EventType("pets.pet_updated.v1")]
public sealed record PetUpdated(Guid PetId, Guid OwnerId, string Name, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>A pet was deleted, also when its owner deleted the account.</summary>
[EventType("pets.pet_deleted.v1")]
public sealed record PetDeleted(Guid PetId, Guid OwnerId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);
