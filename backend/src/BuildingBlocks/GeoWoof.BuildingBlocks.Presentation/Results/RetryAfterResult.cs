using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace GeoWoof.BuildingBlocks.Presentation.Results;

/// <summary>Adds a <c>Retry-After</c> header (whole seconds, rounded up) to another result.</summary>
internal sealed class RetryAfterResult(IResult inner, TimeSpan retryAfter) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return inner.ExecuteAsync(httpContext);
    }
}
