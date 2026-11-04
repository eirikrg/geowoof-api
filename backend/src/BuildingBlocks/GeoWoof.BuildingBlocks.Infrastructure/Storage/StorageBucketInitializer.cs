using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoWoof.BuildingBlocks.Infrastructure.Storage;

/// <summary>
/// When <see cref="StorageOptions.CreateBucketIfMissing"/> is enabled, creates the media bucket,
/// retrying while the storage server starts. Without that option, the bucket is assumed to exist.
/// </summary>
public sealed partial class StorageBucket(
    IAmazonS3 s3,
    IOptions<StorageOptions> options,
    TimeProvider timeProvider,
    ILogger<StorageBucket> logger) : IDisposable
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;

    /// <summary>Completes once the bucket can be used, or throws after the last attempt.</summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready || !options.Value.CreateBucketIfMissing)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_ready)
            {
                await CreateWithRetriesAsync(options.Value.Bucket, cancellationToken);
                _ready = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task CreateWithRetriesAsync(string bucket, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureBucketAsync(bucket, cancellationToken);
                LogBucketReady(logger, bucket);
                return;
            }
            catch (Exception exception) when (attempt < MaxAttempts && exception is AmazonServiceException or HttpRequestException or AmazonClientException)
            {
                LogBucketRetry(logger, bucket, attempt, exception.Message);
                await Task.Delay(RetryDelay, timeProvider, cancellationToken);
            }
        }
    }

    private async Task EnsureBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        var response = await s3.ListBucketsAsync(new ListBucketsRequest(), cancellationToken);
        if (response.Buckets?.Exists(existing => existing.BucketName == bucket) == true)
        {
            return;
        }

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage bucket {Bucket} is ready")]
    private static partial void LogBucketReady(ILogger logger, string bucket);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Storage bucket {Bucket} not ready yet (attempt {Attempt}): {Reason}")]
    private static partial void LogBucketRetry(ILogger logger, string bucket, int attempt, string reason);
}

/// <summary>Prepares the bucket in the background when the host starts. Startup is never blocked by it.</summary>
internal sealed partial class StorageBucketInitializer(StorageBucket bucket, ILogger<StorageBucketInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await bucket.EnsureReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
        catch (Exception exception) when (exception is AmazonServiceException or HttpRequestException or AmazonClientException)
        {
            LogBucketFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not prepare the storage bucket")]
    private static partial void LogBucketFailed(ILogger logger, Exception exception);
}
