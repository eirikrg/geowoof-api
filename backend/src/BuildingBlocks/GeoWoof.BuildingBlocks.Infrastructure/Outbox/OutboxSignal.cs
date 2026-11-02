using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GeoWoof.BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// Wakes up the outbox processor of a schema right after a commit that wrote outbox messages, so
/// that delivery latency does not depend on the polling interval. Signals are coalesced.
/// </summary>
internal sealed class OutboxSignal
{
    private readonly ConcurrentDictionary<string, Channel<bool>> _channels = new(StringComparer.Ordinal);

    public void Notify(string schema) => GetChannel(schema).Writer.TryWrite(true);

    /// <summary>Completes when the schema is signalled or <paramref name="timeout"/> elapses, whichever comes first.</summary>
    public async Task WaitAsync(string schema, TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await GetChannel(schema).Reader.ReadAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout elapsed: time for the fallback poll.
        }
    }

    private Channel<bool> GetChannel(string schema) =>
        _channels.GetOrAdd(schema, static _ => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
        }));
}
