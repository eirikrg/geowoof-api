using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Contracts;
using Microsoft.Extensions.Logging;

namespace GeoWoof.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// Scoped buffer behind <see cref="IIntegrationEventPublisher"/>. The next module DbContext that
/// saves changes in the scope moves the buffered events into its Outbox, in the same transaction.
/// </summary>
internal sealed partial class IntegrationEventBuffer(
    IntegrationEventTypeRegistry registry,
    ILogger<IntegrationEventBuffer> logger) : IIntegrationEventPublisher, IDisposable
{
    private readonly List<IIntegrationEvent> _pending = [];

    public int Count => _pending.Count;

    public void Publish(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        // Fail at publish time (not later in the outbox) when the event type is not registered.
        _ = registry.GetName(integrationEvent.GetType());
        _pending.Add(integrationEvent);
    }

    public IReadOnlyList<IIntegrationEvent> Drain()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    public void Dispose()
    {
        if (_pending.Count > 0)
        {
            LogDiscarded(logger, _pending.Count, string.Join(", ", _pending.Select(e => e.GetType().Name).Distinct()));
            _pending.Clear();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Count} integration event(s) ({EventTypes}) were published but never saved: no module DbContext saved changes in the scope")]
    private static partial void LogDiscarded(ILogger logger, int count, string eventTypes);
}
