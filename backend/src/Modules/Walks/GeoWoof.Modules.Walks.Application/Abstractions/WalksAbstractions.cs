using GeoWoof.Modules.Walks.Domain;

namespace GeoWoof.Modules.Walks.Application.Abstractions;

// Ports of the Walks application layer; the Infrastructure layer implements them.

/// <summary>What a list shows about a walk, without its heavy route.</summary>
public sealed record WalkSummaryRow(
    Guid Id,
    Guid OwnerId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string TimeZone,
    IReadOnlyList<Guid> PetIds,
    WalkVisibility Visibility,
    string? Note,
    int DistanceM,
    int DurationS,
    int MovingTimeS,
    double AverageSpeedMps,
    int? PaceSecondsPerKm,
    IReadOnlyList<string> PreviewPolylines,
    IReadOnlyList<Guid> PhotoAssetIds,
    DateTimeOffset UpdatedAt);

public sealed record WalkStatsRow(DateTimeOffset StartedAt, string TimeZone, int DistanceM, int DurationS, int MovingTimeS);

/// <summary>A position in a list of walks, from the most recent to the oldest.</summary>
public sealed record WalkPageCursor(DateTimeOffset StartedAt, Guid Id);

public interface IWalkRepository
{
    Task<Walk?> GetByIdAsync(Guid walkId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WalkSummaryRow>> GetPageAsync(Guid ownerId, Guid? petId, WalkPageCursor? after, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<WalkStatsRow>> GetStatsRowsAsync(Guid ownerId, Guid? petId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Walk>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Walk>> GetByPetAsync(Guid petId, CancellationToken cancellationToken);

    /// <summary>Also stores the geography of the route, which only exists for spatial queries.</summary>
    void Add(Walk walk, WalkTrack track);

    void Remove(Walk walk);
}

/// <summary>Saves the Walks module's changes in one transaction (with its Outbox).</summary>
public interface IWalksUnitOfWork
{
    /// <exception cref="BuildingBlocks.Application.Persistence.UniqueConstraintViolationException">The walk already exists.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets a failed save, so that the next read gets what is really stored.</summary>
    void DiscardChanges();
}
