using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Walks.Application;
using GeoWoof.Modules.Walks.Application.Walks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GeoWoof.Modules.Walks.Presentation;

public sealed record WalkSegmentRequest(string Polyline, IReadOnlyList<long> TimeOffsetsMs);

public sealed record WalkClientStatsRequest(int DistanceM, int MovingTimeS);

public sealed record WalkRequest(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string TimeZone,
    IReadOnlyList<Guid> PetIds,
    string Visibility,
    string? Note,
    IReadOnlyList<Guid>? PhotoAssetIds,
    IReadOnlyList<WalkSegmentRequest> Segments,
    WalkClientStatsRequest? ClientStats,
    DateTimeOffset UpdatedAt)
{
    internal WalkInput ToInput() => new(
        StartedAt,
        EndedAt,
        TimeZone,
        PetIds,
        Visibility,
        Note,
        PhotoAssetIds,
        Segments?.Select(segment => new WalkSegmentDto(segment?.Polyline!, segment?.TimeOffsetsMs!)).ToList()!,
        ClientStats is null ? null : new WalkClientStats(ClientStats.DistanceM, ClientStats.MovingTimeS),
        UpdatedAt);
}

/// <summary>Minimal API endpoints of the Walks module, mapped under <c>/api/v1</c>.</summary>
public static class WalksEndpoints
{
    public static IEndpointRouteBuilder MapWalksEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var walks = api.MapGroup(string.Empty).WithTags("Walks");
        walks.MapPut("/walks/{id:guid}", PutWalkAsync)
            .Produces<WalkDto>().Produces<WalkDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        walks.MapGet("/walks/{id:guid}", GetWalkAsync).Produces<WalkDto>().ProducesProblem(StatusCodes.Status404NotFound);
        walks.MapDelete("/walks/{id:guid}", DeleteWalkAsync).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
        walks.MapGet("/me/walks", GetMyWalksAsync).Produces<PagedResponse<WalkSummaryDto>>();
        walks.MapGet("/me/walk-stats", GetMyStatsAsync).Produces<WalkStatsDto>();
        walks.MapGet("/pets/{id:guid}/walks", GetPetWalksAsync)
            .Produces<PagedResponse<WalkSummaryDto>>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        walks.MapGet("/pets/{id:guid}/walk-stats", GetPetStatsAsync)
            .Produces<WalkStatsDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> PutWalkAsync(
        Guid id,
        WalkRequest request,
        ClaimsPrincipal user,
        ICommandHandler<PutWalkCommand, PutWalkResult> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new PutWalkCommand(user.GetUserId(), id, request.ToInput()), cancellationToken))
            .ToHttpResult(result => result.Created ? TypedResults.Created($"/api/v1/walks/{id}", result.Walk) : TypedResults.Ok(result.Walk));

    private static async Task<IResult> GetWalkAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetWalkQuery, WalkDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetWalkQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> DeleteWalkAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<DeleteWalkCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new DeleteWalkCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult();

    private static async Task<IResult> GetMyWalksAsync(
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetWalksQuery, PagedResponse<WalkSummaryDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetWalksQuery(user.GetUserId(), null, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetPetWalksAsync(
        Guid id,
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetWalksQuery, PagedResponse<WalkSummaryDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetWalksQuery(user.GetUserId(), id, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetMyStatsAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetWalkStatsQuery, WalkStatsDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetWalkStatsQuery(user.GetUserId(), null), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetPetStatsAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetWalkStatsQuery, WalkStatsDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetWalkStatsQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);
}
