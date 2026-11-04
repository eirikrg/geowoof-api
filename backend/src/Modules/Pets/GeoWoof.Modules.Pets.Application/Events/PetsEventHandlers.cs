using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Pets.Application.Abstractions;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Pets.Domain;

namespace GeoWoof.Modules.Pets.Application.Events;

/// <summary>Publishes the public integration events of the Pets module (saved in its Outbox).</summary>
internal sealed class PetCreatedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<PetCreatedDomainEvent>
{
    public Task Handle(PetCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new PetCreated(domainEvent.PetId, domainEvent.OwnerId, domainEvent.Name, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class PetUpdatedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<PetUpdatedDomainEvent>
{
    public Task Handle(PetUpdatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new PetUpdated(domainEvent.PetId, domainEvent.OwnerId, domainEvent.Name, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class PetDeletedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<PetDeletedDomainEvent>
{
    public Task Handle(PetDeletedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new PetDeleted(domainEvent.PetId, domainEvent.OwnerId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

/// <summary>Erases the pets of a deleted account. The Media module erases their photos on the same event.</summary>
internal sealed class DeletePetsOnUserDeleted(IPetRepository pets) : IIntegrationEventHandler<UserDeleted>
{
    public async Task Handle(UserDeleted integrationEvent, CancellationToken cancellationToken)
    {
        foreach (var pet in await pets.GetByOwnerAsync(integrationEvent.UserId, cancellationToken))
        {
            pet.Delete();
            pets.Remove(pet);
        }
    }
}
