using System.Reflection;
using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Behaviors;
using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.BuildingBlocks.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the command, query and domain event handlers and the validators of a module's
    /// Application assembly. Call <see cref="DecorateApplicationHandlers"/> once, after every module.
    /// </summary>
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services, Assembly applicationAssembly)
    {
        ArgumentNullException.ThrowIfNull(applicationAssembly);

        services.Scan(scan => scan
            .FromAssemblies(applicationAssembly)
            .AddClasses(
                classes => classes.AssignableToAny(
                    typeof(ICommandHandler<>),
                    typeof(ICommandHandler<,>),
                    typeof(IQueryHandler<,>),
                    typeof(IDomainEventHandler<>)),
                publicOnly: false)
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        services.AddValidatorsFromAssembly(applicationAssembly, ServiceLifetime.Scoped, includeInternalTypes: true);
        return services;
    }

    /// <summary>
    /// Wraps every registered handler as logging → validation → handler.
    /// Must run after every module has registered its handlers.
    /// </summary>
    public static IServiceCollection DecorateApplicationHandlers(this IServiceCollection services)
    {
        // Decorators applied later wrap the earlier ones, so validation is applied first.
        services.TryDecorate(typeof(ICommandHandler<>), typeof(ValidationDecorator.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationDecorator.CommandHandler<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(ValidationDecorator.QueryHandler<,>));

        services.TryDecorate(typeof(ICommandHandler<>), typeof(LoggingDecorator.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingDecorator.CommandHandler<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingDecorator.QueryHandler<,>));
        return services;
    }
}
