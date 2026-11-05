namespace GeoWoof.BuildingBlocks.Domain.Visibility;

/// <summary>Who is looking at something, compared with its owner.</summary>
public enum ViewerRelation
{
    Owner = 0,
    Friend = 1,
    Stranger = 2,

    /// <summary>One of them blocked the other, in either direction.</summary>
    Blocked = 3,
}

/// <summary>Who the owner of something chose to show it to.</summary>
public enum ContentVisibility
{
    Everyone = 0,
    Friends = 1,
    OnlyMe = 2,
}

/// <summary>The only place that decides who sees what (docs/api.md, phase 4). A block wins over everything else.</summary>
public static class VisibilityRules
{
    public static bool CanSee(ViewerRelation viewer, ContentVisibility visibility) => viewer switch
    {
        ViewerRelation.Owner => true,
        ViewerRelation.Friend => visibility is ContentVisibility.Everyone or ContentVisibility.Friends,
        ViewerRelation.Stranger => visibility is ContentVisibility.Everyone,
        _ => false,
    };

    /// <summary>The same rule for a query, which filters by the visibilities that this viewer can see.</summary>
    public static IReadOnlyList<ContentVisibility> VisibleTo(ViewerRelation viewer) =>
        Enum.GetValues<ContentVisibility>().Where(visibility => CanSee(viewer, visibility)).ToList();
}
