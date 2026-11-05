using GeoWoof.Modules.Walks.Domain;
using GeoWoof.Modules.Walks.Domain.Routes;

namespace GeoWoof.Modules.Walks.UnitTests;

public sealed class WalkTrackTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);

    [Fact]
    public void A_route_with_two_segments_is_valid()
    {
        var track = WalkTrack.Parse([WalkTestData.North(3), WalkTestData.North(3, firstOffsetMs: 60_000)], Duration);

        track.IsSuccess.ShouldBeTrue();
        track.Value.Points.Count.ShouldBe(2);
        track.Value.Points[1][0].OffsetMs.ShouldBe(60_000);
    }

    [Fact]
    public void A_short_segment_is_accepted_when_another_one_has_a_line()
    {
        var track = WalkTrack.Parse([WalkTestData.North(1), WalkTestData.North(3, firstOffsetMs: 60_000)], Duration);

        track.IsSuccess.ShouldBeTrue();
        track.Value.Previews().Count.ShouldBe(1);
    }

    [Fact]
    public void Each_segment_has_its_own_simplified_preview()
    {
        var track = WalkTestData.Track(Duration, WalkTestData.North(50), WalkTestData.North(50, firstOffsetMs: 600_000));

        var previews = track.Previews();

        previews.Count.ShouldBe(2);
        foreach (var preview in previews)
        {
            Polyline.TryDecode(preview, out var points).ShouldBeTrue();
            points.Count.ShouldBe(2);
        }
    }

    public static TheoryData<string, WalkSegment[]> InvalidTracks => new()
    {
        { "no segments", [] },
        { "a single point", [WalkTestData.North(1)] },
        { "only short segments", [WalkTestData.North(1), WalkTestData.North(1, firstOffsetMs: 4000)] },
        { "an empty segment", [new WalkSegment(string.Empty, []), WalkTestData.North(2)] },
        { "not a polyline", [new WalkSegment("_", [0, 1])] },
        { "no polyline", [new WalkSegment(null!, [0, 1])] },
        { "no offsets", [new WalkSegment(WalkTestData.North(2).Polyline, null!)] },
        { "fewer offsets than points", [new WalkSegment(WalkTestData.North(3).Polyline, [0, 4000])] },
        { "more offsets than points", [new WalkSegment(WalkTestData.North(2).Polyline, [0, 4000, 8000])] },
        { "a negative offset", [new WalkSegment(WalkTestData.North(2).Polyline, [-1, 4000])] },
        { "the same offset twice", [new WalkSegment(WalkTestData.North(2).Polyline, [4000, 4000])] },
        { "offsets going back", [new WalkSegment(WalkTestData.North(2).Polyline, [4000, 0])] },
        { "a segment starting before the previous one ends", [WalkTestData.North(3), WalkTestData.North(3, firstOffsetMs: 4000)] },
        { "an offset after the end of the walk", [WalkTestData.North(2, firstOffsetMs: (long)Duration.TotalMilliseconds)] },
    };

    [Theory]
    [MemberData(nameof(InvalidTracks))]
    public void A_broken_route_is_rejected(string reason, WalkSegment[] segments) =>
        WalkTrack.Parse(segments, Duration).Error.ShouldBe(WalksErrors.InvalidTrack, reason);

    [Fact]
    public void A_route_can_have_a_limited_number_of_points()
    {
        var longWalk = TimeSpan.FromHours(12);

        WalkTrack.Parse([WalkTestData.North(WalkTrack.MaxPoints, stepMs: 1000, latitude: 0)], longWalk).IsSuccess.ShouldBeTrue();
        WalkTrack.Parse([WalkTestData.North(WalkTrack.MaxPoints + 1, stepMs: 1000, latitude: 0)], longWalk).Error.ShouldBe(WalksErrors.TooManyPoints);
    }
}

