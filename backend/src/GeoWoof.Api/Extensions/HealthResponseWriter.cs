using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GeoWoof.Api.Extensions;

/// <summary>Writes health reports as JSON without exception details (those go to the logs).</summary>
internal static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                },
                StringComparer.Ordinal),
        };

        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions), context.RequestAborted);
    }
}
