using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Abstractions;

/// <summary>
/// What a query needs to apply <see cref="VisibilityRules"/> to posts of many authors: who is looking,
/// who is hidden from them and what each kind of viewer can see. The rules stay in one place.
/// </summary>
public sealed record PostAudience(
    Guid ViewerId,
    IReadOnlyList<Guid> HiddenIds,
    IReadOnlyList<ContentVisibility> ToFriend,
    IReadOnlyList<ContentVisibility> ToFollower,
    IReadOnlyList<ContentVisibility> ToStrangerOfPublic,
    IReadOnlyList<ContentVisibility> ToStrangerOfPrivate)
{
    public static PostAudience For(Guid viewerId, SocialGraph graph) => new(
        viewerId,
        graph.Blocked.Union(graph.BlockedBy).ToList(),
        VisibilityRules.VisibleTo(ViewerRelation.Friend, isPrivateProfile: false),
        VisibilityRules.VisibleTo(ViewerRelation.Follower, isPrivateProfile: false),
        VisibilityRules.VisibleTo(ViewerRelation.Stranger, isPrivateProfile: false),
        VisibilityRules.VisibleTo(ViewerRelation.Stranger, isPrivateProfile: true));
}

/// <summary>A post or a repost in a list. They have different ids, so a repost is the one whose id is not the post's.</summary>
public sealed record TimelineRow(Guid Id, Guid PostId, Guid ActorId, DateTimeOffset At)
{
    public bool IsRepost => Id != PostId;
}

/// <summary>A position in a list that is not ordered by the post itself, such as the saved ones.</summary>
public sealed record PostPageRow(Guid PostId, DateTimeOffset At, Guid Id);

public sealed record PostStats(int Likes, int Comments, int Reposts, bool LikedByMe, bool SavedByMe, bool RepostedByMe);

public interface IPostRepository
{
    /// <summary>With what it contains, to change it.</summary>
    Task<Post?> GetAsync(Guid postId, CancellationToken cancellationToken);

    /// <returns><c>null</c> if it does not exist or this viewer cannot see it, which are the same to them.</returns>
    Task<Post?> GetVisibleAsync(Guid postId, PostAudience audience, CancellationToken cancellationToken);

    /// <summary>With what they contain, to show them. The order is not defined.</summary>
    Task<IReadOnlyList<Post>> GetWithDetailsAsync(IReadOnlyCollection<Guid> postIds, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, PostStats>> GetStatsAsync(IReadOnlyCollection<Guid> postIds, Guid viewerId, IReadOnlyCollection<Guid> hiddenIds, CancellationToken cancellationToken);

    /// <summary>What the viewer and those they follow published or reposted, most recent first.</summary>
    Task<IReadOnlyList<TimelineRow>> GetFeedPageAsync(PostAudience audience, SocialPosition? after, int take, CancellationToken cancellationToken);

    /// <summary>What this person published or reposted, most recent first.</summary>
    Task<IReadOnlyList<TimelineRow>> GetUserPageAsync(Guid userId, PostAudience audience, SocialPosition? after, int take, CancellationToken cancellationToken);

    /// <summary>What strangers can see of public profiles, without the viewer's, most recent first.</summary>
    Task<IReadOnlyList<PostPageRow>> GetExplorePageAsync(PostAudience audience, SocialPosition? after, int take, CancellationToken cancellationToken);

    /// <summary>What the viewer saved and can still see, the most recently saved first.</summary>
    Task<IReadOnlyList<PostPageRow>> GetSavedPageAsync(PostAudience audience, SocialPosition? after, int take, CancellationToken cancellationToken);

    /// <summary>Keeps the copy up to date. It writes right away.</summary>
    Task SetAuthorPrivacyAsync(Guid authorId, bool isPrivate, CancellationToken cancellationToken);

    /// <summary>A deleted pet stops appearing in the posts, which stay. It writes right away.</summary>
    Task RemovePetAsync(Guid petId, CancellationToken cancellationToken);

    /// <summary>Everything a deleted account published or did on posts. It writes right away.</summary>
    Task RemoveContentOfUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(Post post);

    void Remove(Post post);
}

/// <summary>What people do with a post: likes, reposts and saving it.</summary>
public interface IPostActionRepository
{
    Task<PostLike?> GetLikeAsync(Guid postId, Guid userId, CancellationToken cancellationToken);

    Task<Repost?> GetRepostAsync(Guid postId, Guid userId, CancellationToken cancellationToken);

    Task<SavedPost?> GetSavedAsync(Guid postId, Guid userId, CancellationToken cancellationToken);

    /// <summary>The reposts that one of these two people made of a post by the other.</summary>
    Task<IReadOnlyList<Repost>> GetRepostsBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken);

    void Add(PostLike like);

    void Remove(PostLike like);

    void Add(Repost repost);

    void Remove(Repost repost);

    void Add(SavedPost saved);

    void Remove(SavedPost saved);
}

/// <summary>How many replies of a comment the viewer can see, and the first of them, the oldest first.</summary>
public sealed record CommentThread(int ReplyCount, IReadOnlyList<Comment> FirstReplies);

public interface ICommentRepository
{
    Task<Comment?> GetAsync(Guid commentId, CancellationToken cancellationToken);

    /// <summary>
    /// The comments of the post itself, the oldest first, without those of the people hidden from the viewer, nor the
    /// deleted ones without a reply that the viewer can see.
    /// </summary>
    Task<IReadOnlyList<Comment>> GetPageAsync(Guid postId, IReadOnlyCollection<Guid> hiddenIds, SocialPosition? after, int take, CancellationToken cancellationToken);

    /// <summary>The replies of a comment, the oldest first, without those of the people hidden from the viewer.</summary>
    Task<IReadOnlyList<Comment>> GetRepliesPageAsync(Guid parentId, IReadOnlyCollection<Guid> hiddenIds, SocialPosition? after, int take, CancellationToken cancellationToken);

    /// <summary>The threads of those comments that have replies that the viewer can see, with the <paramref name="first"/> first ones.</summary>
    Task<IReadOnlyDictionary<Guid, CommentThread>> GetThreadsAsync(IReadOnlyCollection<Guid> parentIds, IReadOnlyCollection<Guid> hiddenIds, int first, CancellationToken cancellationToken);

    /// <summary>Whether anyone has replied to it, apart from the reply <paramref name="except"/>.</summary>
    Task<bool> HasRepliesAsync(Guid commentId, Guid? except, CancellationToken cancellationToken);

    void Add(Comment comment);

    void Remove(Comment comment);
}
