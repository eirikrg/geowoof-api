using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Social.Application;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Application.Blocks;
using GeoWoof.Modules.Social.Application.Follows;
using GeoWoof.Modules.Social.Application.Reports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GeoWoof.Modules.Social.Presentation;

public sealed record ReportBody(string TargetType, Guid TargetId, string Reason, string? Details);

/// <summary>Minimal API endpoints of the Social module, mapped under <c>/api/v1</c>.</summary>
public static class SocialEndpoints
{
    public static IEndpointRouteBuilder MapSocialEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var users = api.MapGroup("/users/{id:guid}").WithTags("Followers");
        users.MapPut("/follow", FollowAsync)
            .RequireAuthorization(AuthorizationPolicies.VerifiedEmail)
            .RequireRateLimiting(RateLimitPolicies.SocialWrites)
            .Produces<RelationshipDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        users.MapDelete("/follow", UnfollowAsync).Produces<RelationshipDto>().ProducesProblem(StatusCodes.Status404NotFound);
        users.MapDelete("/follower", RemoveFollowerAsync).Produces<RelationshipDto>().ProducesProblem(StatusCodes.Status404NotFound);
        users.MapGet("/relationship", GetRelationshipAsync).Produces<RelationshipDto>().ProducesProblem(StatusCodes.Status404NotFound);
        users.MapGet("/counts", GetCountsAsync).Produces<CountsDto>().ProducesProblem(StatusCodes.Status404NotFound);
        users.MapGet("/friends", GetFriendsAsync).Produces<PagedResponse<PersonDto>>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        users.MapGet("/followers", GetFollowersAsync).Produces<PagedResponse<PersonDto>>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        users.MapGet("/following", GetFollowingAsync).Produces<PagedResponse<PersonDto>>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);

        var requests = api.MapGroup("/follow-requests").WithTags("Followers");
        requests.MapGet("/received", GetReceivedRequestsAsync).Produces<PagedResponse<FollowRequestDto>>();
        requests.MapGet("/sent", GetSentRequestsAsync).Produces<PagedResponse<FollowRequestDto>>();
        requests.MapPost("/{id:guid}/accept", AcceptRequestAsync)
            .Produces<RelationshipDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        requests.MapPost("/{id:guid}/reject", RejectRequestAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        requests.MapDelete("/{id:guid}", CancelRequestAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        var blocks = api.MapGroup("/blocks").WithTags("Blocks");
        blocks.MapGet(string.Empty, GetBlocksAsync).Produces<PagedResponse<BlockDto>>();
        blocks.MapPut("/{userId:guid}", BlockAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        blocks.MapDelete("/{userId:guid}", UnblockAsync).Produces(StatusCodes.Status204NoContent);

        api.MapPostEndpoints();

        api.MapPost("/reports", CreateReportAsync).WithTags("Reports")
            .RequireRateLimiting(RateLimitPolicies.SocialWrites)
            .Produces<ReportDto>().Produces<ReportDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<IResult> FollowAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<FollowUserCommand, RelationshipDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new FollowUserCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> UnfollowAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<UnfollowUserCommand, RelationshipDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new UnfollowUserCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> RemoveFollowerAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<RemoveFollowerCommand, RelationshipDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new RemoveFollowerCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetRelationshipAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetRelationshipQuery, RelationshipDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetRelationshipQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetCountsAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetCountsQuery, CountsDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetCountsQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetFriendsAsync(
        Guid id,
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetPeopleQuery, PagedResponse<PersonDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetPeopleQuery(user.GetUserId(), id, PeopleList.Friends, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetFollowersAsync(
        Guid id,
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetPeopleQuery, PagedResponse<PersonDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetPeopleQuery(user.GetUserId(), id, PeopleList.Followers, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetFollowingAsync(
        Guid id,
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetPeopleQuery, PagedResponse<PersonDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetPeopleQuery(user.GetUserId(), id, PeopleList.Following, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetReceivedRequestsAsync(
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetFollowRequestsQuery, PagedResponse<FollowRequestDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetFollowRequestsQuery(user.GetUserId(), RequestDirection.Received, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetSentRequestsAsync(
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetFollowRequestsQuery, PagedResponse<FollowRequestDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetFollowRequestsQuery(user.GetUserId(), RequestDirection.Sent, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> AcceptRequestAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<AcceptFollowRequestCommand, RelationshipDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new AcceptFollowRequestCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> RejectRequestAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<RejectFollowRequestCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new RejectFollowRequestCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult();

    private static async Task<IResult> CancelRequestAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<CancelFollowRequestCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new CancelFollowRequestCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult();

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
