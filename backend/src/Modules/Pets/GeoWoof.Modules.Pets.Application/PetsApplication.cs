using GeoWoof.BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Pets.Application;

public static class PetsApplication
{
    /// <summary>Handlers and validators of the Pets module.</summary>
    public static IServiceCollection AddPetsApplication(this IServiceCollection services)
    {
        services.AddApplicationHandlers(typeof(PetsApplication).Assembly);
        return services;
    }
}
