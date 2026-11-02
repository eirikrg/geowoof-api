using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace GeoWoof.BuildingBlocks.Presentation.Errors;

/// <summary>
/// Turns unhandled exceptions into ProblemDetails without leaking internals. Malformed requests
/// (<see cref="BadHttpRequestException"/>) keep their 4xx status; anything else is a logged 500.
/// </summary>
public sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        int statusCode;
        string title;
        if (exception is BadHttpRequestException badRequest)
        {
            statusCode = badRequest.StatusCode;
            title = "The request is invalid.";
            LogBadRequest(logger, statusCode, exception.Message);
        }
        else
        {
            statusCode = StatusCodes.Status500InternalServerError;
            title = "An unexpected error occurred.";
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Extensions = { [ProblemDetailsDefaults.CodeExtension] = ErrorCodes.ForStatus(statusCode) },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rejected malformed request ({StatusCode}): {Reason}")]
    private static partial void LogBadRequest(ILogger logger, int statusCode, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception while processing {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);
}
