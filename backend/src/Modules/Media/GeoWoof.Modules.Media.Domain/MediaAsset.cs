using GeoWoof.BuildingBlocks.Domain.Entities;

namespace GeoWoof.Modules.Media.Domain;

/// <summary>What an image is for. A module can only attach images uploaded for its own purpose.</summary>
public enum MediaPurpose
{
    UserAvatar = 0,
    PetAvatar = 1,
    WalkPhoto = 2,
    PostImage = 3,
    ChatImage = 4,
}

public enum MediaAssetStatus
{
    /// <summary>The upload link was issued; the bytes are not confirmed yet.</summary>
    Pending = 0,

    /// <summary>Uploaded; waiting for validation and for its variants.</summary>
    Processing = 1,

    Ready = 2,

    Rejected = 3,
}

public sealed record MediaAssetReadyDomainEvent(Guid AssetId, Guid OwnerId) : IDomainEvent;

public sealed record MediaAssetRejectedDomainEvent(Guid AssetId, Guid OwnerId, string Reason) : IDomainEvent;

/// <summary>
/// An image uploaded by a user. The original only exists until it has been processed, because it
/// may carry EXIF metadata such as GPS coordinates; only the clean variants are ever served.
/// </summary>
public sealed class MediaAsset : AggregateRoot
{
    /// <summary>Constructor for EF Core.</summary>
    private MediaAsset()
    {
        ContentType = string.Empty;
    }

    private MediaAsset(Guid id, Guid ownerId, MediaPurpose purpose, string contentType, long sizeBytes, DateTimeOffset now, DateTimeOffset uploadExpiresAt)
        : base(id)
    {
        OwnerId = ownerId;
        Purpose = purpose;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        CreatedAt = now;
        UploadExpiresAt = uploadExpiresAt;
        Status = MediaAssetStatus.Pending;
    }

    public Guid OwnerId { get; private set; }

    public MediaPurpose Purpose { get; private set; }

    public MediaAssetStatus Status { get; private set; }

    public string ContentType { get; private set; }

    public long SizeBytes { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    public string? RejectionCode { get; private set; }

    public bool IsAttached { get; private set; }

    /// <summary>When the asset stopped being used: orphans are cleaned up some time after this.</summary>
    public DateTimeOffset? ReleasedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UploadExpiresAt { get; private set; }

    /// <summary>Claims made by the background processor, including the current one.</summary>
    public int ProcessingAttempts { get; private set; }

    /// <summary>Until when a processor owns the asset; afterwards another one may retry it.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    /// <summary>False while the original may still be in storage (a failed deletion is retried by the cleanup).</summary>
    public bool OriginalRemoved { get; private set; }

    public string OriginalKey => $"originals/{Id:N}";

    public string ThumbKey => $"variants/{Id:N}/thumb.webp";

    public string MediumKey => $"variants/{Id:N}/medium.webp";

    public IReadOnlyList<string> AllKeys => [OriginalKey, ThumbKey, MediumKey];

    public static MediaAsset Request(Guid id, Guid ownerId, MediaPurpose purpose, string contentType, long sizeBytes, DateTimeOffset now, DateTimeOffset uploadExpiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        return new MediaAsset(id, ownerId, purpose, contentType, sizeBytes, now, uploadExpiresAt);
    }

    /// <summary>The bytes are in storage: the background processor can take over.</summary>
    public void ConfirmUpload()
    {
        if (Status == MediaAssetStatus.Pending)
        {
            Status = MediaAssetStatus.Processing;
        }
    }

    public void MarkReady(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (Status != MediaAssetStatus.Processing)
        {
            return;
        }

        Status = MediaAssetStatus.Ready;
        Width = width;
        Height = height;
        LeaseExpiresAt = null;
        Raise(new MediaAssetReadyDomainEvent(Id, OwnerId));
    }

    public void Reject(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Status is MediaAssetStatus.Ready or MediaAssetStatus.Rejected)
        {
            return;
        }

        Status = MediaAssetStatus.Rejected;
        RejectionCode = reason;
        LeaseExpiresAt = null;
        Raise(new MediaAssetRejectedDomainEvent(Id, OwnerId, reason));
    }

    public void MarkOriginalRemoved() => OriginalRemoved = true;

    public void Attach()
    {
        IsAttached = true;
        ReleasedAt = null;
    }

    public void Release(DateTimeOffset now)
    {
        IsAttached = false;
        ReleasedAt = now;
    }
}
