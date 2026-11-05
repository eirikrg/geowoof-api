using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.LiveTracking.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.LiveTracking.Presentation;

public sealed record ViewportRequest(double North, double South, double East, double West);

public sealed record StartSharingRequest(Guid WalkId, IReadOnlyList<Guid>? PetIds);

public sealed record PositionRequest(double Latitude, double Longitude, double? AccuracyM, DateTimeOffset RecordedAt);

/// <summary>The live map (docs/realtime.md). A failure says its code, which is part of the contract, and nothing else.</summary>
[Authorize]
public sealed class LiveHub(
    ICommandHandler<SubscribeViewportCommand, ViewportDto> subscribe,
    ICommandHandler<UnsubscribeViewportCommand> unsubscribe,
    ICommandHandler<StartSharingCommand, StartSharingDto> startSharing,
    ICommandHandler<UpdatePositionCommand> updatePosition,
    ICommandHandler<StopSharingCommand> stopSharing,
    LiveConnections connections) : Hub
{
    public const string WalkerUpdated = nameof(WalkerUpdated);
    public const string WalkerLeft = nameof(WalkerLeft);

    private Guid UserId => Context.User!.GetUserId();

    private CancellationToken Token => Context.ConnectionAborted;

    public override async Task OnConnectedAsync()
    {
        if (!await connections.OpenAsync(UserId, Context.ConnectionId, Token))
        {
            await connections.CloseAsync(UserId, Context.ConnectionId, CancellationToken.None);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // A walker who loses the connection stays until their position expires: they may be back in a moment.
        await connections.CloseAsync(UserId, Context.ConnectionId, CancellationToken.None);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<ViewportDto> SubscribeViewport(ViewportRequest request) =>
        Unwrap(await subscribe.Handle(new SubscribeViewportCommand(UserId, Context.ConnectionId, request.North, request.South, request.East, request.West), Token));

    public async Task UnsubscribeViewport() =>
        Check(await unsubscribe.Handle(new UnsubscribeViewportCommand(UserId, Context.ConnectionId), Token));

    public async Task<StartSharingDto> StartSharing(StartSharingRequest request) =>
        Unwrap(await startSharing.Handle(new StartSharingCommand(UserId, request.WalkId, request.PetIds), Token));

    public async Task UpdatePosition(PositionRequest request) =>
        Check(await updatePosition.Handle(new UpdatePositionCommand(UserId, request.Latitude, request.Longitude, request.AccuracyM, request.RecordedAt), Token));

    public async Task StopSharing() =>
        Check(await stopSharing.Handle(new StopSharingCommand(UserId), Token));

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new HubException(result.Error.Code);

    private static void Check(Result result)
    {
        if (result.IsFailure)
        {
            throw new HubException(result.Error.Code);
        }
    }
}

/// <summary>Limits each method per connection and minute. A client that goes wrong must not take the map with it.</summary>
internal sealed class LiveRateLimitFilter(IOptions<LiveTrackingOptions> options, TimeProvider timeProvider) : IHubFilter
{
    private const string RateLimitExceeded = "rate_limit.exceeded";

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var name = invocationContext.HubMethodName;
        var limit = name switch
        {
            nameof(LiveHub.SubscribeViewport) => options.Value.SubscribePermitsPerMinute,
            nameof(LiveHub.UpdatePosition) => options.Value.PositionPermitsPerMinute,
            nameof(LiveHub.StartSharing) or nameof(LiveHub.StopSharing) => options.Value.SharingPermitsPerMinute,
            _ => int.MaxValue,
        };
        if (!TryCount(invocationContext.Context, name, limit))
        {
            throw new HubException(RateLimitExceeded);
        }

        return await next(invocationContext);
    }

    private bool TryCount(HubCallerContext context, string method, int limit)
    {
        var minute = timeProvider.GetUtcNow().ToUnixTimeSeconds() / 60;
        var key = $"rate:{method}";

        // The items of a connection are not shared: a connection handles its calls one at a time.
        var (window, count) = context.Items.TryGetValue(key, out var value) && value is (long storedWindow, int storedCount)
            ? (storedWindow, storedCount)
            : (minute, 0);
        count = window == minute ? count + 1 : 1;
        context.Items[key] = (minute, count);
        return count <= limit;
    }
}

/// <summary>Reaches the connections through the hub, also those of other instances, with the backplane.</summary>
internal sealed class HubLiveBroadcaster(IHubContext<LiveHub> hub) : ILiveBroadcaster
{
    private static string Group(string cell) => $"geo:{cell}";

    public Task AddToCellAsync(string connectionId, string cell, CancellationToken cancellationToken) =>
        hub.Groups.AddToGroupAsync(connectionId, Group(cell), cancellationToken);

    public Task RemoveFromCellAsync(string connectionId, string cell, CancellationToken cancellationToken) =>
        hub.Groups.RemoveFromGroupAsync(connectionId, Group(cell), cancellationToken);

    public Task WalkerUpdatedToCellAsync(string cell, IReadOnlyList<string> exceptConnections, LiveWalkerDto walker, CancellationToken cancellationToken) =>
        hub.Clients.GroupExcept(Group(cell), exceptConnections).SendAsync(LiveHub.WalkerUpdated, walker, cancellationToken);

    public Task WalkerUpdatedToAsync(IReadOnlyList<string> connections, LiveWalkerDto walker, CancellationToken cancellationToken) =>
        connections.Count == 0 ? Task.CompletedTask : hub.Clients.Clients(connections).SendAsync(LiveHub.WalkerUpdated, walker, cancellationToken);

    public Task WalkerLeftToCellAsync(string cell, IReadOnlyList<string> exceptConnections, Guid userId, CancellationToken cancellationToken) =>
        hub.Clients.GroupExcept(Group(cell), exceptConnections).SendAsync(LiveHub.WalkerLeft, new WalkerLeftDto(userId), cancellationToken);

    public Task WalkerLeftToAsync(IReadOnlyList<string> connections, Guid userId, CancellationToken cancellationToken) =>
        connections.Count == 0 ? Task.CompletedTask : hub.Clients.Clients(connections).SendAsync(LiveHub.WalkerLeft, new WalkerLeftDto(userId), cancellationToken);
}

public static class LiveTrackingPresentation
{
    public const string HubPath = "/hubs/live";

    public static ISignalRServerBuilder AddLiveTrackingHub(this ISignalRServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<ILiveBroadcaster, HubLiveBroadcaster>();
        builder.Services.AddSingleton<LiveRateLimitFilter>();
        builder.AddHubOptions<LiveHub>(options => options.AddFilter<LiveRateLimitFilter>());
        return builder;
    }

    public static IEndpointRouteBuilder MapLiveTrackingHub(this IEndpointRouteBuilder app)
    {
        // When the token expires the connection closes, and the app comes back with a new one.
        app.MapHub<LiveHub>(HubPath, (HttpConnectionDispatcherOptions options) => options.CloseOnAuthenticationExpiration = true);
        return app;
    }
}
