using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Social.Application;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Application.Blocks;
using GeoWoof.Modules.Social.Application.Friends;
using GeoWoof.Modules.Social.Application.Reports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GeoWoof.Modules.Social.Presentation;

public sealed record FriendRequestBody(Guid UserId);

public sealed record ReportBody(string TargetType, Guid TargetId, string Reason, string? Details);

/// <summary>Minimal API endpoints of the Social module, mapped under <c>/api/v1</c>.</summary>
public static class SocialEndpoints
{
    public static IEndpointRouteBuilder MapSocialEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var requests = api.MapGroup("/friend-requests").WithTags("Friends");
        requests.MapPost(string.Empty, SendRequestAsync)
            .RequireAuthorization(AuthorizationPolicies.VerifiedEmail)
            .RequireRateLimiting(RateLimitPolicies.SocialWrites)
            .Produces<FriendRequestDto>().Produces<FriendRequestDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        requests.MapGet("/received", GetReceivedRequestsAsync).Produces<PagedResponse<FriendRequestDto>>();
        requests.MapGet("/sent", GetSentRequestsAsync).Produces<PagedResponse<FriendRequestDto>>();
        requests.MapPost("/{id:guid}/accept", AcceptRequestAsync)
            .Produces<FriendRequestDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        requests.MapPost("/{id:guid}/reject", RejectRequestAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        requests.MapDelete("/{id:guid}", CancelRequestAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        var friends = api.MapGroup("/friends").WithTags("Friends");
        friends.MapGet(string.Empty, GetFriendsAsync).Produces<PagedResponse<FriendDto>>();
        friends.MapDelete("/{userId:guid}", RemoveFriendAsync).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/users/{id:guid}/relationship", GetRelationshipAsync).WithTags("Friends")
            .Produces<RelationshipDto>().ProducesProblem(StatusCodes.Status404NotFound);

        var blocks = api.MapGroup("/blocks").WithTags("Blocks");
        blocks.MapGet(string.Empty, GetBlocksAsync).Produces<PagedResponse<BlockDto>>();
        blocks.MapPut("/{userId:guid}", BlockAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        blocks.MapDelete("/{userId:guid}", UnblockAsync).Produces(StatusCodes.Status204NoContent);

        api.MapPost("/reports", CreateReportAsync).WithTags("Reports")
            .RequireRateLimiting(RateLimitPolicies.SocialWrites)
            .Produces<ReportDto>().Produces<ReportDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<IResult> SendRequestAsync(
        FriendRequestBody body,
        ClaimsPrincipal user,
        ICommandHandler<SendFriendRequestCommand, SendFriendRequestResult> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new SendFriendRequestCommand(user.GetUserId(), body.UserId), cancellationToken))
            .ToHttpResult(result => result.Created ? TypedResults.Created($"/api/v1/friend-requests/{result.Request.Id}", result.Request) : TypedResults.Ok(result.Request));

    private static async Task<IResult> GetReceivedRequestsAsync(
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetFriendRequestsQuery, PagedResponse<FriendRequestDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetFriendRequestsQuery(user.GetUserId(), RequestDirection.Received, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetSentRequestsAsync(
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetFriendRequestsQuery, PagedResponse<FriendRequestDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetFriendRequestsQuery(user.GetUserId(), RequestDirection.Sent, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> AcceptRequestAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<AcceptFriendRequestCommand, FriendRequestDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new AcceptFriendRequestCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> RejectRequestAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<RejectFriendRequestCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new RejectFriendRequestCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult();

    private static async Task<IResult> CancelRequestAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<CancelFriendRequestCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new CancelFriendRequestCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult();

    private static async Task<IResult> GetFriendsAsync(
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetFriendsQuery, PagedResponse<FriendDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetFriendsQuery(user.GetUserId(), limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> RemoveFriendAsync(
        Guid userId,
        ClaimsPrincipal user,
        ICommandHandler<RemoveFriendCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new RemoveFriendCommand(user.GetUserId(), userId), cancellationToken)).ToHttpResult();

    private static async Task<IResult> GetRelationshipAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetRelationshipQuery, RelationshipDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetRelationshipQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetBlocksAsync(
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetBlocksQuery, PagedResponse<BlockDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetBlocksQuery(user.GetUserId(), limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> BlockAsync(
        Guid userId,
        ClaimsPrincipal user,
        ICommandHandler<BlockUserCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new BlockUserCommand(user.GetUserId(), userId), cancellationToken)).ToHttpResult();

    private static async Task<IResult> UnblockAsync(
        Guid userId,
        ClaimsPrincipal user,
        ICommandHandler<UnblockUserCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new UnblockUserCommand(user.GetUserId(), userId), cancellationToken)).ToHttpResult();

    private static async Task<IResult> CreateReportAsync(
        ReportBody body,
        ClaimsPrincipal user,
        ICommandHandler<CreateReportCommand, CreateReportResult> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new CreateReportCommand(user.GetUserId(), body.TargetType, body.TargetId, body.Reason, body.Details), cancellationToken))
            .ToHttpResult(result => result.Created ? TypedResults.Created($"/api/v1/reports/{result.Report.Id}", result.Report) : TypedResults.Ok(result.Report));
}
