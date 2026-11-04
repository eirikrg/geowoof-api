using GeoWoof.Modules.Identity.Application.Profiles;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Media.Contracts;

namespace GeoWoof.Modules.Identity.UnitTests.Application;

public sealed class ProfileTests
{
    [Fact]
    public async Task The_profile_is_replaced_and_empty_values_are_cleared()
    {
        await using var host = new IdentityTestHost();
        var user = host.AddUser("ana@example.com", username: "ana");

        var edited = await host.SendAsync<ReplaceMyProfileCommand, ProfileDto>(new ReplaceMyProfileCommand(user.Id, " Ana López ", " Paseo por el Retiro ", "Madrid", null));
        var cleared = await host.SendAsync<ReplaceMyProfileCommand, ProfileDto>(new ReplaceMyProfileCommand(user.Id, "Ana", "  ", null, null));

        edited.Value.DisplayName.ShouldBe("Ana López");
        edited.Value.Bio.ShouldBe("Paseo por el Retiro");
        edited.Value.City.ShouldBe("Madrid");
        cleared.Value.Bio.ShouldBeNull();
        cleared.Value.City.ShouldBeNull();
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("Ana", 301, null)]
    [InlineData("Ana", null, 101)]
    public async Task The_fields_are_validated(string displayName, int? bioLength, int? cityLength)
    {
        await using var host = new IdentityTestHost();
        var user = host.AddUser("ana@example.com", username: "ana");
        var bio = bioLength is { } length ? new string('x', length) : null;
        var city = cityLength is { } size ? new string('x', size) : null;

        var result = await host.SendAsync<ReplaceMyProfileCommand, ProfileDto>(new ReplaceMyProfileCommand(user.Id, displayName, bio, city, null));

        result.Error.Code.ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Changing_the_avatar_attaches_the_new_one_and_releases_the_old_one()
    {
        await using var host = new IdentityTestHost();
        var user = host.AddUser("ana@example.com", username: "ana");
        Guid first = Guid.CreateVersion7(), second = Guid.CreateVersion7();

        var withFirst = await host.SendAsync<ReplaceMyProfileCommand, ProfileDto>(new ReplaceMyProfileCommand(user.Id, "Ana", null, null, first));
        await host.SendAsync<ReplaceMyProfileCommand, ProfileDto>(new ReplaceMyProfileCommand(user.Id, "Ana", null, null, first));
        await host.SendAsync<ReplaceMyProfileCommand, ProfileDto>(new ReplaceMyProfileCommand(user.Id, "Ana", null, null, second));
        var withNone = await host.SendAsync<ReplaceMyProfileCommand, ProfileDto>(new ReplaceMyProfileCommand(user.Id, "Ana", null, null, null));

        withFirst.Value.Avatar!.AssetId.ShouldBe(first);
        host.Media.Attached.ShouldBe([first, second]);
        host.Media.Released.ShouldBe([first, second]);
        withNone.Value.Avatar.ShouldBeNull();
    }

    [Fact]
    public async Task An_image_that_cannot_be_attached_keeps_the_profile_unchanged()
    {
        await using var host = new IdentityTestHost();
        var user = host.AddUser("ana@example.com", username: "ana");
        host.Media.AttachFailure = new MediaAttachFailure("media.not_found", "Missing", IsNotFound: true);

        var result = await host.SendAsync<ReplaceMyProfileCommand, ProfileDto>(new ReplaceMyProfileCommand(user.Id, "Otra", null, null, Guid.CreateVersion7()));

        result.Error.Code.ShouldBe("media.not_found");
        user.DisplayName.ShouldBe("Ana");
    }

    [Fact]
    public async Task Only_finished_accounts_have_a_public_profile()
    {
        await using var host = new IdentityTestHost();
        var finished = host.AddUser("ana@example.com", username: "ana");
        var onboarding = host.AddUser("bea@example.com");

        var visible = await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(finished.Id));
        var hidden = await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(onboarding.Id));
        var missing = await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(Guid.CreateVersion7()));
        var own = await host.QueryAsync<GetMyProfileQuery, ProfileDto>(new GetMyProfileQuery(onboarding.Id));

        visible.Value.Username.ShouldBe("ana");
        hidden.Error.ShouldBe(IdentityErrors.UserNotFound);
        missing.Error.ShouldBe(IdentityErrors.UserNotFound);
        own.Value.Username.ShouldBeNull();
    }
}
