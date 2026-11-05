using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Walks.Application;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Contracts;
using GeoWoof.Modules.Walks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Walks.Infrastructure;

public static class WalksModule
{
    public static IServiceCollection AddWalksModule(this IServiceCollection services)
    {
        services.AddWalksApplication();

        services.AddModuleDbContext<WalksDbContext>(WalksDbContext.SchemaName);
        services.AddScoped<IWalksUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<WalksDbContext>());
        services.AddScoped<IWalkRepository, WalkRepository>();
        services.AddScoped<IDevelopmentDataSeeder, WalksDevelopmentSeeder>();
        services.AddIntegrationEvents(typeof(WalkRecorded).Assembly);
        services.AddIntegrationEventConsumers<WalksDbContext>(typeof(WalksApplication).Assembly);
        return services;
    }
}
