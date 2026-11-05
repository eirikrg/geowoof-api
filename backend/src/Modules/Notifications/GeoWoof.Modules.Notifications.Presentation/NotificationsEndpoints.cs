using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Notifications.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GeoWoof.Modules.Notifications.Presentation;

public sealed record DeviceRequest(string Token, string Platform);

public sealed record DeviceTokenRequest(string Token);

/// <summary>Minimal API endpoints of the Notifications module, mapped under <c>/api/v1</c>.</summary>
public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var notifications = api.MapGroup("/notifications").WithTags("Notifications");
        notifications.MapGet(string.Empty, GetNotificationsAsync).Produces<PagedResponse<NotificationDto>>().ProducesProblem(StatusCodes.Status400BadRequest);
        notifications.MapGet("/unread-count", GetUnreadAsync).Produces<CountDto>();
        notifications.MapPost("/read-all", MarkAllReadAsync).Produces(StatusCodes.Status204NoContent);
        notifications.MapPost("/{id:guid}/read", MarkReadAsync).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        var devices = api.MapGroup("/devices").WithTags("Notifications");
        devices.MapPut(string.Empty, RegisterDeviceAsync).Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();
        devices.MapDelete(string.Empty, UnregisterDeviceAsync).Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();
        return api;
    }

    private static async Task<IResult> GetNotificationsAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetNotificationsQuery, PagedResponse<NotificationDto>> handler,
        CancellationToken cancellationToken,
        [FromQuery] string? cursor = null,
        [FromQuery] int? limit = null) =>
        (await handler.Handle(new GetNotificationsQuery(user.GetUserId(), cursor, limit), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetUnreadAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetUnreadNotificationsQuery, CountDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetUnreadNotificationsQuery(user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> MarkReadAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<MarkNotificationReadCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new MarkNotificationReadCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult();

    private static async Task<IResult> MarkAllReadAsync(
        ClaimsPrincipal user,
        ICommandHandler<MarkAllNotificationsReadCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new MarkAllNotificationsReadCommand(user.GetUserId()), cancellationToken)).ToHttpResult();

    private static async Task<IResult> RegisterDeviceAsync(
        DeviceRequest request,
        ClaimsPrincipal user,
        ICommandHandler<RegisterDeviceCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new RegisterDeviceCommand(user.GetUserId(), request.Token, request.Platform), cancellationToken)).ToHttpResult();

    private static async Task<IResult> UnregisterDeviceAsync(
        [FromBody] DeviceTokenRequest request,
        ClaimsPrincipal user,
        ICommandHandler<UnregisterDeviceCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new UnregisterDeviceCommand(user.GetUserId(), request.Token), cancellationToken)).ToHttpResult();
}
