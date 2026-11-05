using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Contracts;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Events;

/// <summary>Publishes the public integration events of the Social module (saved in its Outbox).</summary>
internal sealed class FriendRequestSentHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<FriendRequestSentDomainEvent>
{
    public Task Handle(FriendRequestSentDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new FriendRequestSent(domainEvent.RequestId, domainEvent.RequesterId, domainEvent.AddresseeId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class FriendRequestAcceptedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<FriendRequestAcceptedDomainEvent>
{
    public Task Handle(FriendRequestAcceptedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new FriendRequestAccepted(domainEvent.RequestId, domainEvent.RequesterId, domainEvent.AddresseeId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class FriendshipRemovedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<FriendshipRemovedDomainEvent>
{
    public Task Handle(FriendshipRemovedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new FriendshipRemoved(domainEvent.UserAId, domainEvent.UserBId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class UserBlockedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<UserBlockedDomainEvent>
{
    public Task Handle(UserBlockedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new UserBlocked(domainEvent.BlockerId, domainEvent.BlockedId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Erases everything that links a deleted account to other people. Their cached graphs can still
/// list that account for a few minutes, which is harmless because it no longer exists.
/// </summary>
internal sealed class DeleteSocialDataOnUserDeleted(
    IFriendRequestRepository requests,
    IFriendshipRepository friendships,
    IUserBlockRepository blocks,
    IReportRepository reports) : IIntegrationEventHandler<UserDeleted>
{
    public async Task Handle(UserDeleted integrationEvent, CancellationToken cancellationToken)
    {
        var userId = integrationEvent.UserId;
        foreach (var request in await requests.GetAllOfUserAsync(userId, cancellationToken))
        {
            requests.Remove(request);
        }

        foreach (var friendship in await friendships.GetAllOfUserAsync(userId, cancellationToken))
        {
            friendship.End();
            friendships.Remove(friendship);
        }

        foreach (var block in await blocks.GetAllOfUserAsync(userId, cancellationToken))
        {
            blocks.Remove(block);
        }

        foreach (var report in await reports.GetAllOfUserAsync(userId, cancellationToken))
        {
            reports.Remove(report);
        }
    }
}
