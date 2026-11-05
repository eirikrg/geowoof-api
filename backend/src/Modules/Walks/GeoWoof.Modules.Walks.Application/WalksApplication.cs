using GeoWoof.BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Walks.Application;

public static class WalksApplication
{
    /// <summary>Handlers and validators of the Walks module.</summary>
    public static IServiceCollection AddWalksApplication(this IServiceCollection services)
    {
        services.AddApplicationHandlers(typeof(WalksApplication).Assembly);
        return services;
    }
}
