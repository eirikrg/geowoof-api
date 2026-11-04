using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Presentation.Errors;
using Microsoft.AspNetCore.Http;

namespace GeoWoof.BuildingBlocks.Presentation.Results;

/// <summary>Maps application <see cref="Result"/>s to HTTP responses; failures become ProblemDetails with a stable <c>code</c>.</summary>
public static class ResultHttpExtensions
{
    /// <summary><c>204 No Content</c> on success, ProblemDetails on failure.</summary>
    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    /// <summary>Calls <paramref name="onSuccess"/> with the value on success, ProblemDetails on failure.</summary>
    public static IResult ToHttpResult<TValue>(this Result<TValue> result, Func<TValue, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();
    }

    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var statusCode = StatusCodeFor(error.Type);
        var extensions = new Dictionary<string, object?> { [ProblemDetailsDefaults.CodeExtension] = error.Code };

        IResult problem = error is { Type: ErrorType.Validation, FieldErrors: not null }
            ? TypedResults.ValidationProblem(
                error.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                detail: error.Message,
                title: TitleFor(statusCode),
                extensions: extensions)
            : TypedResults.Problem(
                detail: error.Message,
                statusCode: statusCode,
                title: TitleFor(statusCode),
                extensions: extensions);

        return error.RetryAfter is { } retryAfter ? new RetryAfterResult(problem, retryAfter) : problem;
    }

    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TitleFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "The request is invalid.",
        StatusCodes.Status401Unauthorized => "Authentication is required.",
        StatusCodes.Status403Forbidden => "You are not allowed to perform this action.",
        StatusCodes.Status404NotFound => "The resource was not found.",
        StatusCodes.Status409Conflict => "The request conflicts with the current state.",
        StatusCodes.Status422UnprocessableEntity => "The request cannot be processed.",
        StatusCodes.Status429TooManyRequests => "Too many requests.",
        StatusCodes.Status503ServiceUnavailable => "The service is temporarily unavailable.",
        _ => "The request failed.",
    };
}
