namespace GeoWoof.BuildingBlocks.Domain.Entities;

/// <summary>
/// Something meaningful that happened inside an aggregate. Domain events are dispatched in-process,
/// inside the same transaction, when the module's DbContext saves changes.
/// </summary>
public interface IDomainEvent;
