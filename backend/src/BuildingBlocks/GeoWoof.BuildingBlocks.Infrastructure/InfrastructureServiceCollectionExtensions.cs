using GeoWoof.BuildingBlocks.Infrastructure.Caching;
using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Health;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.BuildingBlocks.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GeoWoof.BuildingBlocks.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Health check tag for dependencies that must be up for the API to serve traffic.</summary>
    public const string ReadinessTag = "ready";

    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Registers everything modules share: PostgreSQL, Redis, S3 storage, eventing and readiness checks.</summary>
    public static IServiceCollection AddGeoWoofInfrastructure(this IServiceCollection services, IHostEnvironment environment)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddGeoWoofPostgres(environment);
        services.AddGeoWoofRedis();
        services.AddGeoWoofStorage();
        services.AddGeoWoofEventing();

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [ReadinessTag], timeout: HealthCheckTimeout)
            .AddCheck<RedisHealthCheck>("redis", tags: [ReadinessTag], timeout: HealthCheckTimeout)
            .AddCheck<StorageHealthCheck>("storage", tags: [ReadinessTag], timeout: HealthCheckTimeout);

        return services;
    }
}
