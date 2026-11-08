using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Domain.Visibility;

namespace GeoWoof.Modules.Walks.Domain;

public sealed record WalkRecordedDomainEvent(Guid WalkId, Guid OwnerId, IReadOnlyList<Guid> PetIds, int DistanceM, DateTimeOffset StartedAt) : IDomainEvent;

public sealed record WalkDeletedDomainEvent(Guid WalkId, Guid OwnerId) : IDomainEvent;

public sealed record WalkPet(Guid PetId);

public sealed class WalkPhoto
{
    internal WalkPhoto(Guid assetId, int position)
    {
        AssetId = assetId;
        Position = position;
    }

    public Guid AssetId { get; private set; }

    public int Position { get; internal set; }
}

/// <summary>What can change after a walk has been saved.</summary>
public sealed record WalkDetails(ContentVisibility Visibility, string? Note, IReadOnlyList<Guid> PhotoAssetIds);

/// <summary>
/// A finished walk. Its id is generated on the phone, so that retrying an upload never duplicates it.
/// The route, dates and pets are immutable; only the note, the visibility and the photos can change.
/// </summary>
public sealed class Walk : AggregateRoot
{
    public const int NoteMaxLength = 1000;
    public const int MaxPets = 10;
    public const int MaxPhotos = 10;

    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);

    /// <summary>Margin for a phone whose clock is slightly ahead.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    private readonly List<WalkPet> _pets = [];
    private readonly List<WalkPhoto> _photos = [];

    /// <summary>Constructor for EF Core.</summary>
    private Walk()
    {
        TimeZone = string.Empty;
        Segments = [];
        PreviewPolylines = [];
    }

    private Walk(Guid id, Guid ownerId, DateTimeOffset startedAt, DateTimeOffset endedAt, string timeZone, WalkTrack track, DateTimeOffset now)
        : base(id)
    {
        OwnerId = ownerId;
        StartedAt = startedAt;
        EndedAt = endedAt;
        TimeZone = timeZone;
        Segments = track.Segments;
        PreviewPolylines = track.Previews();
        CreatedAt = now;

        var statistics = WalkStatistics.Calculate(track, endedAt - startedAt);
        DistanceM = statistics.DistanceM;
        DurationS = statistics.DurationS;
        MovingTimeS = statistics.MovingTimeS;
        AverageSpeedMps = statistics.AverageSpeedMps;
        PaceSecondsPerKm = statistics.PaceSecondsPerKm;
        StatsVersion = WalkStatistics.AlgorithmVersion;
    }

    public Guid OwnerId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset EndedAt { get; private set; }

    public string TimeZone { get; private set; }

    public ContentVisibility Visibility { get; private set; }

    public string? Note { get; private set; }

    public int DistanceM { get; private set; }

    public int DurationS { get; private set; }

    public int MovingTimeS { get; private set; }

    public double AverageSpeedMps { get; private set; }

    public int? PaceSecondsPerKm { get; private set; }

    public int StatsVersion { get; private set; }

    /// <summary>The route as it arrived: the source to rebuild everything else.</summary>
    public IReadOnlyList<WalkSegment> Segments { get; private set; }

    public IReadOnlyList<string> PreviewPolylines { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<WalkPet> Pets => _pets.AsReadOnly();

    public IReadOnlyList<Guid> PhotoAssetIds => _photos.OrderBy(photo => photo.Position).Select(photo => photo.AssetId).ToList();

    public static Walk Record(
        Guid id,
        Guid ownerId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string timeZone,
        IReadOnlyCollection<Guid> petIds,
        WalkDetails details,
        WalkTrack track,
        DateTimeOffset updatedAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(petIds);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(track);
        var walk = new Walk(id, ownerId, startedAt, endedAt, timeZone, track, now);
        walk._pets.AddRange(petIds.Select(petId => new WalkPet(petId)));
        walk.Apply(details, updatedAt);
        walk.Raise(new WalkRecordedDomainEvent(id, ownerId, petIds.ToList(), walk.DistanceM, startedAt));
        return walk;
    }

    /// <summary>
    /// Whether a repeated upload is the same walk, and so it can only change what is editable.
    /// It must include every pet of the walk; the caller decides about the extra ones.
    /// </summary>
    public bool IsSameRecording(DateTimeOffset startedAt, DateTimeOffset endedAt, IReadOnlyList<WalkSegment> segments, IReadOnlyCollection<Guid> petIds)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(petIds);

        // A pet deleted after the walk was saved is no longer listed here, but the phone may still send it.
        return StartedAt == startedAt
            && EndedAt == endedAt
            && Segments.Count == segments.Count
            && Segments.Zip(segments).All(pair =>
                string.Equals(pair.First.Polyline, pair.Second.Polyline, StringComparison.Ordinal)
                && pair.Second.TimeOffsetsMs is not null
                && pair.First.TimeOffsetsMs.SequenceEqual(pair.Second.TimeOffsetsMs))
            && _pets.TrueForAll(pet => petIds.Contains(pet.PetId));
    }

    public bool IsVisibleTo(ViewerRelation viewer, bool isPrivateProfile) => VisibilityRules.CanSee(viewer, Visibility, isPrivateProfile);

    /// <summary>The last edit wins: an older one, delivered late, must be ignored.</summary>
    public bool IsStale(DateTimeOffset updatedAt) => updatedAt < UpdatedAt;

    public void Update(WalkDetails details, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(details);
        Apply(details, updatedAt);
    }

    public void RemovePet(Guid petId) => _pets.RemoveAll(pet => pet.PetId == petId);

    /// <summary>Marks the walk for deletion; the repository removes it in the same unit of work.</summary>
    public void Delete() => Raise(new WalkDeletedDomainEvent(Id, OwnerId));

    private void Apply(WalkDetails details, DateTimeOffset updatedAt)
    {
        Visibility = details.Visibility;
        Note = string.IsNullOrWhiteSpace(details.Note) ? null : details.Note.Trim();
        ApplyPhotos(details.PhotoAssetIds);
        UpdatedAt = updatedAt;
    }

    /// <summary>Keeps the existing rows: a photo that stays is only moved, never deleted and inserted again.</summary>
    private void ApplyPhotos(IReadOnlyList<Guid> assetIds)
    {
        _photos.RemoveAll(photo => !assetIds.Contains(photo.AssetId));
        for (var position = 0; position < assetIds.Count; position++)
        {
            var assetId = assetIds[position];
            var photo = _photos.Find(existing => existing.AssetId == assetId);
            if (photo is null)
            {
                _photos.Add(new WalkPhoto(assetId, position));
            }
            else
            {
                photo.Position = position;
            }
        }
    }
}
