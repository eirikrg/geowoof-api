using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Media.Application;
using GeoWoof.Modules.Media.Application.Uploads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GeoWoof.Modules.Media.Presentation;

public sealed record RequestUploadRequest(string ContentType, long SizeBytes, string Purpose);

/// <summary>Minimal API endpoints of the Media module, mapped under <c>/api/v1</c>.</summary>
public static class MediaEndpoints
{
    private const string UploadsPath = "/api/v1/media/uploads";

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var uploads = api.MapGroup("/media/uploads").WithTags("Media");
        uploads.MapPost(string.Empty, RequestUploadAsync)
            .Produces<MediaUploadDto>(StatusCodes.Status201Created).ProducesValidationProblem().ProducesProblem(StatusCodes.Status429TooManyRequests);
        uploads.MapPost("/{id:guid}/complete", CompleteUploadAsync)
            .Produces<MediaAssetDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        uploads.MapGet("/{id:guid}", GetAssetAsync)
            .Produces<MediaAssetDto>().ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> RequestUploadAsync(
        RequestUploadRequest request,
        ClaimsPrincipal user,
        ICommandHandler<RequestUploadCommand, MediaUploadDto> handler,
        CancellationToken cancellationToken)
    {
        var command = new RequestUploadCommand(user.GetUserId(), request.ContentType, request.SizeBytes, request.Purpose);
        return (await handler.Handle(command, cancellationToken))
            .ToHttpResult(upload => TypedResults.Created($"{UploadsPath}/{upload.AssetId}", upload));
    }

    private static async Task<IResult> CompleteUploadAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<CompleteUploadCommand, MediaAssetDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new CompleteUploadCommand(user.GetUserId(), id), cancellationToken))
            .ToHttpResult(asset => TypedResults.Accepted($"{UploadsPath}/{asset.Id}", asset));

    private static async Task<IResult> GetAssetAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetMediaAssetQuery, MediaAssetDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetMediaAssetQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);
}
