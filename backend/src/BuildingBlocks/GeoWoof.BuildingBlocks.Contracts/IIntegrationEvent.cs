namespace GeoWoof.BuildingBlocks.Contracts;

/// <summary>
/// A fact published by one module for others. Integration events travel through the Outbox,
/// so they must only contain primitives or contract DTOs (never domain types).
/// </summary>
public interface IIntegrationEvent
{
    Guid Id { get; }

    DateTimeOffset OccurredAt { get; }
}
