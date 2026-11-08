using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using GeoWoof.Modules.Walks.Domain;
using GeoWoof.Modules.Walks.Domain.Routes;

namespace GeoWoof.Modules.Walks.UnitTests;

internal static class WalkTestData
{
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Metres of 0.0001 degrees of latitude.</summary>
    public const double MetersPerStep = 11.1195;

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>A segment going north, one step of about 11 m every <paramref name="stepMs"/>.</summary>
    public static WalkSegment North(int points, long firstOffsetMs = 0, long stepMs = 4000, double latitude = 40.4)
    {
        var positions = Enumerable.Range(0, points).Select(i => new GeoPoint(latitude + (i * 0.0001), -3.7)).ToList();
        var offsets = Enumerable.Range(0, points).Select(i => firstOffsetMs + (i * stepMs)).ToList();
        return new WalkSegment(Polyline.Encode(positions), offsets);
    }

    public static WalkSegment Segment(params (double Latitude, double Longitude, long OffsetMs)[] points) => new(
        Polyline.Encode(points.Select(point => new GeoPoint(point.Latitude, point.Longitude)).ToList()),
        points.Select(point => point.OffsetMs).ToList());

    public static WalkTrack Track(TimeSpan duration, params WalkSegment[] segments) => WalkTrack.Parse(segments, duration).Value;

    /// <summary>Reads a trace of the fixtures folder: each trkseg is a segment.</summary>
    public static (IReadOnlyList<WalkSegment> Segments, TimeSpan Duration) LoadGpx(string name)
    {
        XNamespace gpx = "http://www.topografix.com/GPX/1/1";
        var document = XDocument.Load(FixturePath(name));
        var segments = document.Descendants(gpx + "trkseg")
            .Select(segment => segment.Elements(gpx + "trkpt").Select(point => (
                Position: new GeoPoint(
                    double.Parse(point.Attribute("lat")!.Value, CultureInfo.InvariantCulture),
                    double.Parse(point.Attribute("lon")!.Value, CultureInfo.InvariantCulture)),
                Time: DateTimeOffset.Parse(point.Element(gpx + "time")!.Value, CultureInfo.InvariantCulture))).ToList())
            .ToList();
        var offsets = segments.Select(points => points.Select(point => (long)(point.Time - Start).TotalMilliseconds).ToList()).ToList();
        var result = segments
            .Select((points, index) => new WalkSegment(Polyline.Encode(points.Select(point => point.Position).ToList()), offsets[index]))
            .ToList();
        return (result, TimeSpan.FromMilliseconds(offsets[^1][^1]));
    }

    public static IEnumerable<(string Name, string Encoded, IReadOnlyList<GeoPoint> Points)> PolylineVectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("polyline-vectors.json")));
        return document.RootElement.EnumerateArray().Select(vector => (
            vector.GetProperty("name").GetString()!,
            vector.GetProperty("encoded").GetString()!,
            (IReadOnlyList<GeoPoint>)vector.GetProperty("points").EnumerateArray()
                .Select(point => new GeoPoint(point[0].GetDouble(), point[1].GetDouble()))
                .ToList())).ToList();
    }
}
