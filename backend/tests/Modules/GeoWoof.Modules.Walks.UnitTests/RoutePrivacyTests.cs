using GeoWoof.Modules.Walks.Domain;
using GeoWoof.Modules.Walks.Domain.Routes;

namespace GeoWoof.Modules.Walks.UnitTests;

public sealed class RoutePrivacyTests
{
    private static IReadOnlyList<GeoPoint> Decode(WalkSegment segment)
    {
        Polyline.TryDecode(segment.Polyline, out var points).ShouldBeTrue();
        return points;
    }

    /// <summary>Goes north for that many points, each about 11 m from the previous one.</summary>
    private static WalkSegment North(int points, long firstOffsetMs = 0) => WalkTestData.North(points, firstOffsetMs);

    [Fact]
    public void No_visible_point_is_near_the_start_or_the_end()
    {
        var original = North(91);
        var positions = Decode(original);
        var start = positions[0];
        var end = positions[positions.Count - 1];

        var route = RoutePrivacy.Hide([original]);

        var visible = route.Segments.SelectMany(Decode).ToList();
        visible.ShouldNotBeEmpty();
        visible.ShouldAllBe(point => GeoMath.DistanceM(point, start) >= RoutePrivacy.HiddenZoneM);
        visible.ShouldAllBe(point => GeoMath.DistanceM(point, end) >= RoutePrivacy.HiddenZoneM);
    }

    [Fact]
    public void What_remains_keeps_the_times_from_the_start_of_the_walk()
    {
        var original = North(91);

        var segment = RoutePrivacy.Hide([original]).Segments.Single();

        // About 18 steps of 11 m to leave the zone, and the offsets of the original points.
        segment.TimeOffsetsMs[0].ShouldBe(original.TimeOffsetsMs[18]);
        segment.TimeOffsetsMs.Count.ShouldBe(Decode(segment).Count);
        segment.TimeOffsetsMs.ShouldBeSubsetOf(original.TimeOffsetsMs);
    }

    [Fact]
    public void A_short_walk_around_the_start_has_no_visible_route()
    {
        var route = RoutePrivacy.Hide([North(15)]);

        route.Segments.ShouldBeEmpty();
        route.Previews.ShouldBeEmpty();
    }

    [Fact]
    public void Coming_back_near_the_start_in_the_middle_of_the_walk_splits_the_route()
    {
        // North for about 330 m, back past the door, and then far away to the south.
        var points = new List<GeoPoint>();
        for (var i = 0; i <= 30; i++)
        {
            points.Add(new GeoPoint(40.4 + (i * 0.0001), -3.7));
        }

        for (var i = 29; i >= -60; i--)
        {
            points.Add(new GeoPoint(40.4 + (i * 0.0001), -3.7));
        }

        var segment = new WalkSegment(Polyline.Encode(points), Enumerable.Range(0, points.Count).Select(i => i * 4000L).ToList());

        var route = RoutePrivacy.Hide([segment]);

        route.Segments.Count.ShouldBe(2);
        var door = points[0];
        route.Segments.SelectMany(Decode).ShouldAllBe(point => GeoMath.DistanceM(point, door) >= RoutePrivacy.HiddenZoneM);
    }

    [Fact]
    public void A_stretch_of_a_single_point_disappears()
    {
        // Only one point between the zones: it cannot be drawn.
        var route = RoutePrivacy.Hide([North(37)]);

        route.Segments.ShouldAllBe(segment => segment.TimeOffsetsMs.Count >= 2);
    }

    [Fact]
    public void Each_segment_stays_on_its_own_without_joining_them()
    {
        var first = North(40);
        var second = North(40, firstOffsetMs: 600_000).ToSegmentAt(40.45);

        var route = RoutePrivacy.Hide([first, second]);

        route.Segments.Count.ShouldBe(2);
        route.Previews.Count.ShouldBe(2);
    }

    [Fact]
    public void A_zone_of_nothing_keeps_the_whole_route()
    {
        var original = North(30);

        var route = RoutePrivacy.Hide([original], zoneM: 0);

        route.Segments.Single().ShouldBeEquivalentTo(original);
    }

    [Fact]
    public void A_route_that_cannot_be_read_shows_nothing()
    {
        var route = RoutePrivacy.Hide([new WalkSegment("not a polyline \u0001", [0, 1])]);

        route.Segments.ShouldBeEmpty();
        route.Previews.ShouldBeEmpty();
    }

    [Fact]
    public void A_route_without_points_shows_nothing()
    {
        RoutePrivacy.Hide([]).Segments.ShouldBeEmpty();
    }
}

internal static class SegmentTestExtensions
{
    /// <summary>The same segment moved to another latitude.</summary>
    public static WalkSegment ToSegmentAt(this WalkSegment segment, double latitude)
    {
        Polyline.TryDecode(segment.Polyline, out var points).ShouldBeTrue();
        var moved = points.Select(point => new GeoPoint(latitude + (point.Latitude - points[0].Latitude), point.Longitude)).ToList();
        return new WalkSegment(Polyline.Encode(moved), segment.TimeOffsetsMs);
    }
}
