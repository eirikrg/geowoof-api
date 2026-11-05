using GeoWoof.Modules.Walks.Domain.Routes;

namespace GeoWoof.Modules.Walks.UnitTests;

public sealed class RouteTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void The_vectors_shared_with_the_app_are_encoded_and_decoded()
    {
        foreach (var (name, encoded, points) in WalkTestData.PolylineVectors())
        {
            Polyline.Encode(points).ShouldBe(encoded, customMessage: name);

            Polyline.TryDecode(encoded, out var decoded).ShouldBeTrue(name);
            decoded.Count.ShouldBe(points.Count, name);
            for (var i = 0; i < points.Count; i++)
            {
                decoded[i].Latitude.ShouldBe(points[i].Latitude, Tolerance, name);
                decoded[i].Longitude.ShouldBe(points[i].Longitude, Tolerance, name);
            }
        }
    }

    [Theory]
    [InlineData("_")] // It announces another chunk that never arrives.
    [InlineData("_p~iF")] // A latitude without its longitude.
    [InlineData("  ")] // Below the alphabet of the algorithm.
    [InlineData("~~~~~~~~~~~~~~~~~~~~")] // A value that never ends.
    public void Text_that_is_not_a_polyline_is_rejected(string encoded) =>
        Polyline.TryDecode(encoded, out _).ShouldBeFalse();

    [Fact]
    public void Impossible_coordinates_are_rejected()
    {
        Polyline.TryDecode(Polyline.Encode([new GeoPoint(90.5, 0)]), out _).ShouldBeFalse();
        Polyline.TryDecode(Polyline.Encode([new GeoPoint(0, 180.5)]), out _).ShouldBeFalse();
    }

    [Fact]
    public void The_distance_between_two_points_is_geodesic()
    {
        // One degree of latitude on the mean radius of the Earth.
        GeoMath.DistanceM(new GeoPoint(40, -3), new GeoPoint(41, -3)).ShouldBe(111_195, 1);

        // A degree of longitude is shorter away from the equator.
        GeoMath.DistanceM(new GeoPoint(40, -3), new GeoPoint(40, -2)).ShouldBe(85_179, 10);
        GeoMath.DistanceM(new GeoPoint(40, -3), new GeoPoint(40, -3)).ShouldBe(0);
    }

    [Fact]
    public void Simplifying_a_straight_line_keeps_only_its_ends()
    {
        var line = Enumerable.Range(0, 100).Select(i => new GeoPoint(40 + (i * 0.0001), -3.7)).ToList();

        RouteSimplifier.Simplify(line, 5).ShouldBe([line[0], line[^1]]);
    }

    [Fact]
    public void Simplifying_keeps_the_corners_and_removes_the_small_wiggles()
    {
        var corner = new GeoPoint(40.002, -3.7);
        var points = new List<GeoPoint>();
        for (var i = 0; i < 20; i++)
        {
            // Less than a metre to each side of the line.
            var wiggle = i % 2 == 0 ? 0.000005 : -0.000005;
            points.Add(new GeoPoint(40 + (i * 0.0001), -3.7 + wiggle));
        }

        points.Add(corner);
        points.Add(new GeoPoint(40.002, -3.69));

        RouteSimplifier.Simplify(points, 5).ShouldBe([points[0], corner, points[^1]]);
    }

    [Fact]
    public void There_is_nothing_to_simplify_with_two_points()
    {
        GeoPoint[] points = [new(40, -3), new(40, -3)];

        RouteSimplifier.Simplify(points, 5).ShouldBe(points);
    }
}
