using GeoWoof.BuildingBlocks.Contracts;
using GeoWoof.BuildingBlocks.Infrastructure.Events;

namespace GeoWoof.BuildingBlocks.Infrastructure.Outbox;

/// <summary>An integration event stored in the producer module's schema, in the same transaction as the business change.</summary>
public sealed class OutboxMessage
{
    private const int MaxErrorLength = 4000;

    /// <summary>Constructor for EF Core.</summary>
    private OutboxMessage()
    {
        Type = string.Empty;
        Payload = string.Empty;
    }

    private OutboxMessage(Guid id, string type, string payload, DateTimeOffset occurredAt)
    {
        Id = id;
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    /// <summary>Stable event name from <see cref="EventTypeAttribute"/>.</summary>
    public string Type { get; private set; }

    /// <summary>JSON payload (stored as <c>jsonb</c>).</summary>
    public string Payload { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Set when the message will not be retried any more (needs manual inspection).</summary>
    public DateTimeOffset? FailedAt { get; private set; }

    public string? LastError { get; private set; }

    internal static OutboxMessage Create(IIntegrationEvent integrationEvent, IntegrationEventTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return new OutboxMessage(
            integrationEvent.Id,
            registry.GetName(integrationEvent.GetType()),
            IntegrationEventSerializer.Serialize(integrationEvent),
            integrationEvent.OccurredAt);
    }

    internal void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt = now;
        NextAttemptAt = null;
        LastError = null;
    }

    internal void MarkAttemptFailed(string error, DateTimeOffset now, OutboxRetryPolicy retryPolicy)
    {
        Attempts++;
        LastError = Truncate(error);
        if (Attempts >= retryPolicy.MaxAttempts)
        {
            FailedAt = now;
            NextAttemptAt = null;
        }
        else
        {
            NextAttemptAt = now + retryPolicy.DelayAfter(Attempts);
        }
    }

    internal void MarkPermanentlyFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = Truncate(error);
        FailedAt = now;
        NextAttemptAt = null;
    }

    private static string Truncate(string error) => error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}
