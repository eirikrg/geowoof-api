namespace GeoWoof.BuildingBlocks.Contracts;

/// <summary>Convenience base record for integration events. Every concrete event needs an <see cref="EventTypeAttribute"/>.</summary>
public abstract record IntegrationEvent : IIntegrationEvent
{
    protected IntegrationEvent(DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7(occurredAt);
        OccurredAt = occurredAt;
    }

    public Guid Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}
