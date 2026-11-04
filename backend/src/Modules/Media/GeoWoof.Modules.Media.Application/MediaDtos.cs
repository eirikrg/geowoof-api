using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Domain;

namespace GeoWoof.Modules.Media.Application;

public sealed record MediaUploadDto(Guid AssetId, Uri UploadUrl, IReadOnlyDictionary<string, string> UploadHeaders, DateTimeOffset ExpiresAt);

public sealed record MediaAssetDto(
    Guid Id,
    string Purpose,
    string Status,
    int? Width,
    int? Height,
    Uri? ThumbUrl,
    Uri? MediumUrl,
    string? RejectionCode)
{
    internal static MediaAssetDto From(MediaAsset asset, IMediaStorage storage, DateTimeOffset urlsExpireAt)
    {
        var isReady = asset.Status == MediaAssetStatus.Ready;
        return new MediaAssetDto(
            asset.Id,
            asset.Purpose.ToString(),
            asset.Status.ToString(),
            asset.Width,
            asset.Height,
            isReady ? storage.CreateDownloadUrl(asset.ThumbKey, urlsExpireAt) : null,
            isReady ? storage.CreateDownloadUrl(asset.MediumKey, urlsExpireAt) : null,
            asset.RejectionCode);
    }
}
