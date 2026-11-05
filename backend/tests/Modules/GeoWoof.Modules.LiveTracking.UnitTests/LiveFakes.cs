using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.LiveTracking.Application;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Social.Contracts;

namespace GeoWoof.Modules.LiveTracking.UnitTests;

internal sealed class InMemoryLiveStore : ILiveStore
{
    private readonly Dictionary<Guid, LiveWalkerState> _walkers = [];
    private readonly Dictionary<Guid, List<string>> _connections = [];
    private readonly Dictionary<string, (Guid UserId, HashSet<string> Cells)> _cells = new(StringComparer.Ordinal);

    public bool SweepLockIsTaken { get; set; }

    public LiveWalkerState? Walker(Guid userId) => _walkers.GetValueOrDefault(userId);

    public Task<LiveWalkerState?> GetWalkerAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_walkers.GetValueOrDefault(userId));

    public Task SaveWalkerAsync(LiveWalkerState walker, CancellationToken cancellationToken)
    {
        _walkers[walker.UserId] = walker;
        return Task.CompletedTask;
    }

    public Task RemoveWalkerAsync(Guid userId, CancellationToken cancellationToken)
    {
        _walkers.Remove(userId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LiveWalkerState>> FindInAreaAsync(double north, double south, double east, double west, CancellationToken cancellationToken)
    {
        IReadOnlyList<LiveWalkerState> found = _walkers.Values
            .Where(walker => walker.Latitude >= south && walker.Latitude <= north && walker.Longitude >= west && walker.Longitude <= east)
            .ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Guid>> GetWalkersNotSeenSinceAsync(DateTimeOffset moment, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> found = _walkers.Values.Where(walker => walker.LastSeenAt < moment).Select(walker => walker.UserId).ToList();
        return Task.FromResult(found);
    }

    public Task<bool> TryTakeSweepLockAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.FromResult(!SweepLockIsTaken);

    public Task<long> AddConnectionAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(userId, out var connections))
        {
            connections = [];
            _connections[userId] = connections;
        }

        connections.Add(connectionId);
        return Task.FromResult((long)connections.Count);
    }

    public Task RemoveConnectionAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        _connections.GetValueOrDefault(userId)?.Remove(connectionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetConnectionsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> found = userIds.SelectMany(userId => _connections.GetValueOrDefault(userId) ?? []).ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<string>> ReplaceCellsAsync(Guid userId, string connectionId, IReadOnlyCollection<string> cells, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> previous = _cells.TryGetValue(connectionId, out var entry) ? entry.Cells.ToList() : [];
        _cells[connectionId] = (userId, cells.ToHashSet(StringComparer.Ordinal));
        return Task.FromResult(previous);
    }

    public Task<IReadOnlyDictionary<string, Guid>> GetCellListenersAsync(string cell, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, Guid> found = _cells
            .Where(entry => entry.Value.Cells.Contains(cell))
            .ToDictionary(entry => entry.Key, entry => entry.Value.UserId, StringComparer.Ordinal);
        return Task.FromResult(found);
    }
}

/// <summary>What was sent, in order, written so that a test reads like what happened.</summary>
internal sealed class RecordingBroadcaster : ILiveBroadcaster
{
    public List<string> Sent { get; } = [];

    public Dictionary<string, HashSet<string>> Groups { get; } = new(StringComparer.Ordinal);

    public Task AddToCellAsync(string connectionId, string cell, CancellationToken cancellationToken)
    {
        if (!Groups.TryGetValue(cell, out var group))
        {
            group = new HashSet<string>(StringComparer.Ordinal);
            Groups[cell] = group;
        }

        group.Add(connectionId);
        return Task.CompletedTask;
    }

    public Task RemoveFromCellAsync(string connectionId, string cell, CancellationToken cancellationToken)
    {
        Groups.GetValueOrDefault(cell)?.Remove(connectionId);
        return Task.CompletedTask;
    }

    public Task WalkerUpdatedToCellAsync(string cell, IReadOnlyList<string> exceptConnections, LiveWalkerDto walker, CancellationToken cancellationToken)
    {
        Sent.Add($"updated cell {cell} except [{string.Join(',', exceptConnections.Order(StringComparer.Ordinal))}]");
        return Task.CompletedTask;
    }

    public Task WalkerUpdatedToAsync(IReadOnlyList<string> connections, LiveWalkerDto walker, CancellationToken cancellationToken)
    {
        Sent.Add($"updated to [{string.Join(',', connections.Order(StringComparer.Ordinal))}]");
        return Task.CompletedTask;
    }

    public Task WalkerLeftToCellAsync(string cell, IReadOnlyList<string> exceptConnections, Guid userId, CancellationToken cancellationToken)
    {
        Sent.Add($"left cell {cell} except [{string.Join(',', exceptConnections.Order(StringComparer.Ordinal))}]");
        return Task.CompletedTask;
    }

    public Task WalkerLeftToAsync(IReadOnlyList<string> connections, Guid userId, CancellationToken cancellationToken)
    {
        Sent.Add($"left to [{string.Join(',', connections.Order(StringComparer.Ordinal))}]");
        return Task.CompletedTask;
    }
}

internal sealed class FakeRelationships : IRelationshipsApi
{
    private readonly Dictionary<(Guid Viewer, Guid Target), Relationship> _relationships = [];

    public void Set(Guid viewerId, Guid targetId, Relationship relationship) => _relationships[(viewerId, targetId)] = relationship;

    public Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken) =>
        Task.FromResult(viewerId == targetId ? Relationship.Self : _relationships.GetValueOrDefault((viewerId, targetId), Relationship.None));

    public Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> hidden = _relationships
            .Where(entry => entry.Value == Relationship.Blocked && entry.Key.Target == userId)
            .Select(entry => entry.Key.Viewer)
            .ToList();
        return Task.FromResult(hidden);
    }

    public Task<IReadOnlyCollection<Guid>> GetFollowingIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>([]);
}

