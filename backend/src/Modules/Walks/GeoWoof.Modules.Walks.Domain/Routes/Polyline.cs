using System.Text;

namespace GeoWoof.Modules.Walks.Domain.Routes;

/// <summary>
/// Google's encoded polyline algorithm with precision 5 (about 1.1 m). The Android app has its own
/// implementation: both are tested with the same vectors, in polyline-vectors.json.
/// </summary>
public static class Polyline
{
    private const double Factor = 1e5;
    private const int AsciiOffset = 63;
    private const int ChunkBits = 5;
    private const int ContinuationBit = 0x20;
    private const int ChunkMask = 0x1F;
    private const int MaxShift = 30;

    public static string Encode(IReadOnlyList<GeoPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var result = new StringBuilder();
        var previousLat = 0L;
        var previousLon = 0L;
        foreach (var point in points)
        {
            var lat = (long)Math.Round(point.Latitude * Factor, MidpointRounding.AwayFromZero);
            var lon = (long)Math.Round(point.Longitude * Factor, MidpointRounding.AwayFromZero);
            EncodeValue(lat - previousLat, result);
            EncodeValue(lon - previousLon, result);
            previousLat = lat;
            previousLon = lon;
        }

        return result.ToString();
    }

    /// <summary>False when the text is not a valid polyline or holds impossible coordinates.</summary>
    public static bool TryDecode(string encoded, out IReadOnlyList<GeoPoint> points)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        var decoded = new List<GeoPoint>(encoded.Length / 4);
        points = decoded;
        var index = 0;
        var lat = 0L;
        var lon = 0L;
        while (index < encoded.Length)
        {
            if (!TryReadValue(encoded, ref index, out var deltaLat) || !TryReadValue(encoded, ref index, out var deltaLon))
            {
                return false;
            }

            lat += deltaLat;
            lon += deltaLon;
            var point = new GeoPoint(lat / Factor, lon / Factor);
            if (!point.IsValid)
            {
                return false;
            }

            decoded.Add(point);
        }

        return true;
    }

    private static void EncodeValue(long value, StringBuilder output)
    {
        var remaining = value < 0 ? ~(value << 1) : value << 1;
        while (remaining >= ContinuationBit)
        {
            output.Append((char)((ContinuationBit | (remaining & ChunkMask)) + AsciiOffset));
            remaining >>= ChunkBits;
        }

        output.Append((char)(remaining + AsciiOffset));
    }

    private static bool TryReadValue(string encoded, ref int index, out long value)
    {
        value = 0;
        long result = 0;
        var shift = 0;
        while (true)
        {
            if (index >= encoded.Length || shift > MaxShift)
            {
                return false;
            }

            var chunk = encoded[index++] - AsciiOffset;
            if (chunk is < 0 or > ContinuationBit + ChunkMask)
            {
                return false;
            }

            result |= (long)(chunk & ChunkMask) << shift;
            shift += ChunkBits;
            if (chunk < ContinuationBit)
            {
                break;
            }
        }

        value = (result & 1) != 0 ? ~(result >> 1) : result >> 1;
        return true;
    }
}

/// <summary>Douglas-Peucker simplification, for the previews of lists and thumbnails.</summary>
public static class RouteSimplifier
{
    private const double MetersPerDegree = 111_320;

    public static IReadOnlyList<GeoPoint> Simplify(IReadOnlyList<GeoPoint> points, double toleranceM)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count <= 2)
        {
            return points;
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        var lonScale = Math.Cos(double.DegreesToRadians(points[0].Latitude));

        // A stack instead of recursion: a walk can have 20 000 points.
        var ranges = new Stack<(int Start, int End)>();
        ranges.Push((0, points.Count - 1));
        while (ranges.TryPop(out var range))
        {
            var farthest = -1;
            var maxDistance = toleranceM;
            for (var i = range.Start + 1; i < range.End; i++)
            {
                var distance = DistanceToSegmentM(points[i], points[range.Start], points[range.End], lonScale);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    farthest = i;
                }
            }

            if (farthest >= 0)
            {
                keep[farthest] = true;
                ranges.Push((range.Start, farthest));
                ranges.Push((farthest, range.End));
            }
        }

        return points.Where((_, index) => keep[index]).ToList();
    }

    private static double DistanceToSegmentM(GeoPoint point, GeoPoint start, GeoPoint end, double lonScale)
    {
        var endX = (end.Longitude - start.Longitude) * lonScale * MetersPerDegree;
        var endY = (end.Latitude - start.Latitude) * MetersPerDegree;
        var x = (point.Longitude - start.Longitude) * lonScale * MetersPerDegree;
        var y = (point.Latitude - start.Latitude) * MetersPerDegree;

        var lengthSquared = (endX * endX) + (endY * endY);
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((x * endX) + (y * endY)) / lengthSquared, 0, 1);
        var deltaX = x - (t * endX);
        var deltaY = y - (t * endY);
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }
}
