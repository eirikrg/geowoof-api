using GeoWoof.Modules.Media.Domain;

namespace GeoWoof.Modules.Media.Application.Abstractions;

// Ports of the Media application layer; the Infrastructure layer implements them.

public interface IMediaAssetRepository
{
    Task<MediaAsset?> GetByIdAsync(Guid assetId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaAsset>> GetByIdsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaAsset>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken);

    Task<int> CountRequestedSinceAsync(Guid ownerId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Assets waiting for a processor, oldest first.</summary>
    Task<IReadOnlyList<Guid>> GetProcessableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically takes an asset for processing until <paramref name="leaseUntil"/>. Returns false when
    /// another processor holds it, so several API instances never work on the same image.
    /// </summary>
    Task<bool> TryClaimAsync(Guid assetId, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken);

    /// <summary>Unconfirmed uploads whose link expired before <paramref name="expiredBefore"/>, and unattached assets released before <paramref name="releasedBefore"/>.</summary>
    Task<IReadOnlyList<MediaAsset>> GetAbandonedAsync(DateTimeOffset expiredBefore, DateTimeOffset releasedBefore, int limit, CancellationToken cancellationToken);

    /// <summary>Processed assets whose original could not be deleted yet.</summary>
    Task<IReadOnlyList<MediaAsset>> GetWithLeftoverOriginalAsync(int limit, CancellationToken cancellationToken);

    void Add(MediaAsset asset);

    void Remove(MediaAsset asset);
}

/// <summary>Saves the Media module's changes in one transaction (with its Outbox).</summary>
public interface IMediaUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record StoredObjectInfo(long SizeBytes, string? ContentType);

/// <summary>The private bucket of user images.</summary>
public interface IMediaStorage
{
    /// <summary>Waits until the bucket can be used, for work that runs while the host starts.</summary>
    Task EnsureReadyAsync(CancellationToken cancellationToken);

    /// <summary>A signed link for a client to upload the object. <paramref name="contentType"/> becomes part of the signature.</summary>
    Uri CreateUploadUrl(string key, string contentType, DateTimeOffset expiresAt);

    /// <summary>A signed link to read the object. A local calculation, without network.</summary>
    Uri CreateDownloadUrl(string key, DateTimeOffset expiresAt);

    /// <summary><c>null</c> when the object does not exist.</summary>
    Task<StoredObjectInfo?> GetInfoAsync(string key, CancellationToken cancellationToken);

    Task<ReadOnlyMemory<byte>> ReadAsync(string key, CancellationToken cancellationToken);

    Task WriteAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken);

    /// <summary>Objects that do not exist are ignored.</summary>
    Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}

/// <summary>The clean versions of an image: WebP, upright and without any metadata.</summary>
public sealed record ImageVariants(int Width, int Height, ReadOnlyMemory<byte> Thumb, ReadOnlyMemory<byte> Medium);

/// <summary>Either <see cref="Variants"/> or the <c>media.*</c> code of why the file was rejected.</summary>
public sealed record ImageProcessingResult(ImageVariants? Variants, string? RejectionCode);

public interface IImageProcessor
{
    ImageProcessingResult Process(ReadOnlyMemory<byte> original, long maxPixels);
}

/// <summary>Wakes up the background processor right after an upload is confirmed. Signals are coalesced.</summary>
public interface IMediaProcessingSignal
{
    void Notify();
}
