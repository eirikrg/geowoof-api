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
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken)
    {
        var asset = await assets.GetByIdAsync(assetId, cancellationToken);
        if (asset is not null && asset.OwnerId == ownerId)
        {
            asset.Release(timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
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
