using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.LiveTracking.Domain;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.LiveTracking.Application;

public sealed record LivePetDto(Guid Id, string Name, MediaImage? Avatar);

public sealed record LiveWalkerDto(
    Guid UserId,
    string Username,
    string DisplayName,
    MediaImage? Avatar,
    IReadOnlyList<LivePetDto> Pets,
    double Latitude,
    double Longitude,
    DateTimeOffset UpdatedAt);

public sealed record WalkerLeftDto(Guid UserId);

public sealed record ViewportDto(bool TooWide, IReadOnlyList<LiveWalkerDto> Walkers);

public sealed record StartSharingDto(string Visibility);

public static class LiveTrackingApplication
{
    public const int MaxCells = 16;

    public static IServiceCollection AddLiveTrackingApplication(this IServiceCollection services)
    {
        services.AddApplicationHandlers(typeof(LiveTrackingApplication).Assembly);
        services.AddScoped<LiveAudience>();
        services.AddScoped<LiveConnections>();
        services.AddScoped<LiveSweep>();
        return services;
    }

    internal static LiveVisibility ToLiveVisibility(this LiveSharingSettings settings) =>
        Enum.TryParse<LiveVisibility>(settings.Visibility, out var visibility) ? visibility : LiveVisibility.FriendsOnly;
}

/// <summary>
/// Who receives what a walker publishes. <see cref="VisibilityRules"/> decides, and a position never goes to anyone
/// it does not allow: the messages to a whole cell leave out whoever has a block, and only go out for public walkers.
/// </summary>
internal sealed class LiveAudience(ILiveStore store, ILiveBroadcaster broadcaster, IRelationshipsApi relationships)
{
    public static LiveWalkerDto? ToDto(LiveWalkerState walker) =>
        walker is { Latitude: { } latitude, Longitude: { } longitude, UpdatedAt: { } updatedAt }
            ? new LiveWalkerDto(
                walker.UserId,
                walker.Username,
                walker.DisplayName,
                walker.Avatar,
                walker.Pets.Select(pet => new LivePetDto(pet.Id, pet.Name, pet.Avatar)).ToList(),
                latitude,
                longitude,
                updatedAt)
            : null;

    public async Task<bool> CanSeeAsync(Guid viewerId, LiveWalkerState walker, CancellationToken cancellationToken)
    {
        if (viewerId == walker.UserId)
        {
            return false;
        }

        var relationship = await relationships.GetRelationshipAsync(viewerId, walker.UserId, cancellationToken);
        return VisibilityRules.CanSeeLive(ToViewerRelation(relationship), walker.Visibility, walker.IsPrivate);
    }

    public async Task SendUpdatedAsync(LiveWalkerState walker, string cell, CancellationToken cancellationToken)
    {
        if (ToDto(walker) is not { } dto)
        {
            return;
        }

        if (IsForEveryoneInTheCell(walker))
        {
            await broadcaster.WalkerUpdatedToCellAsync(cell, await ExcludedFromCellAsync(walker, cancellationToken), dto, cancellationToken);
        }
        else
        {
            await broadcaster.WalkerUpdatedToAsync(await AllowedInCellAsync(walker, cell, cancellationToken), dto, cancellationToken);
        }
    }

    /// <summary>To those who could see this state of the walker, so that it does not say who has been near to anyone else.</summary>
    public async Task SendLeftAsync(LiveWalkerState walker, string cell, CancellationToken cancellationToken)
    {
        if (IsForEveryoneInTheCell(walker))
        {
            await broadcaster.WalkerLeftToCellAsync(cell, await ExcludedFromCellAsync(walker, cancellationToken), walker.UserId, cancellationToken);
        }
        else
        {
            await broadcaster.WalkerLeftToAsync(await AllowedInCellAsync(walker, cell, cancellationToken), walker.UserId, cancellationToken);
        }
    }

    private static bool IsForEveryoneInTheCell(LiveWalkerState walker) =>
        walker is { Visibility: LiveVisibility.Everyone, IsPrivate: false };

