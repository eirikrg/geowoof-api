using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Social.Application;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Contracts;
using GeoWoof.Modules.Social.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Social.Infrastructure;

public static class SocialModule
{
    public static IServiceCollection AddSocialModule(this IServiceCollection services)
    {
        services.AddSocialApplication();

        services.AddModuleDbContext<SocialDbContext>(SocialDbContext.SchemaName);
        services.AddScoped<ISocialUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<SocialDbContext>());
        services.AddScoped<IFollowRepository, FollowRepository>();
        services.AddScoped<IFollowRequestRepository, FollowRequestRepository>();
        services.AddScoped<IUserBlockRepository, UserBlockRepository>();
        services.AddScoped<IReportRepository, ReportRepository>();
        services.AddScoped<ISocialGraphStore, SocialGraphStore>();
        services.AddScoped<ISocialGraphCache, HybridSocialGraphCache>();
        services.AddScoped<IDevelopmentDataSeeder, SocialDevelopmentSeeder>();
        services.AddIntegrationEvents(typeof(UserFollowed).Assembly);
        services.AddIntegrationEventConsumers<SocialDbContext>(typeof(SocialApplication).Assembly);
        return services;
    }
}

/// <summary>
/// In memory and in Redis (ADR-0003). Whoever changes a relationship removes the entries of both
/// people after saving. With several API instances, the copy in memory of another instance can last
/// up to <see cref="LocalExpiration"/>.
/// </summary>
internal sealed class HybridSocialGraphCache(HybridCache cache, ISocialGraphStore store) : ISocialGraphCache
{
    private static readonly TimeSpan LocalExpiration = TimeSpan.FromMinutes(1);

    private static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = LocalExpiration,
    };

    private static string Key(Guid userId) => $"social:graph:{userId}";

    public ValueTask<SocialGraph> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            Key(userId),
            (store, userId),
            static async (state, token) => await state.store.LoadAsync(state.userId, token),
            Options,
            cancellationToken: cancellationToken);

    public async ValueTask InvalidateAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(Key(first), cancellationToken);
        await cache.RemoveAsync(Key(second), cancellationToken);
    }
}
