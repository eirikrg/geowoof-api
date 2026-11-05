using System.Text.Json.Serialization;
using GeoWoof.BuildingBlocks.Infrastructure.Caching;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace GeoWoof.Api.Extensions;

internal static class RealtimeExtensions
{
    /// <summary>The same JSON as the API (docs/realtime.md), and Redis so that several instances share the messages.</summary>
    public static ISignalRServerBuilder AddGeoWoofRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        return services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.PayloadSerializerOptions.Converters.Add(new UtcDateTimeOffsetJsonConverter());
            })
            .AddStackExchangeRedis(options =>
            {
                // It does not stop the API from starting when Redis is down, like the shared connection.
                options.Configuration = ConfigurationOptions.Parse(configuration.GetConnectionString(RedisServiceCollectionExtensions.ConnectionStringName) ?? string.Empty);
                options.Configuration.AbortOnConnectFail = false;
            });
    }
}
