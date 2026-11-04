namespace GeoWoof.Modules.Media.Contracts;

/// <summary>What another module uses an image for (see <see cref="IMediaApi.AttachAsync"/>).</summary>
public enum MediaUsage
{
    UserAvatar = 0,
    PetAvatar = 1,
}

/// <summary>An image with its signed URLs, which are <c>null</c> until it has been processed.</summary>
public sealed record MediaImage(Guid AssetId, string? ThumbUrl, string? MediumUrl);

/// <summary>Why an image cannot be attached. <see cref="Code"/> is part of the API contract.</summary>
public sealed record MediaAttachFailure(string Code, string Message, bool IsNotFound);

/// <summary>In-process API of the Media module for other modules.</summary>
public interface IMediaApi
{
    /// <summary>
    /// Marks an image as used, so it is not cleaned up. It commits right away, and it is idempotent.
    /// Call it before saving the reference: if that save fails, the image only stays attached until
    /// the account is deleted, whereas the opposite order could leave a reference to nothing.
    /// </summary>
    /// <returns><c>null</c> when attached.</returns>
    Task<MediaAttachFailure?> AttachAsync(Guid assetId, Guid ownerId, MediaUsage usage, CancellationToken cancellationToken);

    /// <summary>The image is no longer used: it will be cleaned up. Unknown images are ignored.</summary>
    Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Images by id with freshly signed URLs (a local calculation, without network). Unknown ids are
    /// left out. Only call it for images the current user is allowed to see.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, MediaImage>> GetImagesAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken);

    /// <summary>Registers server-side bytes as if the owner had uploaded them (sample data).</summary>
    Task<Guid> ImportAsync(Guid ownerId, MediaUsage usage, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken);
}
