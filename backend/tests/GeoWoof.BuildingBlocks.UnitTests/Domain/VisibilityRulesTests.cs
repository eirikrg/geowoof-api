using GeoWoof.BuildingBlocks.Domain.Visibility;

namespace GeoWoof.BuildingBlocks.UnitTests.Domain;

public sealed class VisibilityRulesTests
{
    [Theory]
    [InlineData(ViewerRelation.Owner, ContentVisibility.Everyone, true)]
    [InlineData(ViewerRelation.Owner, ContentVisibility.Friends, true)]
    [InlineData(ViewerRelation.Owner, ContentVisibility.OnlyMe, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Everyone, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Friends, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.OnlyMe, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Everyone, true)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Friends, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.OnlyMe, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Everyone, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Friends, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.OnlyMe, false)]
    public void Each_viewer_sees_what_the_owner_allowed(ViewerRelation viewer, ContentVisibility visibility, bool expected)
    {
        VisibilityRules.CanSee(viewer, visibility).ShouldBe(expected);
        VisibilityRules.VisibleTo(viewer).Contains(visibility).ShouldBe(expected);
    }

    [Fact]
    public void Every_combination_is_covered_by_the_test_above()
    {
        // A new value must be added to the table of expected results.
        (Enum.GetValues<ViewerRelation>().Length * Enum.GetValues<ContentVisibility>().Length).ShouldBe(12);
    }
}
