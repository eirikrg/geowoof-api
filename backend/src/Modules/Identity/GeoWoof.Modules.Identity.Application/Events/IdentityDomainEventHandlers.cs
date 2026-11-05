using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.Application.Events;

/// <summary>Publishes the public integration events of the Identity module (saved in its Outbox).</summary>
internal sealed class UserProfileChangedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider)
    : IDomainEventHandler<UserProfileChangedDomainEvent>
{
    public Task Handle(UserProfileChangedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new UserProfileUpdated(domainEvent.UserId, domainEvent.Username, domainEvent.DisplayName, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class UserPrivacyChangedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider)
    : IDomainEventHandler<UserPrivacyChangedDomainEvent>
{
    public Task Handle(UserPrivacyChangedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new UserPrivacyChanged(domainEvent.UserId, domainEvent.IsPrivate, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class UserDeletedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider)
    : IDomainEventHandler<UserDeletedDomainEvent>
{
    public Task Handle(UserDeletedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new UserDeleted(domainEvent.UserId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}
