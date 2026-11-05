using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Posts;

public sealed record PostMediaDto(string Type, Guid AssetId, string? ThumbUrl, string? MediumUrl);

public sealed record TaggedPetDto(Guid Id, string Name, MediaImage? Avatar);

public sealed record MentionDto(string Username, Guid UserId);

public sealed record PostDto(
    Guid Id,
    UserSummaryDto Author,
    string? Text,
    string Visibility,
    IReadOnlyList<PostMediaDto> Media,
    IReadOnlyList<TaggedPetDto> Pets,
    IReadOnlyList<MentionDto> Mentions,
    int LikeCount,
    int CommentCount,
    int RepostCount,
    bool LikedByMe,
    bool SavedByMe,
    bool RepostedByMe,
    bool CanRepost,
    DateTimeOffset CreatedAt);

public sealed record TimelineItemDto(Guid Id, PostDto Post, UserSummaryDto? RepostedBy, DateTimeOffset? RepostedAt);

public sealed record CommentDto(
    Guid Id,
    Guid PostId,
    UserSummaryDto Author,
    string Text,
    IReadOnlyList<MentionDto> Mentions,
    bool CanDelete,
    DateTimeOffset CreatedAt);

public sealed record PostLikeDto(int LikeCount, bool LikedByMe);

public sealed record PostRepostDto(int RepostCount, bool RepostedByMe);

internal static class ViewerRelations
{
    /// <param name="viewerId">Who is looking.</param>
    /// <param name="authorId">Whose content it is.</param>
    /// <param name="graph">What the viewer has decided about others.</param>
    /// <param name="authorFollowsViewer">The graph of the viewer does not say it, so it has to be asked.</param>
    public static ViewerRelation Of(Guid viewerId, Guid authorId, SocialGraph graph, bool authorFollowsViewer)
    {
        if (viewerId == authorId)
        {
            return ViewerRelation.Owner;
        }

        if (graph.IsBlockedEitherWay(authorId))
        {
            return ViewerRelation.Blocked;
        }

        if (!graph.Following.Contains(authorId))
        {
            return ViewerRelation.Stranger;
        }

        return authorFollowsViewer ? ViewerRelation.Friend : ViewerRelation.Follower;
    }
}

