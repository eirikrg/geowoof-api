namespace GeoWoof.BuildingBlocks.Domain.Visibility;

/// <summary>Who is looking at something, compared with its owner.</summary>
public enum ViewerRelation
{
    Owner = 0,

    /// <summary>They follow each other.</summary>
    Friend = 1,

    /// <summary>The viewer follows the owner, who does not follow back.</summary>
    Follower = 2,

    Stranger = 3,

    /// <summary>One of them blocked the other, in either direction.</summary>
    Blocked = 4,
}

/// <summary>Who the owner of something chose to show it to.</summary>
public enum ContentVisibility
{
    Everyone = 0,
    Friends = 1,
    OnlyMe = 2,
}

/// <summary>Who can see someone on the live map while they walk.</summary>
public enum LiveVisibility
{
    Everyone = 0,
    FriendsOnly = 1,
    Nobody = 2,
}

/// <summary>The only place that decides who sees what. A block wins over everything else.</summary>
public static class VisibilityRules
{
    /// <summary>A private profile only shows its content to those who follow it.</summary>
    public static bool CanSeeProfileContent(ViewerRelation viewer, bool isPrivateProfile) => viewer switch
    {
        ViewerRelation.Blocked => false,
        ViewerRelation.Stranger => !isPrivateProfile,
        _ => true,
    };

    public static bool CanSee(ViewerRelation viewer, ContentVisibility visibility, bool isPrivateProfile) =>
        CanSeeProfileContent(viewer, isPrivateProfile) && viewer switch
        {
            ViewerRelation.Owner => true,
            ViewerRelation.Friend => visibility is ContentVisibility.Everyone or ContentVisibility.Friends,
            _ => visibility is ContentVisibility.Everyone,
        };

    /// <summary>
    /// A repost shows something to more people, so it has to be visible to whoever does it, and its author has
    /// to be a friend or have a public profile. Those who see the repost are still decided by <see cref="CanSee"/>.
    /// </summary>
    public static bool CanRepost(ViewerRelation viewer, ContentVisibility visibility, bool isPrivateProfile) =>
        viewer != ViewerRelation.Owner
        && CanSee(viewer, visibility, isPrivateProfile)
        && (viewer == ViewerRelation.Friend || !isPrivateProfile);

    /// <summary>
    /// Where someone is right now is the most delicate of all: a private profile limits it too, so that
    /// <see cref="LiveVisibility.Everyone"/> means its followers and never a stranger.
    /// </summary>
    public static bool CanSeeLive(ViewerRelation viewer, LiveVisibility visibility, bool isPrivateProfile) =>
        CanSeeProfileContent(viewer, isPrivateProfile) && visibility switch
        {
            LiveVisibility.Everyone => true,
            LiveVisibility.FriendsOnly => viewer is ViewerRelation.Owner or ViewerRelation.Friend,
            _ => viewer is ViewerRelation.Owner,
        };

    /// <summary>The same rule for a query, which filters by the visibilities that this viewer can see.</summary>
    public static IReadOnlyList<ContentVisibility> VisibleTo(ViewerRelation viewer, bool isPrivateProfile) =>
        Enum.GetValues<ContentVisibility>().Where(visibility => CanSee(viewer, visibility, isPrivateProfile)).ToList();
}
