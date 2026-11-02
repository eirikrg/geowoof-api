using GeoWoof.BuildingBlocks.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace GeoWoof.Api.Extensions;

internal static class ApiPipelineExtensions
{
    public static WebApplication UseGeoWoofPipeline(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseGeoWoofRequestLogging();
        app.UseCors();
        app.UseRateLimiter();
        return app;
    }

    /// <summary>
    /// <c>/health/live</c>: the process is up (no dependency checks).
    /// <c>/health/ready</c>: PostgreSQL, Redis and storage are reachable (503 otherwise).
    /// </summary>
    public static WebApplication MapGeoWoofHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = HealthResponseWriter.WriteAsync,
        }).DisableRateLimiting();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(InfrastructureServiceCollectionExtensions.ReadinessTag),
            ResponseWriter = HealthResponseWriter.WriteAsync,
        }).DisableRateLimiting();

        return app;
    }

    /// <summary>OpenAPI document at <c>/openapi/v1.json</c> and Scalar UI at <c>/scalar</c>, when enabled.</summary>
    public static WebApplication MapGeoWoofDocumentation(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ExposeDocumentation)
        {
            return app;
        }

        app.MapOpenApi().DisableRateLimiting();
        app.MapScalarApiReference(options => options.WithTitle("GeoWoof API")).DisableRateLimiting();
        return app;
    }
}
