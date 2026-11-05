using GeoWoof.Modules.LiveTracking.Domain;

namespace GeoWoof.Modules.LiveTracking.UnitTests;

public sealed class GeoHashTests
{
    [Theory]
    [InlineData(57.64911, 10.40744, "u4pru")]
    [InlineData(40.4153, -3.6844, "ezjmu")]
    [InlineData(-33.8688, 151.2093, "r3gx2")]
    public void Encodes_known_places(double latitude, double longitude, string expected) =>
        GeoHash.Encode(latitude, longitude).ShouldBe(expected);

    [Fact]
    public void A_small_area_takes_only_its_own_cell()
    {
        var cells = GeoHash.CellsCovering(40.4154, 40.4152, -3.6843, -3.6845, 16);

        cells.ShouldBe(["ezjmu"]);
    }

    [Fact]
    public void An_area_takes_every_cell_it_touches_and_no_other()
    {
        var cells = GeoHash.CellsCovering(40.46, 40.40, -3.64, -3.72, 16)!;

        cells.Count.ShouldBeGreaterThan(1);
        cells.ShouldBeUnique();
        foreach (var (latitude, longitude) in Enumerable.Range(0, 7).SelectMany(row => Enumerable.Range(0, 9).Select(column => (40.40 + (row * 0.01), -3.72 + (column * 0.01)))))
        {
            cells.ShouldContain(GeoHash.Encode(latitude, longitude));
        }

        cells.ShouldNotContain(GeoHash.Encode(40.60, -3.68));
    }

    [Fact]
    public void An_area_that_takes_too_many_cells_gives_none() =>
        GeoHash.CellsCovering(41, 40, -3, -4, 16).ShouldBeNull();

    [Fact]
    public void An_area_that_crosses_the_antimeridian_gives_none() =>
        GeoHash.CellsCovering(1, -1, -179, 179, 16).ShouldBeNull();

    [Fact]
    public void The_whole_world_gives_none_instead_of_overflowing() =>
        GeoHash.CellsCovering(90, -90, 180, -180, 16).ShouldBeNull();

    [Fact]
    public void The_corner_of_the_map_belongs_to_the_last_cell()
    {
        var cells = GeoHash.CellsCovering(90, 90, 180, 180, 16);

        cells.ShouldBe([GeoHash.Encode(90, 180)]);
    }
}
