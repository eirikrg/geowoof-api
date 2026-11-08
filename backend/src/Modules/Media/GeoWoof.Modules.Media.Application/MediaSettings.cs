using System.ComponentModel.DataAnnotations;

namespace GeoWoof.Modules.Media.Application;

/// <summary>Image rules and background job timings (configuration section <c>Media</c>).</summary>
public sealed class MediaSettings
{
    public const string SectionName = "Media";

    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromHours(24);

    [Range(1, long.MaxValue)]
    public long MaxSizeBytes { get; set; } = 15 * 1024 * 1024;

    [Range(1, int.MaxValue)]
    public int MaxUploadsPerDay { get; set; } = 200;

    /// <summary>Largest image decoded, in pixels: protects the server from decompression bombs.</summary>
    [Range(1, long.MaxValue)]
    public long MaxImagePixels { get; set; } = 64_000_000;

    /// <summary>How long an unattached image is kept.</summary>
    public TimeSpan OrphanRetention { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan ProcessingPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ProcessingLease { get; set; } = TimeSpan.FromMinutes(2);

    [Range(1, 20)]
    public int ProcessingMaxAttempts { get; set; } = 3;

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}
