using System.ComponentModel.DataAnnotations;

namespace GeoWoof.BuildingBlocks.Infrastructure.Storage;

/// <summary>S3-compatible object storage settings (configuration section <c>Storage</c>).</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Endpoint used by the API itself (for example <c>http://storage:8333</c> inside Docker).</summary>
    [Required]
    [Url]
    public string ServiceUrl { get; set; } = string.Empty;

    [Required]
    public string AccessKey { get; set; } = string.Empty;

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Private bucket for user media.</summary>
    [Required]
    [RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", ErrorMessage = "Storage:Bucket must be a valid S3 bucket name.")]
    public string Bucket { get; set; } = "geowoof-media";

    [Required]
    public string Region { get; set; } = "us-east-1";

    /// <summary>Path-style URLs (<c>host/bucket/key</c>) are required by most self-hosted S3 servers.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Creates the bucket at startup if it does not exist (local development and tests).</summary>
    public bool CreateBucketIfMissing { get; set; }
}