public sealed class WalkStatisticsTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromHours(1);

    [Fact]
    public void A_steady_walk_has_the_expected_numbers()
    {
        // Ten steps of 11.12 m in 8 s: 1.39 m/s.
        var track = WalkTestData.Track(Duration, WalkTestData.North(11, stepMs: 8000));

        var statistics = WalkStatistics.Calculate(track, TimeSpan.FromSeconds(100));

        statistics.DistanceM.ShouldBe(111);
        statistics.DurationS.ShouldBe(100);
        statistics.MovingTimeS.ShouldBe(80);
        statistics.AverageSpeedMps.ShouldBe(1.39);
        statistics.PaceSecondsPerKm.ShouldBe(719);
    }

    [Fact]
    public void A_pause_adds_neither_distance_nor_moving_time()
    {
        // During the pause the person moved far away.
        var track = WalkTestData.Track(Duration, WalkTestData.North(6, stepMs: 8000), WalkTestData.North(6, firstOffsetMs: 600_000, stepMs: 8000, latitude: 41));

        var statistics = WalkStatistics.Calculate(track, TimeSpan.FromMinutes(11));

        statistics.DistanceM.ShouldBe(111);
        statistics.MovingTimeS.ShouldBe(80);
        statistics.DurationS.ShouldBe(660);
    }

    [Fact]
    public void A_step_at_an_impossible_speed_is_a_gps_error()
    {
        var track = WalkTestData.Track(
            Duration,
            WalkTestData.Segment((40.4000, -3.7, 0), (40.4001, -3.7, 8000), (40.4101, -3.7, 12_000), (40.4102, -3.7, 20_000)));

        var statistics = WalkStatistics.Calculate(track, TimeSpan.FromSeconds(20));

        statistics.DistanceM.ShouldBe((int)Math.Round(2 * WalkTestData.MetersPerStep));
        statistics.MovingTimeS.ShouldBe(16);
    }

    [Fact]
    public void Time_standing_still_is_not_moving_time()
    {
        // Under 0.5 m/s: 11 m in 40 s.
        var track = WalkTestData.Track(Duration, WalkTestData.Segment((40.4000, -3.7, 0), (40.4001, -3.7, 40_000), (40.4002, -3.7, 44_000)));

        var statistics = WalkStatistics.Calculate(track, TimeSpan.FromSeconds(44));

        statistics.DistanceM.ShouldBe(22);
        statistics.MovingTimeS.ShouldBe(4);
    }

    [Fact]
    public void Without_movement_there_is_no_speed_or_pace()
    {
        var track = WalkTestData.Track(Duration, WalkTestData.Segment((40.4, -3.7, 0), (40.4, -3.7, 4000)));

        var statistics = WalkStatistics.Calculate(track, TimeSpan.FromSeconds(4));

        statistics.DistanceM.ShouldBe(0);
        statistics.MovingTimeS.ShouldBe(0);
        statistics.AverageSpeedMps.ShouldBe(0);
        statistics.PaceSecondsPerKm.ShouldBeNull();
    }

    [Fact]
    public void The_simulated_walk_with_a_pause_does_not_count_the_pause()
    {
        var (segments, duration) = WalkTestData.LoadGpx("pause_walk.gpx");
        var track = WalkTrack.Parse(segments, duration).Value;

        var statistics = WalkStatistics.Calculate(track, duration);

        // 119 + 119 steps of 5.6 m, plus what the noise of the trace adds.
        statistics.DistanceM.ShouldBeInRange(1330, 1800);
        statistics.DurationS.ShouldBeGreaterThan(statistics.MovingTimeS + 600);
    }

    [Fact]
    public void The_simulated_walk_with_jumps_does_not_count_the_jumps()
    {
        var (segments, duration) = WalkTestData.LoadGpx("tunnel_jumps.gpx");
        var track = WalkTrack.Parse(segments, duration).Value;

        var statistics = WalkStatistics.Calculate(track, duration);

        // 239 steps of 5.6 m; each jump alone would add more than 500 m.
        statistics.DistanceM.ShouldBeInRange(1330, 1900);
    }
}

public sealed class WeeklyStreakTests
{
    private const string Madrid = "Europe/Madrid";

    /// <summary>A Thursday: its ISO week starts on 2026-11-02.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static WalkMoment On(int month, int day, string zone = Madrid) => new(new DateTimeOffset(2026, month, day, 10, 0, 0, TimeSpan.Zero), zone);

    [Fact]
    public void Without_walks_there_is_no_streak() => WeeklyStreak.Calculate([], Now).ShouldBe(0);

    [Fact]
    public void Consecutive_weeks_add_up_and_several_walks_in_a_week_count_once() =>
        WeeklyStreak.Calculate([On(11, 3), On(11, 4), On(10, 28), On(10, 21)], Now).ShouldBe(3);

    [Fact]
    public void A_week_without_walks_breaks_the_streak() =>
        WeeklyStreak.Calculate([On(11, 3), On(10, 21), On(10, 14)], Now).ShouldBe(1);

    [Fact]
    public void The_current_week_does_not_break_the_streak_until_it_ends() =>
        WeeklyStreak.Calculate([On(10, 28), On(10, 21)], Now).ShouldBe(2);

    [Fact]
    public void A_whole_week_without_walks_ends_the_streak() =>
        WeeklyStreak.Calculate([On(10, 21), On(10, 14)], Now).ShouldBe(0);

    [Fact]
    public void A_walk_counts_in_the_week_of_the_place_where_it_happened()
    {
        // Sunday 23:30 UTC, already Monday in Madrid.
        var sunday = new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.Zero);
        WalkMoment twoWeeksBefore = On(10, 21);

        // In Madrid it is this week, so the walk of the week before last is not consecutive.
        WeeklyStreak.Calculate([new WalkMoment(sunday, Madrid), twoWeeksBefore], Now).ShouldBe(1);

        // In UTC it is last week, which is consecutive with that walk.
        WeeklyStreak.Calculate([new WalkMoment(sunday, "UTC"), twoWeeksBefore with { TimeZone = "UTC" }], Now).ShouldBe(2);
    }

    [Fact]
    public void A_streak_crosses_the_end_of_the_year()
    {
        var now = new DateTimeOffset(2027, 1, 6, 10, 0, 0, TimeSpan.Zero);
        WalkMoment[] walks = [
            new(new DateTimeOffset(2027, 1, 5, 10, 0, 0, TimeSpan.Zero), Madrid),
            new(new DateTimeOffset(2026, 12, 30, 10, 0, 0, TimeSpan.Zero), Madrid),
            new(new DateTimeOffset(2026, 12, 22, 10, 0, 0, TimeSpan.Zero), Madrid),
        ];

        WeeklyStreak.Calculate(walks, now).ShouldBe(3);
    }
}
