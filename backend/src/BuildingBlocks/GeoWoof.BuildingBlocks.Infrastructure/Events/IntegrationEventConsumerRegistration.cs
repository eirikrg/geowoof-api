using System.Reflection;
using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.BuildingBlocks.Infrastructure.Events;

/// <summary>An <see cref="IIntegrationEventHandler{TEvent}"/> together with the module DbContext that owns its Inbox.</summary>
public sealed class IntegrationEventConsumerRegistration
{
    private static readonly MethodInfo InvokeTypedMethod =
        typeof(IntegrationEventConsumerRegistration).GetMethod(nameof(InvokeTyped), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Func<object, IIntegrationEvent, CancellationToken, Task> _invoke;

    public IntegrationEventConsumerRegistration(Type eventType, Type handlerType, Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(dbContextType);

        EventType = eventType;
        HandlerType = handlerType;
        DbContextType = dbContextType;
        ConsumerName = handlerType.FullName ?? handlerType.Name;
        _invoke = InvokeTypedMethod.MakeGenericMethod(eventType)
            .CreateDelegate<Func<object, IIntegrationEvent, CancellationToken, Task>>();
    }

    public Type EventType { get; }

    public Type HandlerType { get; }

    public Type DbContextType { get; }

    /// <summary>Name stored in the Inbox to deduplicate deliveries to this consumer.</summary>
    public string ConsumerName { get; }

    internal Task InvokeAsync(object handler, IIntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        _invoke(handler, integrationEvent, cancellationToken);

    private static Task InvokeTyped<TEvent>(object handler, IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent =>
        ((IIntegrationEventHandler<TEvent>)handler).Handle((TEvent)integrationEvent, cancellationToken);
}
