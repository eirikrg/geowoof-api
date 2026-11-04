using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Domain;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Media.Application.Uploads;

/// <summary>Issues a signed link to upload an image directly to storage (ADR-0010).</summary>
public sealed record RequestUploadCommand(Guid OwnerId, string ContentType, long SizeBytes, string Purpose) : ICommand<MediaUploadDto>;

internal sealed class RequestUploadCommandValidator : AbstractValidator<RequestUploadCommand>
{
    private static readonly string[] AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"];

    public RequestUploadCommandValidator(IOptions<MediaSettings> settings)
    {
        var maxSize = settings.Value.MaxSizeBytes;
        RuleFor(command => command.ContentType)
            .Must(contentType => AllowedContentTypes.Contains(contentType, StringComparer.Ordinal))
            .WithMessage("Use image/jpeg, image/png or image/webp.");
        RuleFor(command => command.SizeBytes)
            .InclusiveBetween(1, maxSize)
            .WithMessage($"The image must have between 1 byte and {maxSize} bytes.");
        RuleFor(command => command.Purpose)
            .Must(purpose => Enum.GetNames<MediaPurpose>().Contains(purpose, StringComparer.Ordinal))
            .WithMessage($"Use one of: {string.Join(", ", Enum.GetNames<MediaPurpose>())}.");
    }
}

internal sealed class RequestUploadCommandHandler(
    IMediaAssetRepository assets,
    IMediaStorage storage,
    IMediaUnitOfWork unitOfWork,
    IOptions<MediaSettings> settings,
    TimeProvider timeProvider) : ICommandHandler<RequestUploadCommand, MediaUploadDto>
{
    public async Task<Result<MediaUploadDto>> Handle(RequestUploadCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var requestedToday = await assets.CountRequestedSinceAsync(command.OwnerId, now.AddDays(-1), cancellationToken);
        if (requestedToday >= settings.Value.MaxUploadsPerDay)
        {
            return MediaErrors.QuotaExceeded;
        }

        var expiresAt = now + settings.Value.UploadUrlLifetime;
        var asset = MediaAsset.Request(
            Guid.CreateVersion7(now),
            command.OwnerId,
            Enum.Parse<MediaPurpose>(command.Purpose),
            command.ContentType,
            command.SizeBytes,
            now,
            expiresAt);
        assets.Add(asset);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = asset.ContentType };
        return new MediaUploadDto(asset.Id, storage.CreateUploadUrl(asset.OriginalKey, asset.ContentType, expiresAt), headers, expiresAt);
    }
}
