using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Media.Domain;

namespace GeoWoof.Modules.Media.Application.Events;

/// <summary>Publishes the public integration events of the Media module (saved in its Outbox).</summary>
internal sealed class MediaAssetReadyHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider)
    : IDomainEventHandler<MediaAssetReadyDomainEvent>
{
    public Task Handle(MediaAssetReadyDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new MediaAssetReady(domainEvent.AssetId, domainEvent.OwnerId, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

internal sealed class MediaAssetRejectedHandler(IIntegrationEventPublisher publisher, TimeProvider timeProvider)
    : IDomainEventHandler<MediaAssetRejectedDomainEvent>
{
    public Task Handle(MediaAssetRejectedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        publisher.Publish(new MediaAssetRejected(domainEvent.AssetId, domainEvent.OwnerId, domainEvent.Reason, timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }
}

/// <summary>Erases every image of a deleted account (right to erasure, a Google Play requirement).</summary>
internal sealed class DeleteUserMediaOnUserDeleted(IMediaAssetRepository assets, IMediaStorage storage) : IIntegrationEventHandler<UserDeleted>
{
    public async Task Handle(UserDeleted integrationEvent, CancellationToken cancellationToken)
    {
        foreach (var asset in await assets.GetByOwnerAsync(integrationEvent.UserId, cancellationToken))
        {
            await storage.DeleteAsync(asset.AllKeys, cancellationToken);
            assets.Remove(asset);
        }
    }
}
