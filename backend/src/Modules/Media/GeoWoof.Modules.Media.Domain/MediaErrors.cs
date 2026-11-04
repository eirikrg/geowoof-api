using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Media.Domain;

/// <summary>Business errors of the Media module. Codes are part of the API contract (docs/api.md).</summary>
public static class MediaErrors
{
    public const string InvalidImageCode = "media.invalid_image";
    public const string ImageTooLargeCode = "media.image_too_large";
    public const string ProcessingFailedCode = "media.processing_failed";
    public const string UploadMismatchCode = "media.upload_mismatch";

    public static readonly Error NotFound =
        Error.NotFound("media.not_found", "The image does not exist.");

    public static readonly Error QuotaExceeded =
        Error.TooManyRequests("media.quota_exceeded", "Too many images uploaded today. Try again later.");

    public static readonly Error UploadMissing =
        Error.Conflict("media.upload_missing", "The image has not been uploaded, or its upload link has expired.");

    public static readonly Error UploadMismatch =
        Error.Unprocessable(UploadMismatchCode, "The uploaded file does not match the declared size and type.");
}
