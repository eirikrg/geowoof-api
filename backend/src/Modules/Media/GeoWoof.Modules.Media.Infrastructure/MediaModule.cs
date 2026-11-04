using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Media.Application;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Media.Infrastructure.Background;
using GeoWoof.Modules.Media.Infrastructure.Images;
using GeoWoof.Modules.Media.Infrastructure.Persistence;
using GeoWoof.Modules.Media.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Media.Infrastructure;

public static class MediaModule
{
    public static IServiceCollection AddMediaModule(this IServiceCollection services)
    {
        services.AddOptions<MediaSettings>().BindConfiguration(MediaSettings.SectionName).ValidateDataAnnotations().ValidateOnStart();

        services.AddMediaApplication();

        services.AddModuleDbContext<MediaDbContext>(MediaDbContext.SchemaName);
        services.AddScoped<IMediaUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<MediaDbContext>());
        services.AddScoped<IMediaAssetRepository, MediaAssetRepository>();
        services.AddIntegrationEvents(typeof(MediaAssetReady).Assembly);
        services.AddIntegrationEventConsumers<MediaDbContext>(typeof(MediaApplication).Assembly);

        services.AddSingleton<IMediaStorage, S3MediaStorage>();
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();
        services.AddSingleton<MediaProcessingSignal>();
        services.AddSingleton<IMediaProcessingSignal>(serviceProvider => serviceProvider.GetRequiredService<MediaProcessingSignal>());
        services.AddHostedService<MediaProcessingService>();
        services.AddHostedService<MediaCleanupService>();
        return services;
    }
}
