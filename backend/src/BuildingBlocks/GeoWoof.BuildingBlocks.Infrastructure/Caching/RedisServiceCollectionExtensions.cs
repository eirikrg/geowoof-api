using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace GeoWoof.BuildingBlocks.Infrastructure.Caching;

public static class RedisServiceCollectionExtensions
{
    public const string ConnectionStringName = "Redis";

    /// <summary>
    /// Registers a shared <see cref="IConnectionMultiplexer"/>. It does not abort when Redis is down at
    /// startup: it keeps reconnecting in the background and the readiness check reports the outage.
    /// </summary>
    public static IServiceCollection AddGeoWoofRedis(this IServiceCollection services)
    {
        services.AddOptions<RedisConnectionOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"Missing connection string 'ConnectionStrings:{ConnectionStringName}'.")
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var options = ConfigurationOptions.Parse(configuration.GetConnectionString(ConnectionStringName)!);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        return services;
    }

    private sealed class RedisConnectionOptions
    {
        public string? ConnectionString { get; set; }
    }
}