    private static ViewerRelation ToViewerRelation(Relationship relationship) => relationship switch
    {
        Relationship.Self => ViewerRelation.Owner,
        Relationship.Friend => ViewerRelation.Friend,
        Relationship.Follower => ViewerRelation.Follower,
        Relationship.Blocked => ViewerRelation.Blocked,
        _ => ViewerRelation.Stranger,
    };

    private async Task<IReadOnlyList<string>> ExcludedFromCellAsync(LiveWalkerState walker, CancellationToken cancellationToken)
    {
        var hidden = await relationships.GetHiddenUserIdsAsync(walker.UserId, cancellationToken);
        return await store.GetConnectionsAsync([.. hidden, walker.UserId], cancellationToken);
    }

    private async Task<IReadOnlyList<string>> AllowedInCellAsync(LiveWalkerState walker, string cell, CancellationToken cancellationToken)
    {
        var listeners = await store.GetCellListenersAsync(cell, cancellationToken);
        var allowed = new List<string>();
        foreach (var (connectionId, userId) in listeners)
        {
            if (await CanSeeAsync(userId, walker, cancellationToken))
            {
                allowed.Add(connectionId);
            }
        }

        return allowed;
    }
}

public sealed record SubscribeViewportCommand(Guid UserId, string ConnectionId, double North, double South, double East, double West) : ICommand<ViewportDto>;

internal sealed class SubscribeViewportCommandHandler(
    ILiveStore store,
    ILiveBroadcaster broadcaster,
    LiveAudience audience,
    IOptions<LiveTrackingOptions> options,
    TimeProvider timeProvider) : ICommandHandler<SubscribeViewportCommand, ViewportDto>
{
    public async Task<Result<ViewportDto>> Handle(SubscribeViewportCommand command, CancellationToken cancellationToken)
    {
        if (!IsValid(command))
        {
            return LiveErrors.InvalidViewport;
        }

        var cells = GeoHash.CellsCovering(command.North, command.South, command.East, command.West, LiveTrackingApplication.MaxCells);

        // First listen and then read: what is published in between arrives, instead of being lost.
        var previous = await store.ReplaceCellsAsync(command.UserId, command.ConnectionId, cells ?? [], cancellationToken);
        foreach (var cell in previous.Except(cells ?? [], StringComparer.Ordinal))
        {
            await broadcaster.RemoveFromCellAsync(command.ConnectionId, cell, cancellationToken);
        }

        foreach (var cell in (cells ?? []).Except(previous, StringComparer.Ordinal))
        {
            await broadcaster.AddToCellAsync(command.ConnectionId, cell, cancellationToken);
        }

        if (cells is null)
        {
            return new ViewportDto(TooWide: true, []);
        }

        var oldest = timeProvider.GetUtcNow() - options.Value.PositionLifetime;
        var found = await store.FindInAreaAsync(command.North, command.South, command.East, command.West, cancellationToken);
        var walkers = new List<LiveWalkerDto>();
        foreach (var walker in found.Where(walker => walker.UpdatedAt >= oldest))
        {
            if (await audience.CanSeeAsync(command.UserId, walker, cancellationToken) && LiveAudience.ToDto(walker) is { } dto)
            {
                walkers.Add(dto);
            }
        }

        return new ViewportDto(TooWide: false, walkers);
    }

    private static bool IsValid(SubscribeViewportCommand command) =>
        double.IsFinite(command.North) && double.IsFinite(command.South) && double.IsFinite(command.East) && double.IsFinite(command.West)
        && command.North is >= -90 and <= 90
        && command.South is >= -90 and <= 90
        && command.East is >= -180 and <= 180
        && command.West is >= -180 and <= 180
        && command.North >= command.South;
}

public sealed record UnsubscribeViewportCommand(Guid UserId, string ConnectionId) : ICommand;

internal sealed class UnsubscribeViewportCommandHandler(ILiveStore store, ILiveBroadcaster broadcaster) : ICommandHandler<UnsubscribeViewportCommand>
{
    public async Task<Result> Handle(UnsubscribeViewportCommand command, CancellationToken cancellationToken)
    {
        foreach (var cell in await store.ReplaceCellsAsync(command.UserId, command.ConnectionId, [], cancellationToken))
        {
            await broadcaster.RemoveFromCellAsync(command.ConnectionId, cell, cancellationToken);
        }

        return Result.Success();
    }
}

