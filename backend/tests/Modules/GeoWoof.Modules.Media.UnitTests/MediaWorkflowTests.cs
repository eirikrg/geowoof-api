using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.Modules.Media.Application;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Application.Processing;
using GeoWoof.Modules.Media.Application.Uploads;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Media.Domain;
using GeoWoof.Modules.Media.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.Media.UnitTests;

public sealed class MediaWorkflowTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly byte[] PhotoBytes = [1, 2, 3];
    private static readonly ImageVariants Clean = new(1080, 810, new byte[] { 4 }, new byte[] { 5 });

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAssets _assets = new();
    private readonly FakeStorage _storage = new();
    private readonly FakeImages _images = new();
    private readonly FakeSignal _signal = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly MediaSettings _settings = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var context = new MediaDbContextDesignTimeFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().ShouldBeFalse("Run `dotnet ef migrations add` for the Media module.");
    }

    [Fact]
    public async Task Processing_stores_the_variants_and_removes_the_original()
    {
        var asset = await ConfirmedAssetAsync();
        _images.Result = new ImageProcessingResult(Clean, RejectionCode: null);

        await Processor().ProcessAsync(asset.Id, Token);

        asset.Status.ShouldBe(MediaAssetStatus.Ready);
        asset.Width.ShouldBe(1080);
        asset.OriginalRemoved.ShouldBeTrue();
        _storage.Objects.ShouldContainKey(asset.ThumbKey);
        _storage.Objects.ShouldContainKey(asset.MediumKey);
        _storage.Objects.ShouldNotContainKey(asset.OriginalKey);
        asset.DomainEvents.OfType<MediaAssetReadyDomainEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_rejected_and_removed()
    {
        var asset = await ConfirmedAssetAsync();
        _images.Result = new ImageProcessingResult(Variants: null, MediaErrors.InvalidImageCode);

        await Processor().ProcessAsync(asset.Id, Token);

        asset.Status.ShouldBe(MediaAssetStatus.Rejected);
        asset.RejectionCode.ShouldBe(MediaErrors.InvalidImageCode);
        _storage.Objects.ShouldNotContainKey(asset.OriginalKey);
        asset.DomainEvents.OfType<MediaAssetRejectedDomainEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_original_that_could_not_be_deleted_is_removed_by_the_cleanup()
    {
        var asset = await ConfirmedAssetAsync();
        asset.Attach();
        _images.Result = new ImageProcessingResult(Clean, RejectionCode: null);
        _storage.FailDeletes = true;

        await Should.ThrowAsync<IOException>(() => Processor().ProcessAsync(asset.Id, Token));

        // The image is usable, but the original with its metadata is still in storage.
        asset.Status.ShouldBe(MediaAssetStatus.Ready);
        asset.OriginalRemoved.ShouldBeFalse();
        _storage.Objects.ShouldContainKey(asset.OriginalKey);

        _storage.FailDeletes = false;
        await Cleanup().RunAsync(Token);

        asset.OriginalRemoved.ShouldBeTrue();
        _storage.Objects.ShouldNotContainKey(asset.OriginalKey);
        _assets.All.ShouldContain(asset);
    }

    /// <summary>The attempts stay at zero here, because the real repository counts them in the database (see the integration tests).</summary>
    [Theory]
    [InlineData(0, MediaAssetStatus.Rejected)]
    [InlineData(1, MediaAssetStatus.Processing)]
    public async Task A_failed_attempt_only_gives_up_when_the_attempts_run_out(int maxAttempts, MediaAssetStatus expected)
    {
        var asset = await ConfirmedAssetAsync();
        _assets.Claim(asset.Id, _time.GetUtcNow(), _time.GetUtcNow().AddMinutes(2));
        _settings.ProcessingMaxAttempts = maxAttempts;

        await Processor().RecordFailureAsync(asset.Id, Token);

        asset.Status.ShouldBe(expected);
    }

    [Fact]
    public async Task The_cleanup_removes_expired_uploads_and_unattached_images_but_nothing_in_use()
    {
        var expired = MediaAsset.Request(Guid.CreateVersion7(), Owner, MediaPurpose.PetAvatar, "image/jpeg", 3, _time.GetUtcNow(), _time.GetUtcNow().AddMinutes(5));
        var unattached = await ConfirmedAssetAsync();
        var used = await ConfirmedAssetAsync();
        used.Attach();
        _assets.Add(expired);

        _time.Advance(TimeSpan.FromHours(25));
        await Cleanup().RunAsync(Token);

        _assets.All.ShouldBe([used]);
        _storage.Objects.ShouldNotContainKey(unattached.OriginalKey);
    }

    [Fact]
    public async Task A_released_image_is_kept_for_a_while_and_then_removed()
    {
        var asset = await ConfirmedAssetAsync();
        var api = Api();
        await api.AttachAsync(asset.Id, Owner, MediaUsage.PetAvatar, Token);
        _time.Advance(TimeSpan.FromDays(3));

        await api.ReleaseAsync(asset.Id, Owner, Token);
        await Cleanup().RunAsync(Token);
        _assets.All.ShouldContain(asset);

        _time.Advance(TimeSpan.FromHours(25));
        await Cleanup().RunAsync(Token);
        _assets.All.ShouldNotContain(asset);
    }

    [Fact]
    public async Task An_image_can_only_be_attached_by_its_owner_for_its_purpose()
    {
        var asset = await ConfirmedAssetAsync();
        var api = Api();

        (await api.AttachAsync(asset.Id, Guid.CreateVersion7(), MediaUsage.PetAvatar, Token))!.Code.ShouldBe("media.not_found");
        (await api.AttachAsync(Guid.CreateVersion7(), Owner, MediaUsage.PetAvatar, Token))!.IsNotFound.ShouldBeTrue();
        (await api.AttachAsync(asset.Id, Owner, MediaUsage.UserAvatar, Token))!.Code.ShouldBe("media.wrong_purpose");

        (await api.AttachAsync(asset.Id, Owner, MediaUsage.PetAvatar, Token)).ShouldBeNull();
        asset.IsAttached.ShouldBeTrue();
    }

    [Fact]
    public async Task An_image_that_the_processor_changes_meanwhile_is_still_attached_and_released()
    {
        var asset = await ConfirmedAssetAsync();
        asset.MarkReady(10, 10);
        var api = Api();

        _unitOfWork.ConflictsToSimulate = 2;
        (await api.AttachAsync(asset.Id, Owner, MediaUsage.PetAvatar, Token)).ShouldBeNull();
        asset.IsAttached.ShouldBeTrue();

        _unitOfWork.ConflictsToSimulate = 1;
        await api.ReleaseAsync(asset.Id, Owner, Token);
        asset.IsAttached.ShouldBeFalse();
        _unitOfWork.Discards.ShouldBe(3);
    }

    [Fact]
    public async Task Attaching_gives_up_when_the_image_keeps_changing()
    {
        var asset = await ConfirmedAssetAsync();
        _unitOfWork.ConflictsToSimulate = 3;

        await Should.ThrowAsync<ConcurrencyConflictException>(() => Api().AttachAsync(asset.Id, Owner, MediaUsage.PetAvatar, Token));
        _unitOfWork.Discards.ShouldBe(2);
    }

    [Fact]
    public async Task Processing_finishes_although_the_image_is_attached_meanwhile()
    {
        var asset = await ConfirmedAssetAsync();
        _images.Result = new ImageProcessingResult(Clean, RejectionCode: null);

        // The first save of each step (ready, and the original removed) finds the asset changed.
        _unitOfWork.ConflictsToSimulate = 1;
        _storage.BeforeDelete = () => _unitOfWork.ConflictsToSimulate = 1;
        await Processor().ProcessAsync(asset.Id, Token);

        asset.Status.ShouldBe(MediaAssetStatus.Ready);
        asset.OriginalRemoved.ShouldBeTrue();
        _storage.Objects.ShouldNotContainKey(asset.OriginalKey);
        _unitOfWork.Discards.ShouldBe(2);
    }

    [Fact]
    public async Task Unconfirmed_and_rejected_images_cannot_be_attached()
    {
        var pending = MediaAsset.Request(Guid.CreateVersion7(), Owner, MediaPurpose.PetAvatar, "image/jpeg", 3, _time.GetUtcNow(), _time.GetUtcNow().AddMinutes(5));
        _assets.Add(pending);
        var rejected = await ConfirmedAssetAsync();
        rejected.Reject(MediaErrors.InvalidImageCode);

        (await Api().AttachAsync(pending.Id, Owner, MediaUsage.PetAvatar, Token))!.Code.ShouldBe("media.not_uploaded");
        (await Api().AttachAsync(rejected.Id, Owner, MediaUsage.PetAvatar, Token))!.Code.ShouldBe("media.asset_rejected");
    }

    [Fact]
    public async Task Urls_are_only_signed_for_images_that_are_ready()
    {
        var processing = await ConfirmedAssetAsync();
        var ready = await ConfirmedAssetAsync();
        ready.MarkReady(10, 10);

        var images = await Api().GetImagesAsync([processing.Id, ready.Id, Guid.CreateVersion7()], Token);

        images.Count.ShouldBe(2);
        images[processing.Id].ThumbUrl.ShouldBeNull();
        images[ready.Id].ThumbUrl.ShouldNotBeNull();
        images[ready.Id].MediumUrl.ShouldNotBeNull();
    }

    [Fact]
    public async Task Importing_stores_the_bytes_and_starts_processing()
    {
        var id = await Api().ImportAsync(Owner, MediaUsage.PetAvatar, PhotoBytes, "image/png", Token);

        var asset = _assets.All.ShouldHaveSingleItem();
        asset.Id.ShouldBe(id);
        asset.Status.ShouldBe(MediaAssetStatus.Processing);
        _storage.Objects.ShouldContainKey(asset.OriginalKey);
        _signal.Notifications.ShouldBe(1);
    }

    [Theory]
    [InlineData("image/jpeg", 1, "PetAvatar", true)]
    [InlineData("image/webp", 15728640, "UserAvatar", true)]
    [InlineData("image/heic", 1, "PetAvatar", false)]
    [InlineData("image/gif", 1, "PetAvatar", false)]
    [InlineData("IMAGE/JPEG", 1, "PetAvatar", false)]
    [InlineData("image/jpeg", 0, "PetAvatar", false)]
    [InlineData("image/jpeg", 15728641, "PetAvatar", false)]
    [InlineData("image/jpeg", 1, "Nothing", false)]
    [InlineData("image/jpeg", 1, "0", false)]
    public void Upload_requests_are_validated(string contentType, long sizeBytes, string purpose, bool expected)
    {
        var validator = new RequestUploadCommandValidator(Options.Create(_settings));

        validator.Validate(new RequestUploadCommand(Owner, contentType, sizeBytes, purpose)).IsValid.ShouldBe(expected);
    }

    private MediaAssetProcessor Processor() => new(_assets, _storage, _images, _unitOfWork, Options.Create(_settings));

    private MediaCleanup Cleanup() => new(_assets, _storage, _unitOfWork, Options.Create(_settings), _time);

    private MediaApi Api() => new(_assets, _storage, _signal, _unitOfWork, Options.Create(_settings), _time);

    private async Task<MediaAsset> ConfirmedAssetAsync()
    {
        var now = _time.GetUtcNow();
        var asset = MediaAsset.Request(Guid.CreateVersion7(), Owner, MediaPurpose.PetAvatar, "image/jpeg", 3, now, now.AddMinutes(5));
        asset.ConfirmUpload();
        _assets.Add(asset);
        await _storage.WriteAsync(asset.OriginalKey, PhotoBytes, "image/jpeg", Token);
        return asset;
    }
}

internal sealed class InMemoryAssets : IMediaAssetRepository
{
    private readonly List<MediaAsset> _assets = [];

    public IReadOnlyList<MediaAsset> All => _assets;

    public Task<MediaAsset?> GetByIdAsync(Guid assetId, CancellationToken cancellationToken) =>
        Task.FromResult(_assets.Find(asset => asset.Id == assetId));

    public Task<IReadOnlyList<MediaAsset>> GetByIdsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>(_assets.Where(asset => assetIds.Contains(asset.Id)).ToList());

    public Task<IReadOnlyList<MediaAsset>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>(_assets.Where(asset => asset.OwnerId == ownerId).ToList());

    public Task<int> CountRequestedSinceAsync(Guid ownerId, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(_assets.Count(asset => asset.OwnerId == ownerId && asset.CreatedAt >= since));

    public Task<IReadOnlyList<Guid>> GetProcessableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(_assets.Where(asset => asset.Status == MediaAssetStatus.Processing).Select(asset => asset.Id).Take(limit).ToList());

    public Task<bool> TryClaimAsync(Guid assetId, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken) =>
        Task.FromResult(Claim(assetId, now, leaseUntil));

    public Task<IReadOnlyList<MediaAsset>> GetAbandonedAsync(DateTimeOffset expiredBefore, DateTimeOffset releasedBefore, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>(_assets
            .Where(asset =>
                (asset.Status == MediaAssetStatus.Pending && asset.UploadExpiresAt < expiredBefore)
                || (asset.Status != MediaAssetStatus.Pending && !asset.IsAttached && (asset.ReleasedAt ?? asset.CreatedAt) < releasedBefore))
            .Take(limit)
            .ToList());

    public Task<IReadOnlyList<MediaAsset>> GetWithLeftoverOriginalAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>(_assets
            .Where(asset => !asset.OriginalRemoved && asset.Status is MediaAssetStatus.Ready or MediaAssetStatus.Rejected)
            .Take(limit)
            .ToList());

    public void Add(MediaAsset asset) => _assets.Add(asset);

    public void Remove(MediaAsset asset) => _assets.Remove(asset);

    /// <summary>The real repository updates the row directly; here the attempt count stays at zero.</summary>
    public bool Claim(Guid assetId, DateTimeOffset now, DateTimeOffset leaseUntil) => _assets.Exists(asset => asset.Id == assetId);
}

internal sealed class FakeStorage : IMediaStorage
{
    public Dictionary<string, ReadOnlyMemory<byte>> Objects { get; } = new(StringComparer.Ordinal);

    public bool FailDeletes { get; set; }

    /// <summary>Runs before each deletion, to change things between the two saves of the processor.</summary>
    public Action? BeforeDelete { get; set; }

    public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Uri CreateUploadUrl(string key, string contentType, DateTimeOffset expiresAt) => new($"https://storage.test/put/{key}");

    public Uri CreateDownloadUrl(string key, DateTimeOffset expiresAt) => new($"https://storage.test/get/{key}");

    public Task<StoredObjectInfo?> GetInfoAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(key, out var content) ? new StoredObjectInfo(content.Length, "image/jpeg") : null);

    public Task<ReadOnlyMemory<byte>> ReadAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Objects[key]);

    public Task WriteAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        Objects[key] = content;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        BeforeDelete?.Invoke();
        if (FailDeletes)
        {
            throw new IOException("The storage is not available.");
        }

        foreach (var key in keys)
        {
            Objects.Remove(key);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeImages : IImageProcessor
{
    public ImageProcessingResult Result { get; set; } = new(Variants: null, MediaErrors.InvalidImageCode);

    public ImageProcessingResult Process(ReadOnlyMemory<byte> original, long maxPixels) => Result;
}

internal sealed class FakeSignal : IMediaProcessingSignal
{
    public int Notifications { get; private set; }

    public void Notify() => Notifications++;
}

internal sealed class FakeUnitOfWork : IMediaUnitOfWork
{
    /// <summary>How many of the next saves find the asset changed by someone else.</summary>
    public int ConflictsToSimulate { get; set; }

    public int Discards { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ConflictsToSimulate > 0)
        {
            ConflictsToSimulate--;
            throw new ConcurrencyConflictException("changed");
        }

        return Task.FromResult(1);
    }

    public void DiscardChanges() => Discards++;
}
