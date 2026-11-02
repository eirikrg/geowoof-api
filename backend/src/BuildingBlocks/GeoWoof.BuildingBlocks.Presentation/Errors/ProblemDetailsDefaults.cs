using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace GeoWoof.BuildingBlocks.Presentation.Errors;

/// <summary>Adds the GeoWoof extensions (<c>code</c>, <c>traceId</c>) to every ProblemDetails response (RFC 9457).</summary>
public static class ProblemDetailsDefaults
{
    public const string CodeExtension = "code";
    public const string TraceIdExtension = "traceId";

    public static void Customize(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = context.ProblemDetails;
        var statusCode = problem.Status ?? context.HttpContext.Response.StatusCode;

        problem.Extensions.TryAdd(CodeExtension, ErrorCodes.ForStatus(statusCode));
        problem.Extensions[TraceIdExtension] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
        problem.Instance ??= context.HttpContext.Request.Path;
    }
}
