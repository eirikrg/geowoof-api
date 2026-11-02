namespace GeoWoof.BuildingBlocks.Presentation.Errors;

/// <summary>Generic error codes produced by the HTTP pipeline (module codes look like <c>pets.not_found</c>).</summary>
public static class ErrorCodes
{
    public const string InvalidRequest = "request.invalid";
    public const string NotFound = "request.not_found";
    public const string MethodNotAllowed = "request.method_not_allowed";
    public const string Conflict = "request.conflict";
    public const string UnsupportedMediaType = "request.unsupported_media_type";
    public const string PayloadTooLarge = "request.payload_too_large";
    public const string Unauthorized = "auth.unauthorized";
    public const string Forbidden = "auth.forbidden";
    public const string RateLimited = "rate_limit.exceeded";
    public const string InternalError = "server.internal_error";
    public const string Unavailable = "server.unavailable";
    public const string Failed = "request.failed";

    /// <summary>Default code for a bare status code (for example, an unmatched route).</summary>
    public static string ForStatus(int statusCode) => statusCode switch
    {
        400 => InvalidRequest,
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        405 => MethodNotAllowed,
        409 => Conflict,
        413 => PayloadTooLarge,
        415 => UnsupportedMediaType,
        429 => RateLimited,
        503 => Unavailable,
        >= 500 => InternalError,
        _ => Failed,
    };
}
