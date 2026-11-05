using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Contracts;
using GeoWoof.Modules.Walks.Domain;

namespace GeoWoof.Modules.Walks.Application.Events;

/// <summary>Publishes the public integration events of the Walks module (saved in its Outbox).</summary>
internal sealed class WalkRecordedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<WalkRecordedDomainEvent>
{
    public Task Handle(WalkRecordedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new WalkRecorded(domainEvent.WalkId, domainEvent.OwnerId, domainEvent.PetIds, domainEvent.DistanceM, domainEvent.StartedAt, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class WalkDeletedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<WalkDeletedDomainEvent>
{
    public Task Handle(WalkDeletedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new WalkDeleted(domainEvent.WalkId, domainEvent.OwnerId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

/// <summary>Erases the walks of a deleted account. The Media module erases their photos on the same event.</summary>
internal sealed class DeleteWalksOnUserDeleted(IWalkRepository walks) : IIntegrationEventHandler<UserDeleted>
{
    public async Task Handle(UserDeleted integrationEvent, CancellationToken cancellationToken)
    {
        foreach (var walk in await walks.GetByOwnerAsync(integrationEvent.UserId, cancellationToken))
        {
            walk.Delete();
            walks.Remove(walk);
        }
    }
}

/// <summary>A walk is still a memory of its owner: it stays, without the deleted pet.</summary>
internal sealed class RemovePetFromWalksOnPetDeleted(IWalkRepository walks) : IIntegrationEventHandler<PetDeleted>
{
    public async Task Handle(PetDeleted integrationEvent, CancellationToken cancellationToken)
    {
        foreach (var walk in await walks.GetByPetAsync(integrationEvent.PetId, cancellationToken))
        {
            walk.RemovePet(integrationEvent.PetId);
        }
    }
}
