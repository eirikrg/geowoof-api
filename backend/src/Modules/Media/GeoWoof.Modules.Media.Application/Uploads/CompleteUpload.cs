using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Domain;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Media.Application.Uploads;

/// <summary>
/// The client finished uploading: checks what is really in storage and hands the image over to the
/// background processor. Idempotent: an image already confirmed just reports its current state.
/// </summary>
public sealed record CompleteUploadCommand(Guid OwnerId, Guid AssetId) : ICommand<MediaAssetDto>;

internal sealed class CompleteUploadCommandHandler(
    IMediaAssetRepository assets,
    IMediaStorage storage,
    IMediaProcessingSignal processingSignal,
    IMediaUnitOfWork unitOfWork,
    IOptions<MediaSettings> settings,
    TimeProvider timeProvider) : ICommandHandler<CompleteUploadCommand, MediaAssetDto>
{
    public async Task<Result<MediaAssetDto>> Handle(CompleteUploadCommand command, CancellationToken cancellationToken)
    {
        var asset = await assets.GetByIdAsync(command.AssetId, cancellationToken);
        if (asset is null || asset.OwnerId != command.OwnerId)
        {
            return MediaErrors.NotFound;
        }

        if (asset.Status == MediaAssetStatus.Pending)
        {
            var stored = await storage.GetInfoAsync(asset.OriginalKey, cancellationToken);
            if (stored is null)
            {
                return MediaErrors.UploadMissing;
            }

            if (stored.SizeBytes != asset.SizeBytes || !string.Equals(stored.ContentType, asset.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                asset.Reject(MediaErrors.UploadMismatchCode);
                asset.MarkOriginalRemoved();
                await storage.DeleteAsync([asset.OriginalKey], cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return MediaErrors.UploadMismatch;
            }

            asset.ConfirmUpload();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            processingSignal.Notify();
        }

        return MediaAssetDto.From(asset, storage, timeProvider.GetUtcNow() + settings.Value.DownloadUrlLifetime);
    }
}

public sealed record GetMediaAssetQuery(Guid OwnerId, Guid AssetId) : IQuery<MediaAssetDto>;

internal sealed class GetMediaAssetQueryHandler(
    IMediaAssetRepository assets,
    IMediaStorage storage,
    IOptions<MediaSettings> settings,
    TimeProvider timeProvider) : IQueryHandler<GetMediaAssetQuery, MediaAssetDto>
{
    public async Task<Result<MediaAssetDto>> Handle(GetMediaAssetQuery query, CancellationToken cancellationToken)
    {
        var asset = await assets.GetByIdAsync(query.AssetId, cancellationToken);
        return asset is null || asset.OwnerId != query.OwnerId
            ? MediaErrors.NotFound
            : MediaAssetDto.From(asset, storage, timeProvider.GetUtcNow() + settings.Value.DownloadUrlLifetime);
    }
}
