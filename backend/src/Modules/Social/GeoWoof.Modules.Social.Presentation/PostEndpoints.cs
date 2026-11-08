using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Social.Application.Posts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GeoWoof.Modules.Social.Presentation;

public sealed record PostBody(string? Text, string? Visibility, IReadOnlyList<Guid>? MediaAssetIds, IReadOnlyList<Guid>? PetIds);

/// <summary>With <paramref name="ParentId"/>, a reply to that comment.</summary>
public sealed record CommentBody(string? Text, Guid? ParentId);

/// <summary>Posts, what people do with them, and the lists that show them.</summary>
public static class PostEndpoints
{
    public static IEndpointRouteBuilder MapPostEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var posts = api.MapGroup("/posts/{id:guid}").WithTags("Posts");
        posts.MapPut(string.Empty, PutPostAsync)
            .RequireAuthorization(AuthorizationPolicies.VerifiedEmail)
            .RequireRateLimiting(RateLimitPolicies.SocialWrites)
            .Produces<PostDto>(StatusCodes.Status201Created)
            .Produces<PostDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        posts.MapGet(string.Empty, GetPostAsync).Produces<PostDto>().ProducesProblem(StatusCodes.Status404NotFound);
        posts.MapDelete(string.Empty, DeletePostAsync).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status403Forbidden);

        posts.MapPut("/likes", LikeAsync).Produces<PostLikeDto>().ProducesProblem(StatusCodes.Status404NotFound);
        posts.MapDelete("/likes", UnlikeAsync).Produces<PostLikeDto>().ProducesProblem(StatusCodes.Status404NotFound);

        posts.MapGet("/comments", GetCommentsAsync)
            .Produces<PagedResponse<CommentDto>>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        posts.MapGet("/comments/{commentId:guid}/replies", GetRepliesAsync)
            .Produces<PagedResponse<CommentDto>>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        posts.MapPost("/comments", AddCommentAsync)
            .RequireAuthorization(AuthorizationPolicies.VerifiedEmail)
            .RequireRateLimiting(RateLimitPolicies.SocialWrites)
            .Produces<CommentDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        posts.MapDelete("/comments/{commentId:guid}", DeleteCommentAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);

        posts.MapPut("/repost", RepostAsync)
            .RequireAuthorization(AuthorizationPolicies.VerifiedEmail)
            .RequireRateLimiting(RateLimitPolicies.SocialWrites)
            .Produces<PostRepostDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        posts.MapDelete("/repost", UndoRepostAsync).Produces<PostRepostDto>().ProducesProblem(StatusCodes.Status404NotFound);

        posts.MapPut("/save", SaveAsync).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
        posts.MapDelete("/save", UnsaveAsync).Produces(StatusCodes.Status204NoContent);

        api.MapGet("/me/saved-posts", GetSavedPostsAsync).WithTags("Posts").Produces<PagedResponse<PostDto>>().ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapGet("/feed", GetFeedAsync).WithTags("Posts").Produces<PagedResponse<TimelineItemDto>>().ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapGet("/explore", GetExploreAsync).WithTags("Posts").Produces<PagedResponse<PostDto>>().ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapGet("/users/{id:guid}/posts", GetUserPostsAsync).WithTags("Posts")
            .Produces<PagedResponse<TimelineItemDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> PutPostAsync(
        Guid id,
        PostBody body,
        ClaimsPrincipal user,
        ICommandHandler<PutPostCommand, PutPostResult> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new PutPostCommand(id, user.GetUserId(), body.Text, body.Visibility, body.MediaAssetIds, body.PetIds), cancellationToken))
            .ToHttpResult(result => result.Created ? TypedResults.Created($"/api/v1/posts/{id}", result.Post) : TypedResults.Ok(result.Post));

    private static async Task<IResult> GetPostAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetPostQuery, PostDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetPostQuery(id, user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> DeletePostAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<DeletePostCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new DeletePostCommand(id, user.GetUserId()), cancellationToken)).ToHttpResult();

    private static async Task<IResult> LikeAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<LikePostCommand, PostLikeDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new LikePostCommand(id, user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> UnlikeAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<UnlikePostCommand, PostLikeDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new UnlikePostCommand(id, user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetCommentsAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetCommentsQuery, PagedResponse<CommentDto>> handler,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetCommentsQuery(id, user.GetUserId(), limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetRepliesAsync(
        Guid id,
        Guid commentId,
        ClaimsPrincipal user,
        IQueryHandler<GetRepliesQuery, PagedResponse<CommentDto>> handler,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetRepliesQuery(id, commentId, user.GetUserId(), limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> AddCommentAsync(
        Guid id,
        CommentBody body,
        ClaimsPrincipal user,
        ICommandHandler<AddCommentCommand, CommentDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new AddCommentCommand(id, user.GetUserId(), body.Text, body.ParentId), cancellationToken))
            .ToHttpResult(comment => TypedResults.Created($"/api/v1/posts/{id}/comments/{comment.Id}", comment));

    private static async Task<IResult> DeleteCommentAsync(
        Guid id,
        Guid commentId,
        ClaimsPrincipal user,
        ICommandHandler<DeleteCommentCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new DeleteCommentCommand(id, commentId, user.GetUserId()), cancellationToken)).ToHttpResult();

    private static async Task<IResult> RepostAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<RepostCommand, PostRepostDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new RepostCommand(id, user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> UndoRepostAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<UndoRepostCommand, PostRepostDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new UndoRepostCommand(id, user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> SaveAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<SavePostCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new SavePostCommand(id, user.GetUserId()), cancellationToken)).ToHttpResult();

    private static async Task<IResult> UnsaveAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<UnsavePostCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new UnsavePostCommand(id, user.GetUserId()), cancellationToken)).ToHttpResult();

    private static async Task<IResult> GetSavedPostsAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetSavedPostsQuery, PagedResponse<PostDto>> handler,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetSavedPostsQuery(user.GetUserId(), limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetFeedAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetFeedQuery, PagedResponse<TimelineItemDto>> handler,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetFeedQuery(user.GetUserId(), limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetExploreAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetExploreQuery, PagedResponse<PostDto>> handler,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetExploreQuery(user.GetUserId(), limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetUserPostsAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetUserPostsQuery, PagedResponse<TimelineItemDto>> handler,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetUserPostsQuery(id, user.GetUserId(), limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);
}
