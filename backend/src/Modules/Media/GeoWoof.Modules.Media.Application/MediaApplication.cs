using GeoWoof.BuildingBlocks.Application;
using GeoWoof.Modules.Media.Application.Processing;
using GeoWoof.Modules.Media.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Media.Application;

public static class MediaApplication
{
    /// <summary>Handlers, validators and application services of the Media module.</summary>
    public static IServiceCollection AddMediaApplication(this IServiceCollection services)
    {
        services.AddApplicationHandlers(typeof(MediaApplication).Assembly);
        services.AddScoped<IMediaApi, MediaApi>();
        services.AddScoped<MediaAssetProcessor>();
        services.AddScoped<MediaCleanup>();
        return services;
    }
}
