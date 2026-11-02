using System.Globalization;
using GeoWoof.BuildingBlocks.Application;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace GeoWoof.Api.Extensions;

internal static class ObservabilityExtensions
{
    private const string DefaultServiceName = "geowoof-api";
    private const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Serilog (readable console in Development, compact JSON elsewhere) and OpenTelemetry traces and
    /// metrics. Logs, traces and metrics are exported over OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// </summary>
    public static WebApplicationBuilder AddGeoWoofObservability(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var serviceName = configuration["OTEL_SERVICE_NAME"] ?? DefaultServiceName;
        var otlpEndpoint = configuration[OtlpEndpointKey];
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddSerilog((services, logger) =>
        {
            logger.ReadFrom.Configuration(configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext();

            if (builder.Environment.IsDevelopment())
            {
                logger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    formatProvider: CultureInfo.InvariantCulture);
            }
            else
            {
                logger.WriteTo.Console(new RenderedCompactJsonFormatter());
            }

            if (exportOtlp)
            {
                logger.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = otlpEndpoint;
                    options.Protocol = OtlpProtocol.Grpc;
                    options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = serviceName };
                });
            }
        });

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(ApplicationDiagnostics.ActivitySourceName)
                .AddSource("GeoWoof.*"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("GeoWoof.*"));

        if (exportOtlp)
        {
            openTelemetry.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>One structured log line per request; health probes are logged at Verbose only.</summary>
    public static WebApplication UseGeoWoofRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options => options.GetLevel = (context, _, exception) =>
            context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                ? LogEventLevel.Verbose
                : exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : LogEventLevel.Information);
        return app;
    }
}
