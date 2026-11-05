using GeoWoof.BuildingBlocks.Application.Paging;

namespace GeoWoof.BuildingBlocks.UnitTests.Application;

public sealed class RelevanceCursorTests
{
    [Fact]
    public void A_position_survives_the_cursor()
    {
        var position = new RelevancePosition(0.8125m, Guid.CreateVersion7());

        RelevanceCursor.Decode(RelevanceCursor.Encode(position)).ShouldBe(position);
    }

    [Theory]
    [InlineData("!!")]
    [InlineData("bm90IGEgY3Vyc29y")]
    public void Anything_else_is_not_a_cursor(string cursor)
    {
        RelevanceCursor.Decode(cursor).ShouldBeNull();
    }

    [Fact]
    public void The_start_is_before_any_score()
    {
        RelevancePosition.Start.Score.ShouldBeGreaterThan(1);
    }
}
