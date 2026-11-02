using System.Reflection;
using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.BuildingBlocks.Infrastructure.Events;

/// <summary>A Contracts assembly whose integration events can be published and consumed.</summary>
public sealed record IntegrationEventAssembly(Assembly Assembly);

/// <summary>Maps integration event CLR types to their stable <see cref="EventTypeAttribute"/> names and back.</summary>
public sealed class IntegrationEventTypeRegistry
{
    private readonly Dictionary<string, Type> _typesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _namesByType = [];

    public IntegrationEventTypeRegistry(IEnumerable<IntegrationEventAssembly> assemblies)
        : this(EventTypesIn(assemblies))
    {
    }

    internal IntegrationEventTypeRegistry(IEnumerable<Type> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        foreach (var type in eventTypes)
        {
            Register(type);
        }
    }

    public IReadOnlyCollection<Type> EventTypes => _namesByType.Keys;

    public bool IsRegistered(Type eventType) => _namesByType.ContainsKey(eventType);

    public string GetName(Type eventType) =>
        _namesByType.TryGetValue(eventType, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Integration event '{eventType.FullName}' is not registered. Register its Contracts assembly with AddIntegrationEvents().");

    /// <summary>Returns the CLR type for a stored event name, or <c>null</c> if it is unknown.</summary>
    public Type? Resolve(string name) => _typesByName.GetValueOrDefault(name);

    private static IEnumerable<Type> EventTypesIn(IEnumerable<IntegrationEventAssembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return assemblies
            .Select(registration => registration.Assembly)
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(IsConcreteIntegrationEvent);
    }

    internal static bool IsConcreteIntegrationEvent(Type type) =>
        type is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(type);

    private void Register(Type type)
    {
        var attribute = type.GetCustomAttribute<EventTypeAttribute>()
            ?? throw new InvalidOperationException($"Integration event '{type.FullName}' must declare [EventType(\"module.event_name.vN\")].");

        if (_typesByName.TryGetValue(attribute.Name, out var existing) && existing != type)
        {
            throw new InvalidOperationException(
                $"Event type name '{attribute.Name}' is used by both '{existing.FullName}' and '{type.FullName}'.");
        }

        _typesByName[attribute.Name] = type;
        _namesByType[type] = attribute.Name;
    }
}
