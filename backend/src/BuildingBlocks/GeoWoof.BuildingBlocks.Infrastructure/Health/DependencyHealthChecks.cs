using Amazon.S3;
using Amazon.S3.Model;
using GeoWoof.BuildingBlocks.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace GeoWoof.BuildingBlocks.Infrastructure.Health;

// Exceptions thrown by a check are turned into an Unhealthy result by the health check service.

/// <summary>Checks that PostgreSQL accepts queries.</summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}

/// <summary>Checks that Redis answers a PING.</summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await redis.GetDatabase().PingAsync();
        return HealthCheckResult.Healthy();
    }
}

/// <summary>Checks that the media bucket exists and is readable with the configured credentials.</summary>
internal sealed class StorageHealthCheck(IAmazonS3 s3, IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Value.Bucket, MaxKeys = 1 }, cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
