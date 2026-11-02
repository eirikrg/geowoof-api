namespace GeoWoof.BuildingBlocks.Infrastructure.Outbox;

/// <summary>Exponential backoff for failed outbox deliveries.</summary>
internal sealed record OutboxRetryPolicy(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    public static OutboxRetryPolicy From(OutboxOptions options) =>
        new(options.MaxAttempts, options.BaseRetryDelay, options.MaxRetryDelay);

    /// <summary>Delay before the next attempt once <paramref name="failedAttempts"/> attempts have failed.</summary>
    public TimeSpan DelayAfter(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        var exponent = Math.Min(failedAttempts - 1, 30);
        var delayTicks = BaseDelay.Ticks * Math.Pow(2, exponent);
        return delayTicks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)delayTicks);
    }
}
