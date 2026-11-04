using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoWoof.BuildingBlocks.Infrastructure.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>Registers an <see cref="IAmazonS3"/> client for the configured S3-compatible endpoint.</summary>
    public static IServiceCollection AddGeoWoofStorage(this IServiceCollection services)
    {
        services.AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IAmazonS3>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<StorageOptions>>().Value;
            var config = new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,
                ForcePathStyle = options.ForcePathStyle,
                AuthenticationRegion = options.Region,

                // Self-hosted S3 servers do not all support the newer default integrity checksums.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            };
            return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        });

        services.AddSingleton<StorageBucket>();
        services.AddHostedService<StorageBucketInitializer>();
        return services;
    }
}
