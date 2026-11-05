using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Domain;

namespace GeoWoof.Modules.Walks.Application;

public sealed record WalkSummaryDto(
    Guid Id,
    Guid OwnerId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string TimeZone,
    IReadOnlyList<Guid> PetIds,
    string Visibility,
    string? Note,
    int DistanceM,
    int DurationS,
    int MovingTimeS,
    double AverageSpeedMps,
    int? PaceSecondsPerKm,
    IReadOnlyList<string> PreviewPolylines,
    IReadOnlyList<MediaImage> Photos,
    DateTimeOffset UpdatedAt);

public sealed record WalkSegmentDto(string Polyline, IReadOnlyList<long> TimeOffsetsMs);

public sealed record WalkDto(
    Guid Id,
    Guid OwnerId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string TimeZone,
    IReadOnlyList<Guid> PetIds,
    string Visibility,
    string? Note,
    int DistanceM,
    int DurationS,
    int MovingTimeS,
    double AverageSpeedMps,
    int? PaceSecondsPerKm,
    IReadOnlyList<string> PreviewPolylines,
    IReadOnlyList<MediaImage> Photos,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WalkSegmentDto> Segments);

public sealed record WalkStatsDto(int WalkCount, int DistanceM, int DurationS, int MovingTimeS, int WeeklyStreak);

/// <summary>The result of saving a walk: whether this request created it decides between 201 and 200.</summary>
public sealed record PutWalkResult(WalkDto Walk, bool Created);

internal static class WalkMapper
{
    /// <summary>With the whole route for its owner. For anyone else, without the zones that reveal where they live.</summary>
    public static async Task<WalkDto> ToDtoAsync(Walk walk, Guid viewerId, IMediaApi media, CancellationToken cancellationToken)
    {
        var photoIds = walk.PhotoAssetIds;
        var images = await media.GetImagesAsync(photoIds, cancellationToken);
        var route = walk.OwnerId == viewerId ? new VisibleRoute(walk.Segments, walk.PreviewPolylines) : RoutePrivacy.Hide(walk.Segments);
        return new WalkDto(
            walk.Id,
            walk.OwnerId,
            walk.StartedAt,
            walk.EndedAt,
            walk.TimeZone,
            walk.Pets.Select(pet => pet.PetId).ToList(),
            walk.Visibility.ToString(),
            walk.Note,
            walk.DistanceM,
            walk.DurationS,
            walk.MovingTimeS,
            walk.AverageSpeedMps,
            walk.PaceSecondsPerKm,
            route.Previews,
            Ordered(photoIds, images),
            walk.UpdatedAt,
            route.Segments.Select(segment => new WalkSegmentDto(segment.Polyline, segment.TimeOffsetsMs)).ToList());
    }

    /// <param name="rows">The walks of the page.</param>
    /// <param name="previews">What to show instead of the stored previews, for someone other than the owner.</param>
    /// <param name="media">Signs the URLs of the photos.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<IReadOnlyList<WalkSummaryDto>> ToSummariesAsync(
        IReadOnlyList<WalkSummaryRow> rows,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>>? previews,
        IMediaApi media,
        CancellationToken cancellationToken)
    {
        var photoIds = rows.SelectMany(row => row.PhotoAssetIds).ToList();
        var images = await media.GetImagesAsync(photoIds, cancellationToken);
        return rows.Select(row => new WalkSummaryDto(
            row.Id,
            row.OwnerId,
            row.StartedAt,
            row.EndedAt,
            row.TimeZone,
            row.PetIds,
            row.Visibility.ToString(),
            row.Note,
            row.DistanceM,
            row.DurationS,
            row.MovingTimeS,
            row.AverageSpeedMps,
            row.PaceSecondsPerKm,
            previews is null ? row.PreviewPolylines : previews.GetValueOrDefault(row.Id, []),
            Ordered(row.PhotoAssetIds, images),
            row.UpdatedAt)).ToList();
    }

    /// <summary>Photos in the order of the walk. One that no longer exists is left out.</summary>
    private static List<MediaImage> Ordered(IReadOnlyList<Guid> photoIds, IReadOnlyDictionary<Guid, MediaImage> images) =>
        photoIds.Where(images.ContainsKey).Select(id => images[id]).ToList();
}
