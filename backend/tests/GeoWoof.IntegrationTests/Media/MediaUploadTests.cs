using System.Net;
using System.Text;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Media.Domain;
using GeoWoof.Modules.Media.Infrastructure.Persistence;
using GeoWoof.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace GeoWoof.IntegrationTests.Media;

/// <summary>The whole upload path of ADR-0010 against real S3-compatible storage (SeaweedFS).</summary>
public sealed class MediaUploadTests(TestInfrastructure infrastructure)
{
    private const string PetAvatar = "PetAvatar";

    [Fact]
    public async Task A_phone_photo_ends_up_upright_and_without_its_metadata()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var user = await TestUser.RegisterAsync(api, "ana@example.com");
        var photo = TestImages.Jpeg(1600, 1200, exifOrientation: 6);
        TestImages.Contains(photo, TestImages.SecretMarker).ShouldBeTrue();

        var assetId = await MediaTestClient.UploadAsync(api, user.Client, photo, PetAvatar);
        using var completed = await MediaTestClient.CompleteAsync(user.Client, assetId);
        completed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var ready = await MediaTestClient.WaitForStatusAsync(user.Client, assetId, "Ready");

        ready.GetProperty("purpose").GetString().ShouldBe(PetAvatar);
        ready.GetProperty("width").GetInt32().ShouldBe(810);
        ready.GetProperty("height").GetInt32().ShouldBe(1080);
        var medium = await MediaTestClient.DownloadAsync(ready.GetProperty("mediumUrl").GetString()!);
        var thumb = await MediaTestClient.DownloadAsync(ready.GetProperty("thumbUrl").GetString()!);
        foreach (var variant in new[] { medium, thumb })
        {
            TestImages.Contains(variant, TestImages.SecretMarker).ShouldBeFalse();
            TestImages.Contains(variant, "Exif").ShouldBeFalse();
            TestImages.Contains(variant, "EXIF").ShouldBeFalse();
            TestImages.Contains(variant.AsSpan(0, 12), "WEBP").ShouldBeTrue();
        }

        using var decoded = SKBitmap.Decode(thumb);
        decoded.Width.ShouldBe(240);
        decoded.Height.ShouldBe(320);

