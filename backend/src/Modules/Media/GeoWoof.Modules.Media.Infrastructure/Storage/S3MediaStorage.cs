using System.Net;
using System.Runtime.InteropServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using GeoWoof.BuildingBlocks.Infrastructure.Storage;
using GeoWoof.Modules.Media.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Media.Infrastructure.Storage;

/// <summary>
/// The media bucket through the S3 API. The API talks to the internal endpoint, while signed links
/// use the public one, because a signature covers the host that the client will connect to.
/// </summary>
internal sealed class S3MediaStorage : IMediaStorage, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly StorageBucket _bucket;
    private readonly AmazonS3Client _signer;
    private readonly Protocol _publicProtocol;
    private readonly string _bucketName;

    public S3MediaStorage(IAmazonS3 s3, StorageBucket bucket, IOptions<StorageOptions> options)
    {
        var storage = options.Value;
        _s3 = s3;
        _bucket = bucket;
        _bucketName = storage.Bucket;

        var publicEndpoint = string.IsNullOrWhiteSpace(storage.PublicEndpoint) ? storage.ServiceUrl : storage.PublicEndpoint;
        _publicProtocol = publicEndpoint.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP;
        _signer = new AmazonS3Client(
            new BasicAWSCredentials(storage.AccessKey, storage.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = publicEndpoint,
                ForcePathStyle = storage.ForcePathStyle,
                AuthenticationRegion = storage.Region,
            });
    }

    public Task EnsureReadyAsync(CancellationToken cancellationToken) => _bucket.EnsureReadyAsync(cancellationToken);

    public Uri CreateUploadUrl(string key, string contentType, DateTimeOffset expiresAt) =>
        new(_signer.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
            Protocol = _publicProtocol,
        }));

    public Uri CreateDownloadUrl(string key, DateTimeOffset expiresAt) =>
        new(_signer.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
            Protocol = _publicProtocol,
        }));

    public async Task<StoredObjectInfo?> GetInfoAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _s3.GetObjectMetadataAsync(_bucketName, key, cancellationToken);
            return new StoredObjectInfo(metadata.ContentLength, metadata.Headers.ContentType);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await _s3.GetObjectAsync(_bucketName, key, cancellationToken);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        var bytes = MemoryMarshal.TryGetArray(content, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : content.ToArray();
        using var stream = new MemoryStream(bytes, writable: false);
        await _s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = stream,
                ContentType = contentType,
                AutoCloseStream = false,

                // Self-hosted S3 servers do not all support chunked signed payloads.
                UseChunkEncoding = false,
            },
            cancellationToken);
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            await _s3.DeleteObjectAsync(_bucketName, key, cancellationToken);
        }
    }

    public void Dispose() => _signer.Dispose();
}
