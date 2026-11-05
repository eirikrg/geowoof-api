using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Media.Contracts;

namespace GeoWoof.Modules.LiveTracking.Application;

public sealed class LiveTrackingOptions
{
    public const string SectionName = "LiveTracking";

    /// <summary>How long a position lasts without a new one.</summary>
    public TimeSpan PositionLifetime { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>What arrives sooner than this after the previous position is ignored.</summary>
    public TimeSpan MinPositionInterval { get; set; } = TimeSpan.FromSeconds(3);

    public int MaxConnectionsPerUser { get; set; } = 5;

    public int SubscribePermitsPerMinute { get; set; } = 60;

    public int PositionPermitsPerMinute { get; set; } = 60;

    public int SharingPermitsPerMinute { get; set; } = 30;

    /// <summary>In front of every key in Redis, so that the tests do not see each other.</summary>
    public string KeyPrefix { get; set; } = "live:";

    public LiveSimulatorOptions Simulator { get; set; } = new();
}

/// <summary>Sample walkers for local development. It never runs outside Development.</summary>
public sealed class LiveSimulatorOptions
{
    public bool Enabled { get; set; }

    /// <summary>The middle of where they walk: the Retiro, in Madrid.</summary>
    public double Latitude { get; set; } = 40.4153;

    public double Longitude { get; set; } = -3.6844;
}

public sealed record LivePet(Guid Id, string Name, MediaImage? Avatar);

/// <summary>Someone sharing their position, with what is shown of them: it is built once, when they start.</summary>
public sealed record LiveWalkerState(
    Guid UserId,
    Guid WalkId,
    string Username,
    string DisplayName,
    MediaImage? Avatar,
    IReadOnlyList<LivePet> Pets,
    LiveVisibility Visibility,
    bool IsPrivate,
    DateTimeOffset LastSeenAt,
    string? Cell = null,
    double? Latitude = null,
    double? Longitude = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>What is known of who is walking and who is looking. It lives in Redis, and nothing of it is kept.</summary>
public interface ILiveStore
{
    Task<LiveWalkerState?> GetWalkerAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveWalkerAsync(LiveWalkerState walker, CancellationToken cancellationToken);

    Task RemoveWalkerAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Those with a position in that area, however old it is.</summary>
    Task<IReadOnlyList<LiveWalkerState>> FindInAreaAsync(double north, double south, double east, double west, CancellationToken cancellationToken);

    /// <summary>Those not heard from since then.</summary>
    Task<IReadOnlyList<Guid>> GetWalkersNotSeenSinceAsync(DateTimeOffset moment, CancellationToken cancellationToken);

    /// <summary>Whether this instance is the one that sweeps now.</summary>
    Task<bool> TryTakeSweepLockAsync(TimeSpan duration, CancellationToken cancellationToken);

    /// <returns>Its position among the connections of that person, from the oldest and starting at 1.</returns>
    Task<long> AddConnectionAsync(Guid userId, string connectionId, CancellationToken cancellationToken);

    Task RemoveConnectionAsync(Guid userId, string connectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetConnectionsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Changes the cells that a connection listens to.</summary>
    /// <returns>The ones it listened to before.</returns>
    Task<IReadOnlyList<string>> ReplaceCellsAsync(Guid userId, string connectionId, IReadOnlyCollection<string> cells, CancellationToken cancellationToken);

    /// <summary>Who is listening to a cell, by connection.</summary>
    Task<IReadOnlyDictionary<string, Guid>> GetCellListenersAsync(string cell, CancellationToken cancellationToken);
}

/// <summary>Reaches the connections of the hub. The Presentation layer implements it.</summary>
public interface ILiveBroadcaster
{
    Task AddToCellAsync(string connectionId, string cell, CancellationToken cancellationToken);

    Task RemoveFromCellAsync(string connectionId, string cell, CancellationToken cancellationToken);

    Task WalkerUpdatedToCellAsync(string cell, IReadOnlyList<string> exceptConnections, LiveWalkerDto walker, CancellationToken cancellationToken);

    Task WalkerUpdatedToAsync(IReadOnlyList<string> connections, LiveWalkerDto walker, CancellationToken cancellationToken);

    Task WalkerLeftToCellAsync(string cell, IReadOnlyList<string> exceptConnections, Guid userId, CancellationToken cancellationToken);

    Task WalkerLeftToAsync(IReadOnlyList<string> connections, Guid userId, CancellationToken cancellationToken);
}
