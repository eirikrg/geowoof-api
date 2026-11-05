using GeoWoof.Modules.Walks.Domain.Routes;

namespace GeoWoof.Modules.Walks.Domain;

/// <summary>What someone other than the owner receives of a route. It can be empty.</summary>
public sealed record VisibleRoute(IReadOnlyList<WalkSegment> Segments, IReadOnlyList<string> Previews);

/// <summary>
/// Removes every point close to the start or the end of a walk, so that a route does not reveal where
/// someone lives (ADR-0006). The zone is measured in a straight line: after walking 200 m around
/// a block, someone can still be next to their door.
/// </summary>
public static class RoutePrivacy
{
    public const double HiddenZoneM = 200;

    private const int MinSegmentPoints = 2;

    public static VisibleRoute Hide(IReadOnlyList<WalkSegment> segments, double zoneM = HiddenZoneM)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var decoded = new List<List<TrackPoint>>(segments.Count);
        foreach (var segment in segments)
        {
            // Validated when it was saved. If it cannot be read, it is safer to show nothing.
            if (!Polyline.TryDecode(segment.Polyline, out var positions) || positions.Count != segment.TimeOffsetsMs.Count)
            {
                return new VisibleRoute([], []);
            }

            decoded.Add(positions.Select((position, i) => new TrackPoint(position, segment.TimeOffsetsMs[i])).ToList());
        }

        var all = decoded.SelectMany(points => points).ToList();
        if (all.Count == 0)
        {
            return new VisibleRoute([], []);
        }

        var start = all[0].Position;
        var end = all[^1].Position;
        var stretches = decoded.SelectMany(points => OutsideZones(points, start, end, zoneM)).ToList();
        return new VisibleRoute(
            stretches.Select(ToSegment).ToList(),
            stretches.Select(points => Polyline.Encode(RouteSimplifier.Simplify(points.Select(point => point.Position).ToList(), WalkTrack.PreviewToleranceM))).ToList());
    }

    /// <summary>A hidden point breaks the stretch: joining what is left would draw a line that nobody walked.</summary>
    private static IEnumerable<List<TrackPoint>> OutsideZones(List<TrackPoint> points, GeoPoint start, GeoPoint end, double zoneM)
    {
        var stretch = new List<TrackPoint>();
        foreach (var point in points)
        {
            if (GeoMath.DistanceM(point.Position, start) >= zoneM && GeoMath.DistanceM(point.Position, end) >= zoneM)
            {
                stretch.Add(point);
                continue;
            }

            if (stretch.Count >= MinSegmentPoints)
            {
                yield return stretch;
            }

            stretch = [];
        }

        if (stretch.Count >= MinSegmentPoints)
        {
            yield return stretch;
        }
    }

    private static WalkSegment ToSegment(List<TrackPoint> points) => new(
        Polyline.Encode(points.Select(point => point.Position).ToList()),
        points.Select(point => point.OffsetMs).ToList());
}
