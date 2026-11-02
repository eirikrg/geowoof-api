using GeoWoof.BuildingBlocks.Domain.Entities;

namespace GeoWoof.BuildingBlocks.Application.Events;

/// <summary>
/// Reacts to a domain event of the same module. Runs inside the transaction of the
/// <c>SaveChangesAsync</c> call that persists the aggregate, before the commit.
/// </summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}
