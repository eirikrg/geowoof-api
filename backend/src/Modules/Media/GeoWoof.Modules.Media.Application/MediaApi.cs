using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Media.Domain;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Media.Application;

internal sealed class MediaApi(
    IMediaAssetRepository assets,
    IMediaStorage storage,
    IMediaProcessingSignal processingSignal,
    IMediaUnitOfWork unitOfWork,
    IOptions<MediaSettings> settings,
    TimeProvider timeProvider) : IMediaApi
{
    private static readonly MediaAttachFailure NotFound = new("media.not_found", "The image does not exist.", IsNotFound: true);

    private static readonly MediaAttachFailure NotUploaded =
        new("media.not_uploaded", "The image has not finished uploading.", IsNotFound: false);

    private static readonly MediaAttachFailure Rejected = new("media.asset_rejected", "The image was rejected.", IsNotFound: false);

    private static readonly MediaAttachFailure WrongPurpose =
        new("media.wrong_purpose", "The image was uploaded for another purpose.", IsNotFound: false);

    public async Task<MediaAttachFailure?> AttachAsync(Guid assetId, Guid ownerId, MediaUsage usage, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var asset = await assets.GetByIdAsync(assetId, cancellationToken);
            if (asset is null || asset.OwnerId != ownerId)
            {
                return NotFound;
            }

            if (asset.Purpose != ToPurpose(usage))
            {
                return WrongPurpose;
            }

            switch (asset.Status)
            {
                case MediaAssetStatus.Pending:
                    return NotUploaded;
                case MediaAssetStatus.Rejected:
                    return Rejected;
            }

            asset.Attach();
            if (await TrySaveAsync(attempt, cancellationToken))
            {
                return null;
            }
        }
    }

    public async Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var asset = await assets.GetByIdAsync(assetId, cancellationToken);
            if (asset is null || asset.OwnerId != ownerId)
            {
                return;
            }

            asset.Release(timeProvider.GetUtcNow());
            if (await TrySaveAsync(attempt, cancellationToken))
            {
                return;
            }
        }
    }

    public async Task<IReadOnlyDictionary<Guid, MediaImage>> GetImagesAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0)
        {
            return new Dictionary<Guid, MediaImage>();
        }

        var expiresAt = timeProvider.GetUtcNow() + settings.Value.DownloadUrlLifetime;
        var found = await assets.GetByIdsAsync(assetIds, cancellationToken);
        return found.ToDictionary(asset => asset.Id, asset => ToImage(asset, expiresAt));
    }

    public async Task<Guid> ImportAsync(Guid ownerId, MediaUsage usage, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        await storage.EnsureReadyAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var asset = MediaAsset.Request(Guid.CreateVersion7(now), ownerId, ToPurpose(usage), contentType, content.Length, now, now);
        await storage.WriteAsync(asset.OriginalKey, content, contentType, cancellationToken);
        asset.ConfirmUpload();
        assets.Add(asset);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        processingSignal.Notify();
        return asset.Id;
    }

    /// <summary>
    /// The processor saves an image twice, when it is ready and again once the original is deleted, so a client that
    /// uses it as soon as it is ready can find it changed in between. Attaching or releasing does not depend on that
    /// change: on a conflict, the asset is read again and the change repeated.
    /// </summary>
    private async Task<bool> TrySaveAsync(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (ConcurrencyConflictException) when (attempt < MediaAssetChanges.MaxSaveAttempts)
        {
            unitOfWork.DiscardChanges();
            return false;
        }
    }

    private MediaImage ToImage(MediaAsset asset, DateTimeOffset expiresAt) =>
        asset.Status == MediaAssetStatus.Ready
            ? new MediaImage(
                asset.Id,
                storage.CreateDownloadUrl(asset.ThumbKey, expiresAt).ToString(),
                storage.CreateDownloadUrl(asset.MediumKey, expiresAt).ToString())
            : new MediaImage(asset.Id, ThumbUrl: null, MediumUrl: null);

    private static MediaPurpose ToPurpose(MediaUsage usage) => usage switch
    {
        MediaUsage.UserAvatar => MediaPurpose.UserAvatar,
        MediaUsage.PetAvatar => MediaPurpose.PetAvatar,
        MediaUsage.WalkPhoto => MediaPurpose.WalkPhoto,
        MediaUsage.PostImage => MediaPurpose.PostImage,
        MediaUsage.ChatImage => MediaPurpose.ChatImage,
        _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "Unknown media usage."),
    };
}

/// <summary>Requests and the processor change the same asset: who finds it changed tries again, this many times in all.</summary>
internal static class MediaAssetChanges
{
    public const int MaxSaveAttempts = 3;
}
