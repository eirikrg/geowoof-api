using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Domain;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Media.Application.Processing;

/// <summary>Turns a confirmed upload into clean variants and removes the original, which may hold EXIF metadata.</summary>
public sealed class MediaAssetProcessor(
    IMediaAssetRepository assets,
    IMediaStorage storage,
    IImageProcessor imageProcessor,
    IMediaUnitOfWork unitOfWork,
    IOptions<MediaSettings> settings)
{
    private const string WebP = "image/webp";

    public async Task ProcessAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await assets.GetByIdAsync(assetId, cancellationToken);
        if (asset is null || asset.Status != MediaAssetStatus.Processing)
        {
            return;
        }

        var original = await storage.ReadAsync(asset.OriginalKey, cancellationToken);
        var result = imageProcessor.Process(original, settings.Value.MaxImagePixels);
        if (result.Variants is { } variants)
        {
            await storage.WriteAsync(asset.ThumbKey, variants.Thumb, WebP, cancellationToken);
            await storage.WriteAsync(asset.MediumKey, variants.Medium, WebP, cancellationToken);
            asset.MarkReady(variants.Width, variants.Height);
        }
        else
        {
            asset.Reject(result.RejectionCode ?? MediaErrors.InvalidImageCode);
        }

        // Saved first: if the deletion fails, the cleanup retries it, and clients never see a half-finished image.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync([asset.OriginalKey], cancellationToken);
        asset.MarkOriginalRemoved();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A processing attempt failed unexpectedly: gives up once the attempts run out. Until then, the lease expiry retries it.</summary>
    public async Task RecordFailureAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await assets.GetByIdAsync(assetId, cancellationToken);
        if (asset is { Status: MediaAssetStatus.Processing } && asset.ProcessingAttempts >= settings.Value.ProcessingMaxAttempts)
        {
            asset.Reject(MediaErrors.ProcessingFailedCode);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>Deletes what nobody uses: expired uploads, unattached images and leftover originals.</summary>
public sealed class MediaCleanup(
    IMediaAssetRepository assets,
    IMediaStorage storage,
    IMediaUnitOfWork unitOfWork,
    IOptions<MediaSettings> settings,
    TimeProvider timeProvider)
{
    private const int BatchSize = 100;

    /// <summary>A client may still be finishing an upload that started just before its link expired.</summary>
    private static readonly TimeSpan UploadGracePeriod = TimeSpan.FromHours(1);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var abandoned = await assets.GetAbandonedAsync(now - UploadGracePeriod, now - settings.Value.OrphanRetention, BatchSize, cancellationToken);
        foreach (var asset in abandoned)
        {
            await storage.DeleteAsync(asset.AllKeys, cancellationToken);
            assets.Remove(asset);
        }

        var leftovers = await assets.GetWithLeftoverOriginalAsync(BatchSize, cancellationToken);
        foreach (var asset in leftovers)
        {
            await storage.DeleteAsync([asset.OriginalKey], cancellationToken);
            asset.MarkOriginalRemoved();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return abandoned.Count + leftovers.Count;
    }
}