        // The original, which still had the metadata, is gone from storage.
        var storage = api.Services.GetRequiredService<IMediaStorage>();
        (await storage.GetInfoAsync($"originals/{assetId:N}", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_rejected()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var user = await TestUser.RegisterAsync(api, "ana@example.com");

        var assetId = await MediaTestClient.UploadAsync(api, user.Client, Encoding.UTF8.GetBytes("#!/bin/sh echo not an image"), PetAvatar);
        using var completed = await MediaTestClient.CompleteAsync(user.Client, assetId);
        completed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var rejected = await MediaTestClient.WaitForStatusAsync(user.Client, assetId, "Rejected");

        rejected.GetProperty("rejectionCode").GetString().ShouldBe("media.invalid_image");
        rejected.GetProperty("thumbUrl").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        var storage = api.Services.GetRequiredService<IMediaStorage>();
        await Eventually.Until(
            () => storage.GetInfoAsync($"originals/{assetId:N}", TestContext.Current.CancellationToken),
            info => info is null,
            "the rejected file is deleted");
    }

    [Fact]
    public async Task An_upload_that_does_not_match_what_was_declared_is_rejected()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var user = await TestUser.RegisterAsync(api, "ana@example.com");
        var photo = TestImages.Jpeg(100, 100);

        var assetId = await MediaTestClient.UploadAsync(api, user.Client, photo, PetAvatar, declaredSize: photo.Length + 1);
        using var completed = await MediaTestClient.CompleteAsync(user.Client, assetId);

        completed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await completed.JsonAsync()).GetProperty("code").GetString().ShouldBe("media.upload_mismatch");
        var asset = await user.Client.GetJsonAsync($"/api/v1/media/uploads/{assetId}");
        asset.GetProperty("status").GetString().ShouldBe("Rejected");
    }

    [Fact]
    public async Task Confirming_without_uploading_fails_and_confirming_twice_changes_nothing()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var user = await TestUser.RegisterAsync(api, "ana@example.com");

        using var requested = await user.Client.PostJsonAsync("/api/v1/media/uploads", new { contentType = "image/jpeg", sizeBytes = 10, purpose = PetAvatar });
        var unused = (await requested.JsonAsync()).GetProperty("assetId").GetGuid();
        using var missing = await MediaTestClient.CompleteAsync(user.Client, unused);
        missing.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await missing.JsonAsync()).GetProperty("code").GetString().ShouldBe("media.upload_missing");

        var ready = await MediaTestClient.UploadReadyAsync(api, user.Client, TestImages.Jpeg(100, 100), PetAvatar);
        using var again = await MediaTestClient.CompleteAsync(user.Client, ready.GetProperty("id").GetGuid());
        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await again.JsonAsync()).GetProperty("status").GetString().ShouldBe("Ready");
    }

    [Fact]
    public async Task An_image_does_not_exist_for_other_people()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        var ready = await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(100, 100), PetAvatar);
        var assetId = ready.GetProperty("id").GetGuid();

        using var read = await bea.Client.GetAsync(new Uri($"/api/v1/media/uploads/{assetId}", UriKind.Relative), TestContext.Current.CancellationToken);
        using var confirm = await MediaTestClient.CompleteAsync(bea.Client, assetId);

        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await read.JsonAsync()).GetProperty("code").GetString().ShouldBe("media.not_found");
        confirm.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_image_that_keeps_failing_is_given_up()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>
        {
            ["Media:ProcessingMaxAttempts"] = "2",
            ["Media:ProcessingLease"] = "00:00:00.100",
            ["Media:ProcessingPollInterval"] = "00:00:00.100",
        });
        using var user = await TestUser.RegisterAsync(api, "ana@example.com");

        // Confirmed, but its file is not in storage: every attempt to read it fails.
        var now = DateTimeOffset.UtcNow;
        var asset = MediaAsset.Request(Guid.CreateVersion7(), user.Id, MediaPurpose.PetAvatar, "image/jpeg", 10, now, now.AddMinutes(5));
        asset.ConfirmUpload();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
            context.Assets.Add(asset);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var rejected = await MediaTestClient.WaitForStatusAsync(user.Client, asset.Id, "Rejected");

        rejected.GetProperty("rejectionCode").GetString().ShouldBe("media.processing_failed");
        await using var verification = api.Services.CreateAsyncScope();
        var stored = await verification.ServiceProvider.GetRequiredService<IMediaAssetRepository>().GetByIdAsync(asset.Id, TestContext.Current.CancellationToken);
        stored!.ProcessingAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task Upload_requests_are_validated()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var user = await TestUser.RegisterAsync(api, "ana@example.com");

        using var response = await user.Client.PostJsonAsync("/api/v1/media/uploads", new { contentType = "application/pdf", sizeBytes = 20_000_000, purpose = "Nothing" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await response.JsonAsync()).GetProperty("errors");
        errors.TryGetProperty("contentType", out _).ShouldBeTrue();
        errors.TryGetProperty("sizeBytes", out _).ShouldBeTrue();
        errors.TryGetProperty("purpose", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_daily_quota_is_enforced()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?> { ["Media:MaxUploadsPerDay"] = "1" });
        using var user = await TestUser.RegisterAsync(api, "ana@example.com");
        var body = new { contentType = "image/jpeg", sizeBytes = 10, purpose = PetAvatar };

        using var first = await user.Client.PostJsonAsync("/api/v1/media/uploads", body);
        using var second = await user.Client.PostJsonAsync("/api/v1/media/uploads", body);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await second.JsonAsync()).GetProperty("code").GetString().ShouldBe("media.quota_exceeded");
    }
}