public sealed record StartSharingCommand(Guid UserId, Guid WalkId, IReadOnlyList<Guid>? PetIds) : ICommand<StartSharingDto>;

internal sealed class StartSharingCommandHandler(
    ILiveStore store,
    IIdentityApi identity,
    IPetsApi pets,
    IMediaApi media,
    TimeProvider timeProvider) : ICommandHandler<StartSharingCommand, StartSharingDto>
{
    private const int MaxPets = 5;

    public async Task<Result<StartSharingDto>> Handle(StartSharingCommand command, CancellationToken cancellationToken)
    {
        if (command.WalkId == Guid.Empty || command.PetIds is not { Count: > 0 and <= MaxPets } petIds || petIds.Distinct().Count() != petIds.Count)
        {
            return LiveErrors.InvalidRequest;
        }

        var settings = await identity.GetLiveSharingAsync(command.UserId, cancellationToken);
        var users = await identity.GetPublicUsersAsync([command.UserId], cancellationToken);
        if (settings is null || !users.TryGetValue(command.UserId, out var user) || settings.ToLiveVisibility() == LiveVisibility.Nobody)
        {
            return LiveErrors.SharingDisabled;
        }

        var owned = await pets.GetPetIdsAsync(command.UserId, cancellationToken);
        if (!petIds.All(owned.Contains))
        {
            return LiveErrors.PetNotOwned;
        }

        var found = await pets.GetPetsAsync(petIds, cancellationToken);
        var assetIds = found.Values
            .Where(pet => pet.AvatarAssetId is not null).Select(pet => pet.AvatarAssetId!.Value)
            .Concat(user.AvatarAssetId is { } avatar ? [avatar] : Array.Empty<Guid>())
            .ToList();
        var images = await media.GetImagesAsync(assetIds, cancellationToken);
        var walkerPets = petIds
            .Where(found.ContainsKey)
            .Select(petId => found[petId])
            .Select(pet => new LivePet(pet.Id, pet.Name, pet.AvatarAssetId is { } assetId ? images.GetValueOrDefault(assetId) : null))
            .ToList();

        // A connection that comes back says it again: the position it had stays.
        var previous = await store.GetWalkerAsync(command.UserId, cancellationToken);
        var visibility = settings.ToLiveVisibility();
        var walker = new LiveWalkerState(
            command.UserId,
            command.WalkId,
            user.Username,
            user.DisplayName,
            user.AvatarAssetId is { } avatarId ? images.GetValueOrDefault(avatarId) : null,
            walkerPets,
            visibility,
            settings.IsPrivate,
            timeProvider.GetUtcNow(),
            previous?.Cell,
            previous?.Latitude,
            previous?.Longitude,
            previous?.UpdatedAt);
        await store.SaveWalkerAsync(walker, cancellationToken);
        return new StartSharingDto(visibility.ToString());
    }
}

public sealed record UpdatePositionCommand(Guid UserId, double Latitude, double Longitude, double? AccuracyM, DateTimeOffset RecordedAt) : ICommand;

