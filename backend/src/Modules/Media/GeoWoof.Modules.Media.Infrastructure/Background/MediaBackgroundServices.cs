using System.Threading.Channels;
using GeoWoof.Modules.Media.Application;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Application.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Media.Infrastructure.Background;

internal sealed class MediaProcessingSignal : IMediaProcessingSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Completes when signalled or when <paramref name="timeout"/> elapses, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await _channel.Reader.ReadAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout elapsed: time for the fallback poll.
        }
    }
}

/// <summary>Processes confirmed uploads. Each asset is claimed with a lease, so several API instances can run this safely.</summary>
internal sealed partial class MediaProcessingService(
    IServiceScopeFactory scopeFactory,
    MediaProcessingSignal signal,
    IOptions<MediaSettings> options,
    TimeProvider timeProvider,
    ILogger<MediaProcessingService> logger) : BackgroundService
{
    private const int BatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogBatchFailed(logger, exception);
            }

            if (processed == 0)
            {
                await signal.WaitAsync(options.Value.ProcessingPollInterval, timeProvider, stoppingToken);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> assetIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            assetIds = await scope.ServiceProvider.GetRequiredService<IMediaAssetRepository>()
                .GetProcessableAsync(timeProvider.GetUtcNow(), BatchSize, cancellationToken);
        }

        var processed = 0;
        foreach (var assetId in assetIds)
        {
            if (await ProcessAssetAsync(assetId, cancellationToken))
            {
                processed++;
            }
        }

        return processed;
    }

    private async Task<bool> ProcessAssetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var claimed = await scope.ServiceProvider.GetRequiredService<IMediaAssetRepository>()
                .TryClaimAsync(assetId, now, now + options.Value.ProcessingLease, cancellationToken);
            if (!claimed)
            {
                return false;
            }

            await scope.ServiceProvider.GetRequiredService<MediaAssetProcessor>().ProcessAsync(assetId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogAssetFailed(logger, exception, assetId);

            // A new scope: the failed one may hold half-applied changes.
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MediaAssetProcessor>().RecordFailureAsync(assetId, cancellationToken);
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The media processor failed to read its work")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing image {AssetId} failed")]
    private static partial void LogAssetFailed(ILogger logger, Exception exception, Guid assetId);
}

/// <summary>Periodically removes images that nobody uses (ADR-0010).</summary>
internal sealed partial class MediaCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<MediaSettings> options,
    TimeProvider timeProvider,
    ILogger<MediaCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var removed = await scope.ServiceProvider.GetRequiredService<MediaCleanup>().RunAsync(stoppingToken);
                    LogCleanup(logger, removed);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogCleanupFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Media cleanup handled {Count} images")]
    private static partial void LogCleanup(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The media cleanup failed")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
