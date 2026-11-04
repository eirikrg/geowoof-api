namespace GeoWoof.BuildingBlocks.Domain.Results;

/// <summary>
/// A business error with a stable, machine-readable <see cref="Code"/> (for example
/// <c>pets.not_found</c>). Clients translate the code into localized text.
/// </summary>
public sealed record Error
{
    private Error(string code, string message, ErrorType type, IReadOnlyDictionary<string, string[]>? fieldErrors, TimeSpan? retryAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        Type = type;
        FieldErrors = fieldErrors;
        RetryAfter = retryAfter;
    }

    public string Code { get; }

    public string Message { get; }

    public ErrorType Type { get; }

    /// <summary>Per-field validation messages (camelCase field names). Only for <see cref="ErrorType.Validation"/>.</summary>
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; }

    /// <summary>When the client may retry (sent as the <c>Retry-After</c> header), for lockouts and cooldowns.</summary>
    public TimeSpan? RetryAfter { get; }

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure, null);

    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(code, message, ErrorType.Validation, fieldErrors);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized, null);

    public static Error Forbidden(string code, string message, TimeSpan? retryAfter = null) =>
        new(code, message, ErrorType.Forbidden, null, retryAfter);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound, null);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict, null);

    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.Unprocessable, null);

    public static Error TooManyRequests(string code, string message, TimeSpan? retryAfter = null) =>
        new(code, message, ErrorType.TooManyRequests, null, retryAfter);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable, null);
}
