using System.Net;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Identity;

public sealed class ProfileEndpointsTests(TestInfrastructure infrastructure)
{
    [Fact]
    public async Task A_profile_with_a_photo_is_visible_to_others_without_the_email()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        var photo = await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(400, 400), "UserAvatar");
        var assetId = photo.GetProperty("id").GetGuid();

        using var saved = await ana.Client.PutJsonAsync("/api/v1/me/profile", new { displayName = "Ana López", bio = "Paseo por el Retiro", city = "Madrid", avatarAssetId = assetId });
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);

        var own = await ana.Client.GetJsonAsync("/api/v1/me/profile");
        own.GetProperty("displayName").GetString().ShouldBe("Ana López");
        own.GetProperty("avatar").GetProperty("assetId").GetGuid().ShouldBe(assetId);

        var seenByBea = await bea.Client.GetJsonAsync($"/api/v1/users/{ana.Id}");
        seenByBea.GetProperty("username").GetString().ShouldBe("ana");
        seenByBea.GetProperty("bio").GetString().ShouldBe("Paseo por el Retiro");
        seenByBea.GetProperty("city").GetString().ShouldBe("Madrid");
        seenByBea.TryGetProperty("email", out _).ShouldBeFalse();
        var avatar = await MediaTestClient.DownloadAsync(seenByBea.GetProperty("avatar").GetProperty("thumbUrl").GetString()!);
        avatar.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Replacing_the_profile_clears_what_is_left_out_and_releases_the_photo()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var photo = await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(100, 100), "UserAvatar");
        var assetId = photo.GetProperty("id").GetGuid();
        using var first = await ana.Client.PutJsonAsync("/api/v1/me/profile", new { displayName = "Ana", bio = "Algo", city = "Madrid", avatarAssetId = assetId });

        using var second = await ana.Client.PutJsonAsync("/api/v1/me/profile", new { displayName = "Ana" });

        var profile = await second.JsonAsync();
        profile.GetProperty("bio").ValueKind.ShouldBe(JsonValueKind.Null);
        profile.GetProperty("city").ValueKind.ShouldBe(JsonValueKind.Null);
        profile.GetProperty("avatar").ValueKind.ShouldBe(JsonValueKind.Null);
        await using var scope = api.Services.CreateAsyncScope();
        var asset = await scope.ServiceProvider.GetRequiredService<IMediaAssetRepository>().GetByIdAsync(assetId, TestContext.Current.CancellationToken);
        asset!.IsAttached.ShouldBeFalse();
        asset.ReleasedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Only_valid_images_of_the_user_can_become_an_avatar()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        var petPhoto = await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(100, 100), "PetAvatar");
        var beaPhoto = await MediaTestClient.UploadReadyAsync(api, bea.Client, TestImages.Jpeg(100, 100), "UserAvatar");

        using var wrongPurpose = await ana.Client.PutJsonAsync("/api/v1/me/profile", new { displayName = "Ana", avatarAssetId = petPhoto.GetProperty("id").GetGuid() });
        using var someoneElses = await ana.Client.PutJsonAsync("/api/v1/me/profile", new { displayName = "Ana", avatarAssetId = beaPhoto.GetProperty("id").GetGuid() });

        wrongPurpose.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await wrongPurpose.JsonAsync()).GetProperty("code").GetString().ShouldBe("media.wrong_purpose");
        someoneElses.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await someoneElses.JsonAsync()).GetProperty("code").GetString().ShouldBe("media.not_found");
    }

    [Fact]
    public async Task Accounts_still_in_onboarding_are_not_public()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com", onboard: false);

        using var response = await ana.Client.GetAsync(new Uri($"/api/v1/users/{bea.Id}", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.JsonAsync()).GetProperty("code").GetString().ShouldBe("identity.user_not_found");
        (await bea.Client.GetJsonAsync("/api/v1/me/profile")).GetProperty("username").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
