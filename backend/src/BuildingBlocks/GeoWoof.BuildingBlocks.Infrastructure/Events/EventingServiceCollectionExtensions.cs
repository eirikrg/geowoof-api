using System.Reflection;
using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Outbox;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GeoWoof.BuildingBlocks.Infrastructure.Events;

public static class EventingServiceCollectionExtensions
{
    /// <summary>Registers the domain event dispatcher, the integration event publisher and the Outbox core services.</summary>
    public static IServiceCollection AddGeoWoofEventing(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<OutboxOptions>()
            .BindConfiguration(OutboxOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(options => options.PollingInterval > TimeSpan.Zero, "Outbox:PollingInterval must be positive.")
            .Validate(options => options.BaseRetryDelay > TimeSpan.Zero, "Outbox:BaseRetryDelay must be positive.")
            .Validate(options => options.MaxRetryDelay >= options.BaseRetryDelay, "Outbox:MaxRetryDelay must be >= BaseRetryDelay.")
            .ValidateOnStart();

        services.TryAddSingleton<OutboxSignal>();
        services.TryAddSingleton<IntegrationEventTypeRegistry>();
        services.TryAddSingleton<IntegrationEventDispatcher>();
        services.TryAddScoped<IntegrationEventBuffer>();
        services.TryAddScoped<IIntegrationEventPublisher>(serviceProvider => serviceProvider.GetRequiredService<IntegrationEventBuffer>());
        services.TryAddScoped<DomainEventDispatcher>();
        services.TryAddScoped<ModuleSaveChangesInterceptor>();
        return services;
    }

    /// <summary>Makes the integration events declared in a module's Contracts assembly publishable and consumable.</summary>
    public static IServiceCollection AddIntegrationEvents(this IServiceCollection services, Assembly contractsAssembly)
    {
        ArgumentNullException.ThrowIfNull(contractsAssembly);
        services.AddSingleton(new IntegrationEventAssembly(contractsAssembly));
        return services;
    }

    /// <summary>
    /// Registers every <see cref="IIntegrationEventHandler{TEvent}"/> found in <paramref name="assembly"/>
    /// as a consumer whose Inbox lives in <typeparamref name="TContext"/>.
    /// </summary>
    public static IServiceCollection AddIntegrationEventConsumers<TContext>(this IServiceCollection services, Assembly assembly)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var handlerTypes = assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false });
        foreach (var handlerType in handlerTypes)
        {
            var eventTypes = handlerType.GetInterfaces()
                .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>))
                .Select(contract => contract.GetGenericArguments()[0])
                .ToList();
            if (eventTypes.Count == 0)
            {
                continue;
            }

            services.TryAddScoped(handlerType);
            foreach (var eventType in eventTypes)
            {
                services.AddSingleton(new IntegrationEventConsumerRegistration(eventType, handlerType, typeof(TContext)));
            }
        }

        return services;
    }
}
