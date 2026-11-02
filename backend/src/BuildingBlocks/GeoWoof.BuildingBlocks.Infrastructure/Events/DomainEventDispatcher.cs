using System.Collections.Concurrent;
using System.Reflection;
using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.BuildingBlocks.Infrastructure.Events;

/// <summary>Invokes the <see cref="IDomainEventHandler{TEvent}"/>s registered in the current scope.</summary>
internal sealed class DomainEventDispatcher(IServiceProvider services)
{
    private static readonly MethodInfo DispatchTypedMethod =
        typeof(DomainEventDispatcher).GetMethod(nameof(DispatchTyped), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IDomainEvent, CancellationToken, Task>> Invokers = new();

    public Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var invoker = Invokers.GetOrAdd(
            domainEvent.GetType(),
            static eventType => DispatchTypedMethod.MakeGenericMethod(eventType)
                .CreateDelegate<Func<IServiceProvider, IDomainEvent, CancellationToken, Task>>());
        return invoker(services, domainEvent, cancellationToken);
    }

    private static async Task DispatchTyped<TEvent>(IServiceProvider services, IDomainEvent domainEvent, CancellationToken cancellationToken)
        where TEvent : IDomainEvent
    {
        foreach (var handler in services.GetServices<IDomainEventHandler<TEvent>>())
        {
            await handler.Handle((TEvent)domainEvent, cancellationToken);
        }
    }
}
