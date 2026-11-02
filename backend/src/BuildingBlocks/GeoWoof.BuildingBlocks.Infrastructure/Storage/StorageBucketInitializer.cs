using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoWoof.BuildingBlocks.Infrastructure.Storage;

/// <summary>
/// When <see cref="StorageOptions.CreateBucketIfMissing"/> is enabled, creates the media bucket in the
/// background, retrying while the storage server starts. Startup is never blocked by it.
/// </summary>
internal sealed partial class StorageBucketInitializer(
    IAmazonS3 s3,
    IOptions<StorageOptions> options,
    TimeProvider timeProvider,
    ILogger<StorageBucketInitializer> logger) : BackgroundService
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bucket = options.Value.Bucket;
        if (!options.Value.CreateBucketIfMissing)
        {
            return;
        }

        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await EnsureBucketAsync(bucket, stoppingToken);
                LogBucketReady(logger, bucket);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is AmazonServiceException or HttpRequestException or AmazonClientException)
            {
                if (attempt >= MaxAttempts)
                {
                    LogBucketFailed(logger, exception, bucket, attempt);
                    return;
                }

                LogBucketRetry(logger, bucket, attempt, exception.Message);
                await Task.Delay(RetryDelay, timeProvider, stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create storage bucket {Bucket} after {Attempt} attempts")]
    private static partial void LogBucketFailed(ILogger logger, Exception exception, string bucket, int attempt);
}
