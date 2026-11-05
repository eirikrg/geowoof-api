using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Posts;

public sealed record GetPostQuery(Guid PostId, Guid ViewerId) : IQuery<PostDto>;

internal sealed class GetPostQueryHandler(IPostRepository posts, PostAssembler assembler, ISocialGraphCache graphs) : IQueryHandler<GetPostQuery, PostDto>
{
    public async Task<Result<PostDto>> Handle(GetPostQuery query, CancellationToken cancellationToken)
    {
        var audience = PostAudience.For(query.ViewerId, await graphs.GetAsync(query.ViewerId, cancellationToken));
        if (await posts.GetVisibleAsync(query.PostId, audience, cancellationToken) is null)
        {
            return SocialErrors.PostNotFound;
        }

        var post = await assembler.FindAsync(query.ViewerId, query.PostId, cancellationToken);
        return post is null ? SocialErrors.PostNotFound : post;
    }
}

public sealed record GetFeedQuery(Guid ViewerId, int? Limit, string? Cursor) : IQuery<PagedResponse<TimelineItemDto>>;

internal sealed class GetFeedQueryHandler(IPostRepository posts, PostAssembler assembler, ISocialGraphCache graphs)
    : IQueryHandler<GetFeedQuery, PagedResponse<TimelineItemDto>>
{
    public async Task<Result<PagedResponse<TimelineItemDto>>> Handle(GetFeedQuery query, CancellationToken cancellationToken)
    {
        SocialPosition? after = null;
        if (query.Cursor is { } cursor)
        {
            after = SocialCursor.Decode(cursor);
            if (after is null)
            {
                return SocialErrors.InvalidCursor;
            }
        }

        var audience = PostAudience.For(query.ViewerId, await graphs.GetAsync(query.ViewerId, cancellationToken));
        var limit = PageLimit.Normalize(query.Limit);
        var rows = await posts.GetFeedPageAsync(audience, after, limit + 1, cancellationToken);
        return await assembler.TimelinePageAsync(query.ViewerId, rows, limit, cancellationToken);
    }
}

public sealed record GetUserPostsQuery(Guid UserId, Guid ViewerId, int? Limit, string? Cursor) : IQuery<PagedResponse<TimelineItemDto>>;

internal sealed class GetUserPostsQueryHandler(IPostRepository posts, PostAssembler assembler, IIdentityApi identity, ISocialGraphCache graphs)
    : IQueryHandler<GetUserPostsQuery, PagedResponse<TimelineItemDto>>
{
    public async Task<Result<PagedResponse<TimelineItemDto>>> Handle(GetUserPostsQuery query, CancellationToken cancellationToken)
    {
        SocialPosition? after = null;
        if (query.Cursor is { } cursor)
        {
            after = SocialCursor.Decode(cursor);
            if (after is null)
            {
                return SocialErrors.InvalidCursor;
            }
        }

        var graph = await graphs.GetAsync(query.ViewerId, cancellationToken);
        var users = await identity.GetPublicUsersAsync([query.UserId], cancellationToken);
        if (!users.TryGetValue(query.UserId, out var user))
        {
            return SocialErrors.UserNotFound;
        }

        // Whether they are also friends does not matter here: any follower can go into a private profile.
        var relation = ViewerRelations.Of(query.ViewerId, query.UserId, graph, authorFollowsViewer: false);
        if (relation == ViewerRelation.Blocked)
        {
            return SocialErrors.UserNotFound;
        }

        if (!VisibilityRules.CanSeeProfileContent(relation, user.IsPrivate))
        {
            return SocialErrors.ProfilePrivate;
        }

        var limit = PageLimit.Normalize(query.Limit);
        var rows = await posts.GetUserPageAsync(query.UserId, PostAudience.For(query.ViewerId, graph), after, limit + 1, cancellationToken);
        return await assembler.TimelinePageAsync(query.ViewerId, rows, limit, cancellationToken);
    }
}

public sealed record GetExploreQuery(Guid ViewerId, int? Limit, string? Cursor) : IQuery<PagedResponse<PostDto>>;

internal sealed class GetExploreQueryHandler(IPostRepository posts, PostAssembler assembler, ISocialGraphCache graphs)
    : IQueryHandler<GetExploreQuery, PagedResponse<PostDto>>
{
    public async Task<Result<PagedResponse<PostDto>>> Handle(GetExploreQuery query, CancellationToken cancellationToken)
    {
        SocialPosition? after = null;
        if (query.Cursor is { } cursor)
        {
            after = SocialCursor.Decode(cursor);
            if (after is null)
            {
                return SocialErrors.InvalidCursor;
            }
        }

        var audience = PostAudience.For(query.ViewerId, await graphs.GetAsync(query.ViewerId, cancellationToken));
        var limit = PageLimit.Normalize(query.Limit);
        var rows = await posts.GetExplorePageAsync(audience, after, limit + 1, cancellationToken);
        return await assembler.PostPageAsync(query.ViewerId, rows, limit, cancellationToken);
    }
}

public sealed record GetSavedPostsQuery(Guid ViewerId, int? Limit, string? Cursor) : IQuery<PagedResponse<PostDto>>;

internal sealed class GetSavedPostsQueryHandler(IPostRepository posts, PostAssembler assembler, ISocialGraphCache graphs)
    : IQueryHandler<GetSavedPostsQuery, PagedResponse<PostDto>>
{
    public async Task<Result<PagedResponse<PostDto>>> Handle(GetSavedPostsQuery query, CancellationToken cancellationToken)
    {
        SocialPosition? after = null;
        if (query.Cursor is { } cursor)
        {
            after = SocialCursor.Decode(cursor);
            if (after is null)
            {
                return SocialErrors.InvalidCursor;
            }
        }

        var audience = PostAudience.For(query.ViewerId, await graphs.GetAsync(query.ViewerId, cancellationToken));
        var limit = PageLimit.Normalize(query.Limit);
        var rows = await posts.GetSavedPageAsync(audience, after, limit + 1, cancellationToken);
        return await assembler.PostPageAsync(query.ViewerId, rows, limit, cancellationToken);
    }
}
