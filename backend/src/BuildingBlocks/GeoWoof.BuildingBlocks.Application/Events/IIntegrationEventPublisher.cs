using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.BuildingBlocks.Application.Events;

/// <summary>
/// Queues integration events for the current unit of work. They are written to the Outbox of the
/// module DbContext that saves changes next in the same scope, atomically with that save.
/// </summary>
public interface IIntegrationEventPublisher
{
    void Publish(IIntegrationEvent integrationEvent);
}