/// <summary>Builds the posts for one viewer, applying the rules again with the current data.</summary>
internal sealed class PostAssembler(
    IPostRepository posts,
    UserSummaries summaries,
    IIdentityApi identity,
    ISocialGraphCache graphs,
    IFollowRepository follows,
    IMediaApi media,
    IPetsApi pets)
{
    public async Task<PostDto?> FindAsync(Guid viewerId, Guid postId, CancellationToken cancellationToken) =>
        (await BuildAsync(viewerId, [postId], cancellationToken)).GetValueOrDefault(postId);

    /// <returns>Those that this viewer can see now. An account being deleted is left out.</returns>
    public async Task<IReadOnlyDictionary<Guid, PostDto>> BuildAsync(Guid viewerId, IReadOnlyCollection<Guid> postIds, CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
        {
            return new Dictionary<Guid, PostDto>();
        }

        var graph = await graphs.GetAsync(viewerId, cancellationToken);
        var hiddenIds = graph.Blocked.Union(graph.BlockedBy).ToList();
        var rows = await posts.GetWithDetailsAsync(postIds, cancellationToken);
        var authorIds = rows.Select(post => post.AuthorId).Distinct().ToList();
        var authors = await identity.GetPublicUsersAsync(authorIds, cancellationToken);
        var followingViewer = await follows.GetFollowersAmongAsync(viewerId, authorIds, cancellationToken);
        var people = await summaries.LoadAsync(viewerId, authorIds, cancellationToken);
        var stats = await posts.GetStatsAsync(postIds, viewerId, hiddenIds, cancellationToken);
        var tagged = await pets.GetPetsAsync(rows.SelectMany(post => post.Pets).Select(pet => pet.PetId).Distinct().ToList(), cancellationToken);
        var images = await LoadImagesAsync(rows, tagged, cancellationToken);

        var built = new Dictionary<Guid, PostDto>();
        foreach (var post in rows)
        {
            if (!authors.TryGetValue(post.AuthorId, out var author) || !people.TryGetValue(post.AuthorId, out var person))
            {
                continue;
            }

            // The query used a copy of the privacy of the author, which arrives by an event and can be behind.
            var relation = ViewerRelations.Of(viewerId, post.AuthorId, graph, followingViewer.Contains(post.AuthorId));
            if (VisibilityRules.CanSee(relation, post.Visibility, author.IsPrivate))
            {
                built[post.Id] = ToDto(post, person, relation, author.IsPrivate, stats.GetValueOrDefault(post.Id), tagged, images, hiddenIds);
            }
        }

        return built;
    }

    /// <summary>A page of the feed or of a profile. Whatever cannot be built is left out, keeping the position.</summary>
    public async Task<PagedResponse<TimelineItemDto>> TimelinePageAsync(Guid viewerId, IReadOnlyList<TimelineRow> rows, int limit, CancellationToken cancellationToken)
    {
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? SocialCursor.Encode(page[^1].At, page[^1].Id) : null;
        var built = await BuildAsync(viewerId, page.Select(row => row.PostId).Distinct().ToList(), cancellationToken);
        var actors = await summaries.LoadAsync(viewerId, page.Where(row => row.IsRepost).Select(row => row.ActorId).Distinct().ToList(), cancellationToken);

        var items = new List<TimelineItemDto>();
        foreach (var row in page)
        {
            if (!built.TryGetValue(row.PostId, out var post))
            {
                continue;
            }

            if (!row.IsRepost)
            {
                items.Add(new TimelineItemDto(row.Id, post, RepostedBy: null, RepostedAt: null));
            }
            else if (actors.TryGetValue(row.ActorId, out var actor))
            {
                items.Add(new TimelineItemDto(row.Id, post, actor, row.At));
            }
        }

        return new PagedResponse<TimelineItemDto>(items, next);
    }

    /// <summary>A page of a list of posts, such as explore or the saved ones.</summary>
    public async Task<PagedResponse<PostDto>> PostPageAsync(Guid viewerId, IReadOnlyList<PostPageRow> rows, int limit, CancellationToken cancellationToken)
    {
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? SocialCursor.Encode(page[^1].At, page[^1].Id) : null;
        var built = await BuildAsync(viewerId, page.Select(row => row.PostId).Distinct().ToList(), cancellationToken);
        var items = page.Where(row => built.ContainsKey(row.PostId)).Select(row => built[row.PostId]).ToList();
        return new PagedResponse<PostDto>(items, next);
    }

    private async Task<IReadOnlyDictionary<Guid, MediaImage>> LoadImagesAsync(IReadOnlyList<Post> rows, IReadOnlyDictionary<Guid, PetSummary> tagged, CancellationToken cancellationToken)
    {
        var assetIds = rows
            .SelectMany(post => post.Media)
            .Select(item => item.AssetId)
            .Concat(tagged.Values.Where(pet => pet.AvatarAssetId is not null).Select(pet => pet.AvatarAssetId!.Value))
            .Distinct()
            .ToList();
        return await media.GetImagesAsync(assetIds, cancellationToken);
    }

    private static PostDto ToDto(
        Post post,
        UserSummaryDto author,
        ViewerRelation relation,
        bool authorIsPrivate,
        PostStats? stats,
        IReadOnlyDictionary<Guid, PetSummary> tagged,
        IReadOnlyDictionary<Guid, MediaImage> images,
        IReadOnlyCollection<Guid> hiddenIds)
    {
        var mediaItems = post.Media
            .OrderBy(item => item.Position)
            .Select(item =>
            {
                var image = images.GetValueOrDefault(item.AssetId);
                return new PostMediaDto(item.Type.ToString(), item.AssetId, image?.ThumbUrl, image?.MediumUrl);
            })
            .ToList();
        var pets = post.Pets
            .Where(pet => tagged.ContainsKey(pet.PetId))
            .Select(pet =>
            {
                var summary = tagged[pet.PetId];
                return new TaggedPetDto(summary.Id, summary.Name, summary.AvatarAssetId is { } assetId ? images.GetValueOrDefault(assetId) : null);
            })
            .ToList();
        var mentions = post.Mentions
            .Where(mention => !hiddenIds.Contains(mention.UserId))
            .Select(mention => new MentionDto(mention.Username, mention.UserId))
            .ToList();
        return new PostDto(
            post.Id,
            author,
            post.Text,
            post.Visibility.ToString(),
            mediaItems,
            pets,
            mentions,
            stats?.Likes ?? 0,
            stats?.Comments ?? 0,
            stats?.Reposts ?? 0,
            stats?.LikedByMe ?? false,
            stats?.SavedByMe ?? false,
            stats?.RepostedByMe ?? false,
            VisibilityRules.CanRepost(relation, post.Visibility, authorIsPrivate),
            post.CreatedAt);
    }
}

/// <summary>Decides which <c>@usernames</c> of a text are mentions, for the post they are written on.</summary>
internal sealed class MentionResolver(IIdentityApi identity, ISocialGraphCache graphs)
{
    /// <returns>Those of accounts that exist, without a block with the writer, and that can see the post.</returns>
    public async Task<IReadOnlyList<Mention>> ResolveAsync(
        string? text,
        Guid writerId,
        Guid postAuthorId,
        ContentVisibility visibility,
        bool authorIsPrivate,
        CancellationToken cancellationToken)
    {
        var usernames = MentionParser.Parse(text);
        if (usernames.Count == 0)
        {
            return [];
        }

        var users = await identity.GetPublicUsersByUsernameAsync(usernames, cancellationToken);
        var writer = await graphs.GetAsync(writerId, cancellationToken);
        var authorGraph = await graphs.GetAsync(postAuthorId, cancellationToken);
        var mentions = new List<Mention>();
        foreach (var username in usernames)
        {
            if (!users.TryGetValue(username, out var user) || writer.IsBlockedEitherWay(user.Id))
            {
                continue;
            }

            var theirs = await graphs.GetAsync(user.Id, cancellationToken);
            var relation = ViewerRelations.Of(user.Id, postAuthorId, theirs, authorGraph.Following.Contains(user.Id));
            if (VisibilityRules.CanSee(relation, visibility, authorIsPrivate))
            {
                mentions.Add(new Mention(user.Id, username));
            }
        }

        return mentions;
    }
}
