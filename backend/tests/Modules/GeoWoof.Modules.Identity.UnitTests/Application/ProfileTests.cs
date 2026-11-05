using GeoWoof.BuildingBlocks.Application.Paging;
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

        var viewer = host.AddUser("carlos@example.com", username: "carlos").Id;
        var visible = await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(viewer, finished.Id));
        var hidden = await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(viewer, onboarding.Id));
        var missing = await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(viewer, Guid.CreateVersion7()));
        var own = await host.QueryAsync<GetMyProfileQuery, ProfileDto>(new GetMyProfileQuery(onboarding.Id));

        visible.Value.Username.ShouldBe("ana");
        hidden.Error.ShouldBe(IdentityErrors.UserNotFound);
        missing.Error.ShouldBe(IdentityErrors.UserNotFound);
        own.Value.Username.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_block_in_either_direction_hides_the_profile(bool viewerBlocks)
    {
        await using var host = new IdentityTestHost();
        var ana = host.AddUser("ana@example.com", username: "ana");
        var bea = host.AddUser("bea@example.com", username: "bea");
        host.Relationships.Block(viewerBlocks ? bea.Id : ana.Id, viewerBlocks ? ana.Id : bea.Id);

        var result = await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(bea.Id, ana.Id));

        result.Error.ShouldBe(IdentityErrors.UserNotFound);
    }

    [Fact]
    public async Task A_private_profile_only_shows_its_username_photo_and_bio_to_those_who_do_not_follow_it()
    {
        await using var host = new IdentityTestHost();
        var dani = host.AddUser("dani@example.com", username: "dani");
        dani.UpdateProfile("Dani Prieto", "Con Bimba", "Madrid", null);
        dani.SetPrivate(true);
        var stranger = host.AddUser("stranger@example.com", username: "stranger").Id;
        var follower = host.AddUser("follower@example.com", username: "follower").Id;
        host.Relationships.Follow(follower, dani.Id);

        var strangerSees = (await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(stranger, dani.Id))).Value;
        var followerSees = (await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(follower, dani.Id))).Value;
        var ownerSees = (await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(dani.Id, dani.Id))).Value;

        strangerSees.IsPrivate.ShouldBeTrue();
        strangerSees.Username.ShouldBe("dani");
        strangerSees.DisplayName.ShouldBe("dani");
        strangerSees.City.ShouldBeNull();
        strangerSees.Bio.ShouldBe("Con Bimba");
        followerSees.DisplayName.ShouldBe("Dani Prieto");
        followerSees.City.ShouldBe("Madrid");
        ownerSees.DisplayName.ShouldBe("Dani Prieto");
    }

    [Fact]
    public async Task A_public_profile_shows_everything_to_anyone()
    {
        await using var host = new IdentityTestHost();
        var ana = host.AddUser("ana@example.com", username: "ana");
        ana.UpdateProfile("Ana López", null, "Madrid", null);
        var stranger = host.AddUser("stranger@example.com", username: "stranger").Id;

        var seen = (await host.QueryAsync<GetPublicProfileQuery, ProfileDto>(new GetPublicProfileQuery(stranger, ana.Id))).Value;

        seen.IsPrivate.ShouldBeFalse();
        seen.DisplayName.ShouldBe("Ana López");
        seen.City.ShouldBe("Madrid");
    }

    [Fact]
    public async Task The_search_shows_a_private_profile_restricted_unless_you_follow_it()
    {
        await using var host = new IdentityTestHost();
        var viewer = host.AddUser("viewer@example.com", username: "viewer");
        var dani = host.AddUser("dani@example.com", username: "dani");
        dani.UpdateProfile("Ana", null, "Madrid", null);
        dani.SetPrivate(true);

        var restricted = await host.QueryAsync<SearchUsersQuery, PagedResponse<UserSummaryDto>>(new SearchUsersQuery(viewer.Id, "Ana", null, null));
        host.Relationships.Follow(viewer.Id, dani.Id);
        var full = await host.QueryAsync<SearchUsersQuery, PagedResponse<UserSummaryDto>>(new SearchUsersQuery(viewer.Id, "Ana", null, null));

        restricted.Value.Items.Single().DisplayName.ShouldBe("dani");
        restricted.Value.Items.Single().City.ShouldBeNull();
        full.Value.Items.Single().DisplayName.ShouldBe("Ana");
        full.Value.Items.Single().City.ShouldBe("Madrid");
    }

    [Fact]
    public async Task The_search_leaves_out_the_viewer_and_the_blocked_in_both_directions()
    {
        await using var host = new IdentityTestHost();
        var viewer = host.AddUser("ana@example.com", username: "ana");
        var visible = host.AddUser("visible@example.com", username: "visible");
        var blockedByViewer = host.AddUser("b1@example.com", username: "blocked_one");
        var blockedViewer = host.AddUser("b2@example.com", username: "blocked_two");
        host.Relationships.Block(viewer.Id, blockedByViewer.Id);
        host.Relationships.Block(blockedViewer.Id, viewer.Id);

        var result = await host.QueryAsync<SearchUsersQuery, PagedResponse<UserSummaryDto>>(new SearchUsersQuery(viewer.Id, "  Ana  ", null, null));

        result.Value.Items.Single().Id.ShouldBe(visible.Id);
        result.Value.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task The_search_goes_through_pages()
    {
        await using var host = new IdentityTestHost();
        var viewer = host.AddUser("viewer@example.com", username: "viewer");
        host.AddUser("a@example.com", username: "first");
        host.AddUser("b@example.com", username: "second");

        var first = await host.QueryAsync<SearchUsersQuery, PagedResponse<UserSummaryDto>>(new SearchUsersQuery(viewer.Id, "Ana", 1, null));
        var second = await host.QueryAsync<SearchUsersQuery, PagedResponse<UserSummaryDto>>(new SearchUsersQuery(viewer.Id, "Ana", 1, first.Value.NextCursor));

        first.Value.NextCursor.ShouldNotBeNull();
        second.Value.NextCursor.ShouldBeNull();
        second.Value.Items.Single().Id.ShouldNotBe(first.Value.Items.Single().Id);
    }

    [Fact]
    public async Task The_search_rejects_a_bad_cursor_and_a_text_too_short_or_too_long()
    {
        await using var host = new IdentityTestHost();
        var viewer = host.AddUser("viewer@example.com", username: "viewer");

        (await host.QueryAsync<SearchUsersQuery, PagedResponse<UserSummaryDto>>(new SearchUsersQuery(viewer.Id, "ana", null, "!!"))).Error.ShouldBe(IdentityErrors.InvalidCursor);
        new SearchUsersQueryValidator().Validate(new SearchUsersQuery(viewer.Id, " a ", null, null)).IsValid.ShouldBeFalse();
        new SearchUsersQueryValidator().Validate(new SearchUsersQuery(viewer.Id, new string('a', 51), null, null)).IsValid.ShouldBeFalse();
        new SearchUsersQueryValidator().Validate(new SearchUsersQuery(viewer.Id, "an", null, null)).IsValid.ShouldBeTrue();
    }
}