internal sealed class UpdatePositionCommandHandler(
    ILiveStore store,
    LiveAudience audience,
    IIdentityApi identity,
    IOptions<LiveTrackingOptions> options,
    TimeProvider timeProvider) : ICommandHandler<UpdatePositionCommand>
{
    public async Task<Result> Handle(UpdatePositionCommand command, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(command.Latitude) || !double.IsFinite(command.Longitude)
            || command.Latitude is < -90 or > 90 || command.Longitude is < -180 or > 180)
        {
            return LiveErrors.InvalidPosition;
        }

        if (await store.GetWalkerAsync(command.UserId, cancellationToken) is not { } walker)
        {
            return LiveErrors.NotSharing;
        }

        // Read each time: choosing Nobody has to count from the next position.
        var settings = await identity.GetLiveSharingAsync(command.UserId, cancellationToken);
        if (settings is null || settings.ToLiveVisibility() == LiveVisibility.Nobody)
        {
            await StopAsync(walker, cancellationToken);
            return LiveErrors.SharingDisabled;
        }

        var now = timeProvider.GetUtcNow();
        var isTooSoon = walker.UpdatedAt is { } updatedAt && now - updatedAt < options.Value.MinPositionInterval;
        if (isTooSoon || now - command.RecordedAt > options.Value.PositionLifetime)
        {
            return Result.Success();
        }

        var cell = GeoHash.Encode(command.Latitude, command.Longitude);
        var next = walker with
        {
            Visibility = settings.ToLiveVisibility(),
            IsPrivate = settings.IsPrivate,
            LastSeenAt = now,
            Cell = cell,
            Latitude = command.Latitude,
            Longitude = command.Longitude,
            UpdatedAt = now,
        };

        // Whoever could see this person in the place, or with the rules, that no longer apply has to forget them.
        var hasNewRules = next.Visibility != walker.Visibility || next.IsPrivate != walker.IsPrivate;
        if (walker.Cell is { } previous && (hasNewRules || !string.Equals(previous, cell, StringComparison.Ordinal)))
        {
            await audience.SendLeftAsync(walker, previous, cancellationToken);
        }

        await store.SaveWalkerAsync(next, cancellationToken);
        await audience.SendUpdatedAsync(next, cell, cancellationToken);
        return Result.Success();
    }

    private async Task StopAsync(LiveWalkerState walker, CancellationToken cancellationToken)
    {
        if (walker.Cell is { } cell)
        {
            await audience.SendLeftAsync(walker, cell, cancellationToken);
        }

        await store.RemoveWalkerAsync(walker.UserId, cancellationToken);
    }
}

public sealed record StopSharingCommand(Guid UserId) : ICommand;

internal sealed class StopSharingCommandHandler(ILiveStore store, LiveAudience audience) : ICommandHandler<StopSharingCommand>
{
    public async Task<Result> Handle(StopSharingCommand command, CancellationToken cancellationToken)
    {
        if (await store.GetWalkerAsync(command.UserId, cancellationToken) is not { } walker)
        {
            return Result.Success();
        }

        if (walker.Cell is { } cell)
        {
            await audience.SendLeftAsync(walker, cell, cancellationToken);
        }

        await store.RemoveWalkerAsync(command.UserId, cancellationToken);
        return Result.Success();
    }
}

/// <summary>The connections of the hub: how many each person has, and what is left to clean when one closes.</summary>
public sealed class LiveConnections(ILiveStore store, IOptions<LiveTrackingOptions> options)
{
    /// <returns>Whether it can stay open. By position and not by count: of two opened at once, only the one that goes over closes.</returns>
    public async Task<bool> OpenAsync(Guid userId, string connectionId, CancellationToken cancellationToken) =>
        await store.AddConnectionAsync(userId, connectionId, cancellationToken) <= options.Value.MaxConnectionsPerUser;

    public async Task CloseAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        await store.ReplaceCellsAsync(userId, connectionId, [], cancellationToken);
        await store.RemoveConnectionAsync(userId, connectionId, cancellationToken);
    }
}

/// <summary>Removes who has stopped sending. Reads already leave them out, so this is what tells the others.</summary>
public sealed class LiveSweep(ILiveStore store, ILiveBroadcaster broadcaster, IRelationshipsApi relationships, IOptions<LiveTrackingOptions> options, TimeProvider timeProvider)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!await store.TryTakeSweepLockAsync(options.Value.SweepInterval, cancellationToken))
        {
            return;
        }

        var audience = new LiveAudience(store, broadcaster, relationships);
        var oldest = timeProvider.GetUtcNow() - options.Value.PositionLifetime;
        foreach (var userId in await store.GetWalkersNotSeenSinceAsync(oldest, cancellationToken))
        {
            if (await store.GetWalkerAsync(userId, cancellationToken) is { Cell: { } cell } walker)
            {
                await audience.SendLeftAsync(walker, cell, cancellationToken);
            }

            await store.RemoveWalkerAsync(userId, cancellationToken);
        }
    }
}
