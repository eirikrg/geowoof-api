using GeoWoof.BuildingBlocks.Domain.Visibility;

namespace GeoWoof.BuildingBlocks.UnitTests.Domain;

public sealed class VisibilityRulesTests
{
    [Theory]
    [InlineData(ViewerRelation.Owner, ContentVisibility.Everyone, false, true)]
    [InlineData(ViewerRelation.Owner, ContentVisibility.Friends, false, true)]
    [InlineData(ViewerRelation.Owner, ContentVisibility.OnlyMe, false, true)]
    [InlineData(ViewerRelation.Owner, ContentVisibility.Everyone, true, true)]
    [InlineData(ViewerRelation.Owner, ContentVisibility.Friends, true, true)]
    [InlineData(ViewerRelation.Owner, ContentVisibility.OnlyMe, true, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Everyone, false, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Friends, false, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.OnlyMe, false, false)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Everyone, true, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Friends, true, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.OnlyMe, true, false)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.Everyone, false, true)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.Friends, false, false)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.OnlyMe, false, false)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.Everyone, true, true)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.Friends, true, false)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.OnlyMe, true, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Everyone, false, true)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Friends, false, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.OnlyMe, false, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Everyone, true, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Friends, true, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.OnlyMe, true, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Everyone, false, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Friends, false, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.OnlyMe, false, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Everyone, true, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Friends, true, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.OnlyMe, true, false)]
    public void Each_viewer_sees_what_the_owner_allowed(ViewerRelation viewer, ContentVisibility visibility, bool isPrivateProfile, bool expected)
    {
        VisibilityRules.CanSee(viewer, visibility, isPrivateProfile).ShouldBe(expected);
        VisibilityRules.VisibleTo(viewer, isPrivateProfile).Contains(visibility).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ViewerRelation.Owner, false, true)]
    [InlineData(ViewerRelation.Owner, true, true)]
    [InlineData(ViewerRelation.Friend, false, true)]
    [InlineData(ViewerRelation.Friend, true, true)]
    [InlineData(ViewerRelation.Follower, false, true)]
    [InlineData(ViewerRelation.Follower, true, true)]
    [InlineData(ViewerRelation.Stranger, false, true)]
    [InlineData(ViewerRelation.Stranger, true, false)]
    [InlineData(ViewerRelation.Blocked, false, false)]
    [InlineData(ViewerRelation.Blocked, true, false)]
    public void A_private_profile_only_shows_its_content_to_its_followers(ViewerRelation viewer, bool isPrivateProfile, bool expected)
    {
        VisibilityRules.CanSeeProfileContent(viewer, isPrivateProfile).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ViewerRelation.Owner, ContentVisibility.Everyone, false, false)]
    [InlineData(ViewerRelation.Owner, ContentVisibility.Friends, false, false)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Everyone, false, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Friends, false, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Everyone, true, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.Friends, true, true)]
    [InlineData(ViewerRelation.Friend, ContentVisibility.OnlyMe, false, false)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.Everyone, false, true)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.Friends, false, false)]
    [InlineData(ViewerRelation.Follower, ContentVisibility.Everyone, true, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Everyone, false, true)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Friends, false, false)]
    [InlineData(ViewerRelation.Stranger, ContentVisibility.Everyone, true, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Everyone, false, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Friends, false, false)]
    [InlineData(ViewerRelation.Blocked, ContentVisibility.Everyone, true, false)]
    public void Only_what_a_friend_or_a_public_profile_publishes_can_be_reposted(ViewerRelation viewer, ContentVisibility visibility, bool isPrivateProfile, bool expected)
    {
        VisibilityRules.CanRepost(viewer, visibility, isPrivateProfile).ShouldBe(expected);
    }

    [Fact]
    public void Every_combination_is_covered_by_the_tests_above()
    {
        // A new value must be added to the tables of expected results.
        Enum.GetValues<ViewerRelation>().Length.ShouldBe(5);
        Enum.GetValues<ContentVisibility>().Length.ShouldBe(3);
    }

    [Theory]
    [InlineData(LiveVisibility.Everyone, ViewerRelation.Friend, false, true)]
    [InlineData(LiveVisibility.Everyone, ViewerRelation.Follower, false, true)]
    [InlineData(LiveVisibility.Everyone, ViewerRelation.Stranger, false, true)]
    [InlineData(LiveVisibility.Everyone, ViewerRelation.Blocked, false, false)]
    [InlineData(LiveVisibility.Everyone, ViewerRelation.Friend, true, true)]
    [InlineData(LiveVisibility.Everyone, ViewerRelation.Follower, true, true)]
    [InlineData(LiveVisibility.Everyone, ViewerRelation.Stranger, true, false)]
    [InlineData(LiveVisibility.Everyone, ViewerRelation.Blocked, true, false)]
    [InlineData(LiveVisibility.FriendsOnly, ViewerRelation.Friend, false, true)]
    [InlineData(LiveVisibility.FriendsOnly, ViewerRelation.Follower, false, false)]
    [InlineData(LiveVisibility.FriendsOnly, ViewerRelation.Stranger, false, false)]
    [InlineData(LiveVisibility.FriendsOnly, ViewerRelation.Blocked, false, false)]
    [InlineData(LiveVisibility.FriendsOnly, ViewerRelation.Friend, true, true)]
    [InlineData(LiveVisibility.FriendsOnly, ViewerRelation.Follower, true, false)]
    [InlineData(LiveVisibility.FriendsOnly, ViewerRelation.Stranger, true, false)]
    [InlineData(LiveVisibility.Nobody, ViewerRelation.Friend, false, false)]
    [InlineData(LiveVisibility.Nobody, ViewerRelation.Follower, false, false)]
    [InlineData(LiveVisibility.Nobody, ViewerRelation.Stranger, false, false)]
    [InlineData(LiveVisibility.Nobody, ViewerRelation.Blocked, false, false)]
    [InlineData(LiveVisibility.Nobody, ViewerRelation.Friend, true, false)]
    public void Who_sees_someone_on_the_live_map(LiveVisibility visibility, ViewerRelation viewer, bool isPrivate, bool expected) =>
        VisibilityRules.CanSeeLive(viewer, visibility, isPrivate).ShouldBe(expected);
}