internal sealed class FakeIdentity : IIdentityApi
{
    public Dictionary<Guid, LiveSharingSettings> Settings { get; } = [];

    public Dictionary<Guid, UserSummary> Users { get; } = [];

    public void Add(Guid userId, LiveVisibility? visibility, bool isPrivate = false, Guid? avatarAssetId = null)
    {
        Settings[userId] = new LiveSharingSettings(visibility?.ToString(), isPrivate);
        Users[userId] = new UserSummary(userId, "ana", "Ana", City: "Madrid", avatarAssetId, isPrivate);
    }

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<IReadOnlyDictionary<Guid, UserSummary>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, UserSummary> found = Users.Where(user => userIds.Contains(user.Key)).ToDictionary(user => user.Key, user => user.Value);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyDictionary<string, UserSummary>> GetPublicUsersByUsernameAsync(IReadOnlyCollection<string> usernames, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, UserSummary>>(new Dictionary<string, UserSummary>(StringComparer.Ordinal));

    public Task<LiveSharingSettings?> GetLiveSharingAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Settings.GetValueOrDefault(userId));
}

internal sealed class FakePets : IPetsApi
{
    public Dictionary<Guid, (Guid OwnerId, PetSummary Pet)> Pets { get; } = [];

    public Guid Add(Guid ownerId, string name = "Luna", Guid? avatarAssetId = null)
    {
        var id = Guid.CreateVersion7();
        Pets[id] = (ownerId, new PetSummary(id, name, avatarAssetId));
        return id;
    }

    public Task<Guid?> FindOwnerAsync(Guid petId, CancellationToken cancellationToken) =>
        Task.FromResult(Pets.TryGetValue(petId, out var pet) ? pet.OwnerId : (Guid?)null);

    public Task<IReadOnlyList<Guid>> GetPetIdsAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> found = Pets.Where(pet => pet.Value.OwnerId == ownerId).Select(pet => pet.Key).ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyDictionary<Guid, PetSummary>> GetPetsAsync(IReadOnlyCollection<Guid> petIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, PetSummary> found = Pets.Where(pet => petIds.Contains(pet.Key)).ToDictionary(pet => pet.Key, pet => pet.Value.Pet);
        return Task.FromResult(found);
    }
}

internal sealed class FakeMedia : IMediaApi
{
    public Task<MediaAttachFailure?> AttachAsync(Guid assetId, Guid ownerId, MediaUsage usage, CancellationToken cancellationToken) =>
        Task.FromResult<MediaAttachFailure?>(null);

    public Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<Guid, MediaImage>> GetImagesAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, MediaImage> found = assetIds.ToDictionary(id => id, id => new MediaImage(id, $"https://thumb/{id}", $"https://medium/{id}"));
        return Task.FromResult(found);
    }

    public Task<Guid> ImportAsync(Guid ownerId, MediaUsage usage, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.CreateVersion7());
}
