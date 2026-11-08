using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Pets.Application;
using GeoWoof.Modules.Pets.Application.Abstractions;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Pets.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Pets.Infrastructure;

public static class PetsModule
{
    public static IServiceCollection AddPetsModule(this IServiceCollection services)
    {
        services.AddPetsApplication();

        services.AddModuleDbContext<PetsDbContext>(PetsDbContext.SchemaName);
        services.AddScoped<IPetsUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<PetsDbContext>());
        services.AddScoped<IPetRepository, PetRepository>();
        services.AddScoped<IPetWeightRepository, PetWeightRepository>();
        services.AddScoped<IHealthEventRepository, HealthEventRepository>();
        services.AddScoped<IDevelopmentDataSeeder, PetsDevelopmentSeeder>();
        services.AddIntegrationEvents(typeof(PetCreated).Assembly);
        services.AddIntegrationEventConsumers<PetsDbContext>(typeof(PetsApplication).Assembly);
        return services;
    }
}
