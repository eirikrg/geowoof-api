using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using GeoWoof.BuildingBlocks.Presentation.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace GeoWoof.Api.Extensions;

internal static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddGeoWoofApi(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddOptions<ApiOptions>()
            .Configure(options => options.ExposeDocumentation = environment.IsDevelopment())
            .BindConfiguration(ApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Customize);
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IOptions<ApiOptions>>((cors, api) => cors.AddDefaultPolicy(policy => policy
                .WithOrigins(api.Value.CorsAllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()));

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<ApiOptions>>((rateLimiter, api) => ConfigureRateLimiter(rateLimiter, api.Value.GlobalPermitsPerMinute));

        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "GeoWoof API",
                Version = "v1",
                Description = "REST API of GeoWoof. Errors are RFC 9457 ProblemDetails with a stable `code` extension.",
            };
            return Task.CompletedTask;
        }));

        return services;
    }

    private static void ConfigureRateLimiter(RateLimiterOptions options, int permitsPerMinute)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetTokenBucketLimiter(
                PartitionKeyFor(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = permitsPerMinute,
                    TokensPerPeriod = Math.Max(1, permitsPerMinute / 6),
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

        options.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
            await problemDetails.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context.HttpContext,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests.",
                    Detail = "The rate limit was exceeded. Retry later.",
                },
            });
        };
    }

    /// <summary>Authenticated users are limited per user id; anonymous clients per IP address.</summary>
    private static string PartitionKeyFor(HttpContext context)
    {
        var userId = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId is not null
            ? "user:" + userId
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
