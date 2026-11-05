using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Contracts;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Events;

/// <summary>Publishes the public integration events of the Social module (saved in its Outbox).</summary>
internal sealed class UserFollowedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<UserFollowedDomainEvent>
{
    public Task Handle(UserFollowedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new UserFollowed(domainEvent.FollowerId, domainEvent.FolloweeId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class FollowRequestedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<FollowRequestedDomainEvent>
{
    public Task Handle(FollowRequestedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new FollowRequested(domainEvent.RequestId, domainEvent.RequesterId, domainEvent.TargetId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class FollowRequestAcceptedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<FollowRequestAcceptedDomainEvent>
{
    public Task Handle(FollowRequestAcceptedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new FollowRequestAccepted(domainEvent.RequestId, domainEvent.RequesterId, domainEvent.TargetId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class FriendshipStartedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider) : IDomainEventHandler<FriendshipStartedDomainEvent>
{
    public Task Handle(FriendshipStartedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new FriendshipStarted(domainEvent.UserAId, domainEvent.UserBId, timeProvider.GetUtcNow()));
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

/// <summary>A profile that becomes public has no one left to ask: those waiting become followers.</summary>
internal sealed class AcceptRequestsOnProfilePublic(
    IFollowRequestRepository requests,
    FollowService followService,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IIntegrationEventHandler<UserPrivacyChanged>
{
    public async Task Handle(UserPrivacyChanged integrationEvent, CancellationToken cancellationToken)
    {
        if (integrationEvent.IsPrivate)
        {
            return;
        }

        var pending = await requests.GetAllPendingForAsync(integrationEvent.UserId, cancellationToken);
        foreach (var request in pending)
        {
            request.Accept(timeProvider.GetUtcNow());
            await followService.StartAsync(request.RequesterId, request.TargetId, cancellationToken);
        }

        // Saved here, and not by the dispatcher, so that the graphs are read again after the change.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var request in pending)
        {
            await graphs.InvalidateAsync(request.RequesterId, request.TargetId, cancellationToken);
        }
    }
}

/// <summary>
/// Erases everything that links a deleted account to other people. Their cached graphs can still
/// list that account for a few minutes, which is harmless because it no longer exists.
/// </summary>
internal sealed class DeleteSocialDataOnUserDeleted(
    IFollowRequestRepository requests,
    IFollowRepository follows,
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

        foreach (var follow in await follows.GetAllOfUserAsync(userId, cancellationToken))
        {
            follows.Remove(follow);
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
