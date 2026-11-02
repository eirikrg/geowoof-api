using System.ComponentModel.DataAnnotations;

namespace GeoWoof.BuildingBlocks.Infrastructure.Outbox;

/// <summary>Outbox processing settings (configuration section <c>Outbox</c>).</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Maximum messages locked and dispatched per batch.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;

    /// <summary>Fallback polling interval; processors also wake up right after a commit that wrote messages.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts before a message is marked as failed and no longer retried.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Delay after the first failure; it doubles on every further failure.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
}
