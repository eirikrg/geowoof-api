using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Walks.Domain.Routes;

namespace GeoWoof.Modules.Walks.Domain;

/// <summary>A stretch of walking without pauses, as the API receives it and as it is stored.</summary>
public sealed record WalkSegment(string Polyline, IReadOnlyList<long> TimeOffsetsMs);

public readonly record struct TrackPoint(GeoPoint Position, long OffsetMs);

/// <summary>The route of a walk, validated and decoded (ADR-0006).</summary>
public sealed class WalkTrack
{
    public const int MaxPoints = 20_000;

    internal const double PreviewToleranceM = 5;

    private WalkTrack(IReadOnlyList<WalkSegment> segments, IReadOnlyList<IReadOnlyList<TrackPoint>> points)
    {
        Segments = segments;
        Points = points;
    }

    public IReadOnlyList<WalkSegment> Segments { get; }

    /// <summary>The decoded points of each segment.</summary>
    public IReadOnlyList<IReadOnlyList<TrackPoint>> Points { get; }

    public static Result<WalkTrack> Parse(IReadOnlyList<WalkSegment> segments, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var decoded = new List<IReadOnlyList<TrackPoint>>(segments.Count);
        var totalPoints = 0;
        var previousOffset = -1L;
        foreach (var segment in segments)
        {
            if (segment.Polyline is null
                || segment.TimeOffsetsMs is null
                || !Polyline.TryDecode(segment.Polyline, out var positions)
                || positions.Count == 0
                || positions.Count != segment.TimeOffsetsMs.Count)
            {
                return WalksErrors.InvalidTrack;
            }

            totalPoints += positions.Count;
            if (totalPoints > MaxPoints)
            {
                return WalksErrors.TooManyPoints;
            }

            var points = new List<TrackPoint>(positions.Count);
            for (var i = 0; i < positions.Count; i++)
            {
                var offset = segment.TimeOffsetsMs[i];
                if (offset <= previousOffset || offset > duration.TotalMilliseconds)
                {
                    return WalksErrors.InvalidTrack;
                }

                previousOffset = offset;
                points.Add(new TrackPoint(positions[i], offset));
            }

            decoded.Add(points);
        }

        return decoded.Exists(points => points.Count >= 2)
            ? new WalkTrack(segments, decoded)
            : WalksErrors.InvalidTrack;
    }

    /// <summary>One simplified polyline per segment, so that a pause does not draw a straight line in the previews.</summary>
    public IReadOnlyList<string> Previews() => Points
        .Where(points => points.Count >= 2)
        .Select(points => Polyline.Encode(RouteSimplifier.Simplify(points.Select(point => point.Position).ToList(), PreviewToleranceM)))
        .ToList();
}

/// <summary>The canonical numbers of a walk. They are always calculated here, never taken from the client.</summary>
public sealed record WalkStatistics(int DistanceM, int DurationS, int MovingTimeS, double AverageSpeedMps, int? PaceSecondsPerKm)
{
    /// <summary>Stored with each walk, so that walks can be recalculated if the algorithm changes.</summary>
    public const int AlgorithmVersion = 1;

    /// <summary>Faster than anyone walks, even running with a dog: such a step is a GPS error (ADR-0006).</summary>
    public const double ImpossibleSpeedMps = 8.5;

    public const double MovingSpeedMps = 0.5;

    private const double MetersPerKm = 1000;
    private const double MillisecondsPerSecond = 1000;

    public static WalkStatistics Calculate(WalkTrack track, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        var distance = 0.0;
        var movingMs = 0L;

        // Only inside each segment: the time and the distance of a pause are not part of the walk.
        foreach (var points in track.Points)
        {
            for (var i = 1; i < points.Count; i++)
            {
                var step = GeoMath.DistanceM(points[i - 1].Position, points[i].Position);
                var elapsedMs = points[i].OffsetMs - points[i - 1].OffsetMs;
                var speed = step / (elapsedMs / MillisecondsPerSecond);
                if (speed > ImpossibleSpeedMps)
                {
                    continue;
                }

                distance += step;
                if (speed >= MovingSpeedMps)
                {
                    movingMs += elapsedMs;
                }
            }
        }

        var movingSeconds = movingMs / MillisecondsPerSecond;
        var hasMoved = distance > 0 && movingSeconds > 0;
        return new WalkStatistics(
            (int)Math.Round(distance),
            (int)Math.Round(duration.TotalSeconds),
            (int)Math.Round(movingSeconds),
            hasMoved ? Math.Round(distance / movingSeconds, 2) : 0,
            hasMoved ? (int)Math.Round(movingSeconds / (distance / MetersPerKm)) : null);
    }
}
