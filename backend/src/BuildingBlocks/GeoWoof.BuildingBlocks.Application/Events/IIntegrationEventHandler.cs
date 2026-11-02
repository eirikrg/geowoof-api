using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.BuildingBlocks.Application.Events;

/// <summary>
/// Reacts to an integration event published by any module. Delivery is at-least-once; the
/// infrastructure records each processed message (Inbox) in the consumer's schema, inside the
/// same transaction as the handler's changes, so the effect happens once per consumer.
/// Throw to signal a transient failure: the message is retried with backoff.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task Handle(TEvent integrationEvent, CancellationToken cancellationToken);
}
