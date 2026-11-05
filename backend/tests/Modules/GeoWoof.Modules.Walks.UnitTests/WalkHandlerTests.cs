using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Social.Contracts;
using GeoWoof.Modules.Walks.Application;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Application.Events;
using GeoWoof.Modules.Walks.Application.Walks;
using GeoWoof.Modules.Walks.Domain;
using GeoWoof.Modules.Walks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.Walks.UnitTests;

public sealed class WalkHandlerTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Bea = Guid.CreateVersion7();
    private static readonly Guid Luna = Guid.CreateVersion7();
    private static readonly Guid Toby = Guid.CreateVersion7();
    private static readonly Guid Mishi = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryWalks _walks = new();
    private readonly FakePets _pets = new();
    private readonly FakeMedia _media = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeRelationships _relationships = new();
    private readonly FakeIdentity _identity = new();

    public WalkHandlerTests()
    {
        _pets.Owners[Luna] = Ana;
        _pets.Owners[Toby] = Ana;
        _pets.Owners[Mishi] = Bea;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WalkInput Input() => new(
        WalkTestData.Start,
        WalkTestData.Start.AddMinutes(10),
        "Europe/Madrid",
        [Luna],
        "Friends",
        "Por el Retiro",
        [],
        [ToDto(WalkTestData.North(11, stepMs: 8000))],
        new WalkClientStats(120, 80),
        WalkTestData.Start.AddMinutes(11));

    private static WalkSegmentDto ToDto(WalkSegment segment) => new(segment.Polyline, segment.TimeOffsetsMs);

    private PutWalkCommandHandler Put() => new(_walks, _pets, _media, _unitOfWork, _time, NullLogger<PutWalkCommandHandler>.Instance);

    private Task<BuildingBlocks.Domain.Results.Result<PutWalkResult>> PutAsync(Guid id, WalkInput input, Guid? owner = null) =>
        Put().Handle(new PutWalkCommand(owner ?? Ana, id, input), Token);

    private Task<BuildingBlocks.Domain.Results.Result<WalkDto>> GetAsync(Guid viewer, Guid id) =>
        new GetWalkQueryHandler(_walks, _identity, _relationships, _media).Handle(new GetWalkQuery(viewer, id), Token);

    private Task<BuildingBlocks.Domain.Results.Result<BuildingBlocks.Application.Paging.PagedResponse<WalkSummaryDto>>> GetOfUserAsync(Guid viewer, Guid user) =>
        new GetUserWalksQueryHandler(_walks, _identity, _relationships, _media).Handle(new GetUserWalksQuery(viewer, user, null, null), Token);

    /// <summary>Goes north for about 1 km, so that it has something outside the hidden zones.</summary>
    private async Task<Guid> SaveLongWalkAsync(string visibility)
    {
        var id = Guid.CreateVersion7();
        var input = Input() with { Visibility = visibility, Segments = [ToDto(WalkTestData.North(91, stepMs: 4000))] };
        (await PutAsync(id, input)).IsSuccess.ShouldBeTrue();
        return id;
    }

    [Theory]
    [InlineData("Everyone", true, true)]
    [InlineData("Friends", true, false)]
    [InlineData("OnlyMe", false, false)]
    public async Task A_walk_is_seen_according_to_its_visibility(string visibility, bool friendSees, bool strangerSees)
    {
        var id = await SaveLongWalkAsync(visibility);
        var carlos = Guid.CreateVersion7();
        _relationships.Befriend(Ana, Bea);

        (await GetAsync(Ana, id)).IsSuccess.ShouldBeTrue();
        (await GetAsync(Bea, id)).IsSuccess.ShouldBe(friendSees);
        (await GetAsync(carlos, id)).IsSuccess.ShouldBe(strangerSees);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_block_in_either_direction_hides_a_walk_even_from_a_friend(bool anaBlocks)
    {
        var id = await SaveLongWalkAsync("Everyone");
        _identity.Add(Ana, "ana");
        _relationships.Befriend(Ana, Bea);
        _relationships.Block(anaBlocks ? Ana : Bea, anaBlocks ? Bea : Ana);

        (await GetAsync(Bea, id)).Error.ShouldBe(WalksErrors.NotFound);
        (await GetOfUserAsync(Bea, Ana)).Error.ShouldBe(WalksErrors.UserNotFound);
    }

    [Fact]
    public async Task Only_the_owner_gets_the_whole_route()
    {
        var id = await SaveLongWalkAsync("Everyone");
        var whole = (await GetAsync(Ana, id)).Value;

        var other = (await GetAsync(Bea, id)).Value;

        whole.Segments.Single().TimeOffsetsMs.Count.ShouldBe(91);
        other.Segments.Single().TimeOffsetsMs.Count.ShouldBeLessThan(91);
        other.Segments.Single().TimeOffsetsMs[0].ShouldBeGreaterThan(0);
        other.PreviewPolylines.ShouldNotBe(whole.PreviewPolylines);
        other.DistanceM.ShouldBe(whole.DistanceM);
    }

    [Fact]
    public async Task The_list_of_a_person_shows_what_each_viewer_can_see_with_the_route_cut()
    {
        _identity.Add(Ana, "ana");
        await SaveLongWalkAsync("Everyone");
        _time.Advance(TimeSpan.FromMinutes(1));
        await SaveLongWalkAsync("Friends");
        _time.Advance(TimeSpan.FromMinutes(1));
        await SaveLongWalkAsync("OnlyMe");
        var carlos = Guid.CreateVersion7();
        _relationships.Befriend(Ana, Bea);

        var mine = (await GetOfUserAsync(Ana, Ana)).Value.Items;
        var friend = (await GetOfUserAsync(Bea, Ana)).Value.Items;
        var stranger = (await GetOfUserAsync(carlos, Ana)).Value.Items;

        mine.Count.ShouldBe(3);
        friend.Count.ShouldBe(2);
        friend.ShouldAllBe(walk => walk.Visibility != "OnlyMe");
        stranger.Single().Visibility.ShouldBe("Everyone");
        stranger.Single().PreviewPolylines.ShouldNotBe(mine[0].PreviewPolylines);
    }

    [Fact]
    public async Task A_follower_who_is_not_a_friend_only_sees_what_is_for_everyone()
    {
        _identity.Add(Ana, "ana");
        var everyone = await SaveLongWalkAsync("Everyone");
        _time.Advance(TimeSpan.FromMinutes(1));
        var friends = await SaveLongWalkAsync("Friends");
        _relationships.Follow(Bea, Ana);

        (await GetAsync(Bea, everyone)).IsSuccess.ShouldBeTrue();
        (await GetAsync(Bea, friends)).Error.ShouldBe(WalksErrors.NotFound);
        (await GetOfUserAsync(Bea, Ana)).Value.Items.Single().Id.ShouldBe(everyone);
    }

    [Fact]
    public async Task A_private_profile_only_shows_its_walks_to_its_followers()
    {
        _identity.Add(Ana, "ana", isPrivate: true);
        var everyone = await SaveLongWalkAsync("Everyone");
        var carlos = Guid.CreateVersion7();
        _relationships.Follow(Bea, Ana);

        (await GetAsync(carlos, everyone)).Error.ShouldBe(WalksErrors.NotFound);
        (await GetOfUserAsync(carlos, Ana)).Error.ShouldBe(WalksErrors.ProfilePrivate);
        (await GetAsync(Bea, everyone)).IsSuccess.ShouldBeTrue();
        (await GetOfUserAsync(Bea, Ana)).Value.Items.Count.ShouldBe(1);
        (await GetOfUserAsync(Ana, Ana)).Value.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_totals_of_a_person_only_count_what_the_viewer_can_see()
    {
        _identity.Add(Ana, "ana");
        await SaveLongWalkAsync("Everyone");
        _time.Advance(TimeSpan.FromMinutes(1));
        await SaveLongWalkAsync("Friends");
        _time.Advance(TimeSpan.FromMinutes(1));
        await SaveLongWalkAsync("OnlyMe");
        var carlos = Guid.CreateVersion7();
        _relationships.Befriend(Ana, Bea);
        var handler = new GetUserWalkStatsQueryHandler(_walks, _identity, _relationships, _time);

        (await handler.Handle(new GetUserWalkStatsQuery(Ana, Ana), Token)).Value.WalkCount.ShouldBe(3);
        (await handler.Handle(new GetUserWalkStatsQuery(Bea, Ana), Token)).Value.WalkCount.ShouldBe(2);
        (await handler.Handle(new GetUserWalkStatsQuery(carlos, Ana), Token)).Value.WalkCount.ShouldBe(1);
    }

    [Fact]
    public async Task The_totals_follow_the_same_rules_as_the_list()
    {
        _identity.Add(Ana, "ana", isPrivate: true);
        var carlos = Guid.CreateVersion7();
        _relationships.Block(Bea, Ana);
        var handler = new GetUserWalkStatsQueryHandler(_walks, _identity, _relationships, _time);

        (await handler.Handle(new GetUserWalkStatsQuery(carlos, Ana), Token)).Error.ShouldBe(WalksErrors.ProfilePrivate);
        (await handler.Handle(new GetUserWalkStatsQuery(Bea, Ana), Token)).Error.ShouldBe(WalksErrors.UserNotFound);
        (await handler.Handle(new GetUserWalkStatsQuery(Bea, Guid.CreateVersion7()), Token)).Error.ShouldBe(WalksErrors.UserNotFound);
    }

    [Fact]
    public async Task The_list_of_someone_who_does_not_exist_is_not_found()
    {
        (await GetOfUserAsync(Bea, Guid.CreateVersion7())).Error.ShouldBe(WalksErrors.UserNotFound);
    }

    [Fact]
    public async Task A_bad_cursor_in_the_list_of_a_person_is_rejected()
    {
        _identity.Add(Ana, "ana");

        var result = await new GetUserWalksQueryHandler(_walks, _identity, _relationships, _media).Handle(new GetUserWalksQuery(Bea, Ana, null, "!!"), Token);

        result.Error.ShouldBe(WalksErrors.InvalidCursor);
    }

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var context = new WalksDbContextDesignTimeFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().ShouldBeFalse("Run `dotnet ef migrations add` for the Walks module.");
    }

    [Fact]
    public async Task A_new_walk_is_created_with_the_numbers_of_the_server()
    {
        var id = Guid.CreateVersion7();

        var result = await PutAsync(id, Input());

        result.Value.Created.ShouldBeTrue();
        var walk = result.Value.Walk;
        walk.Id.ShouldBe(id);
        walk.OwnerId.ShouldBe(Ana);
        walk.DistanceM.ShouldBe(111);
        walk.DurationS.ShouldBe(600);
        walk.MovingTimeS.ShouldBe(80);
        walk.Visibility.ShouldBe("Friends");
        walk.Note.ShouldBe("Por el Retiro");
        walk.PreviewPolylines.Count.ShouldBe(1);
        walk.Segments.Count.ShouldBe(1);
        _unitOfWork.Saves.ShouldBe(1);
        var recorded = _walks.All.ShouldHaveSingleItem().DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<WalkRecordedDomainEvent>();
        recorded.PetIds.ShouldBe([Luna]);
        recorded.DistanceM.ShouldBe(111);
    }

    [Fact]
    public async Task Repeating_an_upload_does_not_duplicate_anything()
    {
        var id = Guid.CreateVersion7();
        await PutAsync(id, Input());

        var again = await PutAsync(id, Input());

        again.Value.Created.ShouldBeFalse();
        _walks.All.ShouldHaveSingleItem().DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_upload_that_loses_a_race_is_a_retry()
    {
        _unitOfWork.FailNextSaveWithDuplicate = true;
        var id = Guid.CreateVersion7();

        var result = await PutAsync(id, Input());

        result.Value.Created.ShouldBeFalse();
        _unitOfWork.Discards.ShouldBe(1);
        _walks.All.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_retry_compares_equal_whatever_the_precision_or_offset_of_its_dates()
    {
        var id = Guid.CreateVersion7();
        var input = Input() with
        {
            StartedAt = WalkTestData.Start.AddTicks(1234).ToOffset(TimeSpan.FromHours(1)),
            EndedAt = WalkTestData.Start.AddMinutes(10).AddTicks(1234),
        };

        var first = await PutAsync(id, input);

        first.Value.Walk.StartedAt.ShouldBe(WalkTestData.Start);
        first.Value.Walk.StartedAt.Offset.ShouldBe(TimeSpan.Zero);
        (await PutAsync(id, input)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_walk_of_someone_else_does_not_exist()
    {
        var id = Guid.CreateVersion7();
        await PutAsync(id, Input());

        (await PutAsync(id, Input() with { PetIds = [Mishi] }, Bea)).Error.ShouldBe(WalksErrors.NotFound);
        (await new GetWalkQueryHandler(_walks, _identity, _relationships, _media).Handle(new GetWalkQuery(Bea, id), Token)).Error.ShouldBe(WalksErrors.NotFound);
        (await new DeleteWalkCommandHandler(_walks, _media, _unitOfWork).Handle(new DeleteWalkCommand(Bea, id), Token)).Error.ShouldBe(WalksErrors.NotFound);
        _walks.All.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_route_dates_and_pets_of_a_saved_walk_cannot_change()
    {
        var id = Guid.CreateVersion7();
        await PutAsync(id, Input() with { PetIds = [Luna] });

        (await PutAsync(id, Input() with { StartedAt = WalkTestData.Start.AddMinutes(1) })).Error.ShouldBe(WalksErrors.RouteImmutable);
        (await PutAsync(id, Input() with { EndedAt = WalkTestData.Start.AddMinutes(9) })).Error.ShouldBe(WalksErrors.RouteImmutable);
        (await PutAsync(id, Input() with { Segments = [ToDto(WalkTestData.North(5))] })).Error.ShouldBe(WalksErrors.RouteImmutable);
        (await PutAsync(id, Input() with { PetIds = [Toby] })).Error.ShouldBe(WalksErrors.RouteImmutable);
        (await PutAsync(id, Input() with { PetIds = [Luna, Toby] })).Error.ShouldBe(WalksErrors.RouteImmutable);
    }

    [Fact]
    public async Task A_retry_can_still_mention_a_pet_deleted_after_the_walk_was_saved()
    {
        var id = Guid.CreateVersion7();
        await PutAsync(id, Input() with { PetIds = [Luna, Toby] });
        _pets.Owners.Remove(Toby);
        await new RemovePetFromWalksOnPetDeleted(_walks).Handle(new PetDeleted(Toby, Ana, Now), Token);

        var retry = await PutAsync(id, Input() with { PetIds = [Luna, Toby] });

        retry.Value.Walk.PetIds.ShouldBe([Luna]);
    }

    [Fact]
    public async Task Note_visibility_and_photos_can_be_edited_and_the_last_edit_wins()
    {
        var id = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();
        await PutAsync(id, Input() with { PhotoAssetIds = [first, second] });
        _media.Attached.Clear();

        var edited = await PutAsync(id, Input() with
        {
            Note = "  ",
            Visibility = "OnlyMe",
            PhotoAssetIds = [third, second],
            UpdatedAt = WalkTestData.Start.AddHours(1),
        });

        edited.Value.Created.ShouldBeFalse();
        edited.Value.Walk.Note.ShouldBeNull();
        edited.Value.Walk.Visibility.ShouldBe("OnlyMe");
        edited.Value.Walk.Photos.Select(photo => photo.AssetId).ShouldBe([third, second]);
        _media.Attached.ShouldBe([(third, Ana, MediaUsage.WalkPhoto)]);
        _media.Released.ShouldBe([first]);

        var late = await PutAsync(id, Input() with { Note = "Antigua", UpdatedAt = WalkTestData.Start.AddMinutes(30) });

        late.Value.Walk.Note.ShouldBeNull();
        late.Value.Walk.UpdatedAt.ShouldBe(WalkTestData.Start.AddHours(1));
        _media.Released.ShouldBe([first]);
    }

    [Fact]
    public async Task A_clock_ahead_does_not_win_over_later_edits()
    {
        var id = Guid.CreateVersion7();
        var ahead = Now.AddHours(3);

        var saved = await PutAsync(id, Input() with { UpdatedAt = ahead });
        saved.Value.Walk.UpdatedAt.ShouldBe(Now);

        _time.Advance(TimeSpan.FromMinutes(1));
        var edited = await PutAsync(id, Input() with { Note = "Corregida", UpdatedAt = Now.AddSeconds(30) });

        edited.Value.Walk.Note.ShouldBe("Corregida");
    }

    [Fact]
    public async Task Every_pet_must_belong_to_the_owner()
    {
        (await PutAsync(Guid.CreateVersion7(), Input() with { PetIds = [Luna, Mishi] })).Error.ShouldBe(WalksErrors.PetNotOwned);
        (await PutAsync(Guid.CreateVersion7(), Input() with { PetIds = [Guid.CreateVersion7()] })).Error.ShouldBe(WalksErrors.PetNotOwned);
        _walks.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_dates_of_a_walk_must_make_sense()
    {
        var start = WalkTestData.Start;
        (await PutAsync(Guid.CreateVersion7(), Input() with { EndedAt = start })).Error.ShouldBe(WalksErrors.InvalidTimeRange);
        (await PutAsync(Guid.CreateVersion7(), Input() with { EndedAt = start.AddSeconds(-1) })).Error.ShouldBe(WalksErrors.InvalidTimeRange);
        (await PutAsync(Guid.CreateVersion7(), Input() with { EndedAt = start.AddHours(12).AddSeconds(1) })).Error.ShouldBe(WalksErrors.TooLong);

        var future = Input() with { StartedAt = Now.AddMinutes(-5), EndedAt = Now.AddMinutes(6) };
        (await PutAsync(Guid.CreateVersion7(), future)).Error.ShouldBe(WalksErrors.InvalidTimeRange);

        var withinTheMargin = Input() with { StartedAt = Now.AddMinutes(-5), EndedAt = Now.AddMinutes(5) };
        (await PutAsync(Guid.CreateVersion7(), withinTheMargin)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_broken_route_is_not_saved()
    {
        var result = await PutAsync(Guid.CreateVersion7(), Input() with { Segments = [new WalkSegmentDto("_", [0])] });

        result.Error.ShouldBe(WalksErrors.InvalidTrack);
        _unitOfWork.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task A_photo_that_cannot_be_attached_stops_the_walk()
    {
        _media.AttachFailure = new MediaAttachFailure("media.wrong_purpose", "Wrong", IsNotFound: false);

        var wrong = await PutAsync(Guid.CreateVersion7(), Input() with { PhotoAssetIds = [Guid.CreateVersion7()] });

        wrong.Error.Code.ShouldBe("media.wrong_purpose");
        _walks.All.ShouldBeEmpty();

        _media.AttachFailure = new MediaAttachFailure("media.not_found", "Missing", IsNotFound: true);
        var missing = await PutAsync(Guid.CreateVersion7(), Input() with { PhotoAssetIds = [Guid.CreateVersion7()] });

        missing.Error.Code.ShouldBe("media.not_found");
    }

    [Fact]
    public async Task Deleting_a_walk_releases_its_photos()
    {
        var id = Guid.CreateVersion7();
        var photo = Guid.CreateVersion7();
        await PutAsync(id, Input() with { PhotoAssetIds = [photo] });
        var walk = _walks.All[0];
        walk.ClearDomainEvents();

        var result = await new DeleteWalkCommandHandler(_walks, _media, _unitOfWork).Handle(new DeleteWalkCommand(Ana, id), Token);

        result.IsSuccess.ShouldBeTrue();
        _walks.All.ShouldBeEmpty();
        _media.Released.ShouldBe([photo]);
        walk.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<WalkDeletedDomainEvent>();
    }

    [Fact]
    public async Task Lists_go_from_newest_to_oldest_page_by_page()
    {
        var ids = new List<Guid>();
        for (var day = 0; day < 5; day++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            var start = WalkTestData.Start.AddDays(-day);
            await PutAsync(id, Input() with { StartedAt = start, EndedAt = start.AddMinutes(10) });
        }

        var handler = new GetWalksQueryHandler(_walks, _pets, _media);
        var first = (await handler.Handle(new GetWalksQuery(Ana, null, 2, null), Token)).Value;
        var second = (await handler.Handle(new GetWalksQuery(Ana, null, 2, first.NextCursor), Token)).Value;
        var third = (await handler.Handle(new GetWalksQuery(Ana, null, 2, second.NextCursor), Token)).Value;

        first.Items.Select(walk => walk.Id).ShouldBe(ids.Take(2));
        second.Items.Select(walk => walk.Id).ShouldBe(ids.Skip(2).Take(2));
        third.Items.Select(walk => walk.Id).ShouldBe(ids.Skip(4));
        third.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData("no es un cursor")]
    [InlineData("AAAA")]
    public async Task A_cursor_that_makes_no_sense_is_rejected(string cursor)
    {
        var result = await new GetWalksQueryHandler(_walks, _pets, _media).Handle(new GetWalksQuery(Ana, null, null, cursor), Token);

        result.Error.ShouldBe(WalksErrors.InvalidCursor);
    }

    [Fact]
    public async Task The_walks_and_stats_of_a_pet_are_only_for_its_owner()
    {
        await PutAsync(Guid.CreateVersion7(), Input() with { PetIds = [Luna, Toby] });
        var other = WalkTestData.Start.AddDays(-1);
        await PutAsync(Guid.CreateVersion7(), Input() with { PetIds = [Toby], StartedAt = other, EndedAt = other.AddMinutes(10) });
        var list = new GetWalksQueryHandler(_walks, _pets, _media);
        var stats = new GetWalkStatsQueryHandler(_walks, _pets, _time);

        (await list.Handle(new GetWalksQuery(Ana, Luna, null, null), Token)).Value.Items.Count.ShouldBe(1);
        (await list.Handle(new GetWalksQuery(Ana, Toby, null, null), Token)).Value.Items.Count.ShouldBe(2);
        (await stats.Handle(new GetWalkStatsQuery(Ana, Luna), Token)).Value.WalkCount.ShouldBe(1);
        (await stats.Handle(new GetWalkStatsQuery(Ana, null), Token)).Value.WalkCount.ShouldBe(2);

        (await list.Handle(new GetWalksQuery(Bea, Luna, null, null), Token)).Error.ShouldBe(WalksErrors.NotPetOwner);
        (await stats.Handle(new GetWalkStatsQuery(Bea, Luna), Token)).Error.ShouldBe(WalksErrors.NotPetOwner);
        (await list.Handle(new GetWalksQuery(Ana, Guid.CreateVersion7(), null, null), Token)).Error.ShouldBe(WalksErrors.PetNotFound);
        (await stats.Handle(new GetWalkStatsQuery(Ana, Guid.CreateVersion7()), Token)).Error.ShouldBe(WalksErrors.PetNotFound);
    }

    [Fact]
    public async Task The_stats_add_up_every_walk()
    {
        await PutAsync(Guid.CreateVersion7(), Input());
        var other = WalkTestData.Start.AddDays(-7);
        await PutAsync(Guid.CreateVersion7(), Input() with { StartedAt = other, EndedAt = other.AddMinutes(20) });

        var stats = (await new GetWalkStatsQueryHandler(_walks, _pets, _time).Handle(new GetWalkStatsQuery(Ana, null), Token)).Value;

        stats.ShouldBe(new WalkStatsDto(WalkCount: 2, DistanceM: 222, DurationS: 1800, MovingTimeS: 160, WeeklyStreak: 2));
        (await new GetWalkStatsQueryHandler(_walks, _pets, _time).Handle(new GetWalkStatsQuery(Bea, null), Token)).Value
            .ShouldBe(new WalkStatsDto(0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Deleting_an_account_deletes_its_walks()
    {
        await PutAsync(Guid.CreateVersion7(), Input());
        await PutAsync(Guid.CreateVersion7(), Input() with { PetIds = [Mishi] }, Bea);
        var walkOfAna = _walks.All.Single(walk => walk.OwnerId == Ana);
        walkOfAna.ClearDomainEvents();

        await new DeleteWalksOnUserDeleted(_walks).Handle(new UserDeleted(Ana, Now), Token);

        _walks.All.ShouldHaveSingleItem().OwnerId.ShouldBe(Bea);
        walkOfAna.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<WalkDeletedDomainEvent>();
    }

    [Fact]
    public async Task Deleting_a_pet_keeps_its_walks()
    {
        await PutAsync(Guid.CreateVersion7(), Input() with { PetIds = [Luna, Toby] });

        await new RemovePetFromWalksOnPetDeleted(_walks).Handle(new PetDeleted(Luna, Ana, Now), Token);
        await new RemovePetFromWalksOnPetDeleted(_walks).Handle(new PetDeleted(Toby, Ana, Now), Token);

        _walks.All.ShouldHaveSingleItem().Pets.ShouldBeEmpty();
    }

    public static TheoryData<string, WalkInput> InvalidInputs => new()
    {
        { "Visibility", Input() with { Visibility = "Nobody" } },
        { "Visibility", Input() with { Visibility = null! } },
        { "Note", Input() with { Note = new string('a', Walk.NoteMaxLength + 1) } },
        { "TimeZone", Input() with { TimeZone = "Marte/Olympus" } },
        { "TimeZone", Input() with { TimeZone = null! } },
        { "PetIds", Input() with { PetIds = [] } },
        { "PetIds", Input() with { PetIds = null! } },
        { "PetIds", Input() with { PetIds = [Luna, Luna] } },
        { "PetIds", Input() with { PetIds = Enumerable.Range(0, Walk.MaxPets + 1).Select(_ => Guid.CreateVersion7()).ToList() } },
        { "PhotoAssetIds", Input() with { PhotoAssetIds = Enumerable.Range(0, Walk.MaxPhotos + 1).Select(_ => Guid.CreateVersion7()).ToList() } },
        { "PhotoAssetIds", Input() with { PhotoAssetIds = [Luna, Luna] } },
        { "Segments", Input() with { Segments = null! } },
        { "UpdatedAt", Input() with { UpdatedAt = default } },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void The_fields_are_validated(string field, WalkInput input)
    {
        var result = new PutWalkCommandValidator().Validate(new PutWalkCommand(Ana, Guid.CreateVersion7(), input));

        result.Errors.Select(error => error.PropertyName.TrimStart('.')).ShouldContain(field);
    }

    [Fact]
    public void A_correct_walk_passes_validation()
    {
        var result = new PutWalkCommandValidator().Validate(new PutWalkCommand(Ana, Guid.CreateVersion7(), Input()));

        result.IsValid.ShouldBeTrue();
    }
}

internal sealed class InMemoryWalks : IWalkRepository
{
    private readonly List<Walk> _walks = [];

    public IReadOnlyList<Walk> All => _walks;

    public Task<Walk?> GetByIdAsync(Guid walkId, CancellationToken cancellationToken) => Task.FromResult(_walks.Find(walk => walk.Id == walkId));

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<WalkSegment>>> GetTracksAsync(IReadOnlyCollection<Guid> walkIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<WalkSegment>>>(_walks.Where(walk => walkIds.Contains(walk.Id)).ToDictionary(walk => walk.Id, walk => walk.Segments));

    public Task<IReadOnlyList<WalkSummaryRow>> GetPageAsync(Guid ownerId, Guid? petId, IReadOnlyCollection<ContentVisibility>? visibilities, WalkPageCursor? after, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WalkSummaryRow>>(Of(ownerId, petId)
            .Where(walk => visibilities is null || visibilities.Contains(walk.Visibility))
            .OrderByDescending(walk => walk.StartedAt)
            .ThenByDescending(walk => walk.Id)
            .Where(walk => after is null || walk.StartedAt < after.StartedAt || (walk.StartedAt == after.StartedAt && walk.Id.CompareTo(after.Id) < 0))
            .Take(take)
            .Select(walk => new WalkSummaryRow(
                walk.Id,
                walk.OwnerId,
                walk.StartedAt,
                walk.EndedAt,
                walk.TimeZone,
                walk.Pets.Select(pet => pet.PetId).ToList(),
                walk.Visibility,
                walk.Note,
                walk.DistanceM,
                walk.DurationS,
                walk.MovingTimeS,
                walk.AverageSpeedMps,
                walk.PaceSecondsPerKm,
                walk.PreviewPolylines,
                walk.PhotoAssetIds,
                walk.UpdatedAt))
            .ToList());

    public Task<IReadOnlyList<WalkStatsRow>> GetStatsRowsAsync(Guid ownerId, Guid? petId, IReadOnlyCollection<ContentVisibility>? visibilities, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WalkStatsRow>>(Of(ownerId, petId)
            .Where(walk => visibilities is null || visibilities.Contains(walk.Visibility))
            .Select(walk => new WalkStatsRow(walk.StartedAt, walk.TimeZone, walk.DistanceM, walk.DurationS, walk.MovingTimeS))
            .ToList());

    public Task<IReadOnlyList<Walk>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Walk>>(_walks.Where(walk => walk.OwnerId == ownerId).ToList());

    public Task<IReadOnlyList<Walk>> GetByPetAsync(Guid petId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Walk>>(_walks.Where(walk => walk.Pets.Any(pet => pet.PetId == petId)).ToList());

    public void Add(Walk walk, WalkTrack track) => _walks.Add(walk);

    public void Remove(Walk walk) => _walks.Remove(walk);

    private IEnumerable<Walk> Of(Guid ownerId, Guid? petId) =>
        _walks.Where(walk => walk.OwnerId == ownerId && (petId is null || walk.Pets.Any(pet => pet.PetId == petId)));
}

internal sealed class FakePets : IPetsApi
{
    public Dictionary<Guid, Guid> Owners { get; } = [];

    public Task<Guid?> FindOwnerAsync(Guid petId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(Owners.TryGetValue(petId, out var owner) ? owner : null);

    public Task<IReadOnlyList<Guid>> GetPetIdsAsync(Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Owners.Where(pair => pair.Value == ownerId).Select(pair => pair.Key).ToList());

    public Task<IReadOnlyDictionary<Guid, PetSummary>> GetPetsAsync(IReadOnlyCollection<Guid> petIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PetSummary>>(new Dictionary<Guid, PetSummary>());
}

internal sealed class FakeMedia : IMediaApi
{
    public List<(Guid AssetId, Guid OwnerId, MediaUsage Usage)> Attached { get; } = [];

    public List<Guid> Released { get; } = [];

    public MediaAttachFailure? AttachFailure { get; set; }

    public Task<MediaAttachFailure?> AttachAsync(Guid assetId, Guid ownerId, MediaUsage usage, CancellationToken cancellationToken)
    {
        if (AttachFailure is null)
        {
            Attached.Add((assetId, ownerId, usage));
        }

        return Task.FromResult(AttachFailure);
    }

    public Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken)
    {
        Released.Add(assetId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, MediaImage>> GetImagesAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, MediaImage>>(assetIds.ToDictionary(id => id, id => new MediaImage(id, "thumb", "medium")));

    public Task<Guid> ImportAsync(Guid ownerId, MediaUsage usage, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class FakeUnitOfWork : IWalksUnitOfWork
{
    public int Saves { get; private set; }

    public int Discards { get; private set; }

    public bool FailNextSaveWithDuplicate { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSaveWithDuplicate)
        {
            FailNextSaveWithDuplicate = false;
            throw new UniqueConstraintViolationException("pk_walks", "Duplicate.", new InvalidOperationException());
        }

        Saves++;
        return Task.FromResult(1);
    }

    public void DiscardChanges() => Discards++;
}

/// <summary>Who is related to whom, in the tests of a module that only knows the contract of Social.</summary>
internal sealed class FakeRelationships : IRelationshipsApi
{
    private readonly HashSet<(Guid Follower, Guid Followee)> _follows = [];
    private readonly HashSet<(Guid, Guid)> _blocks = [];

    public void Follow(Guid follower, Guid followee) => _follows.Add((follower, followee));

    public void Befriend(Guid first, Guid second)
    {
        Follow(first, second);
        Follow(second, first);
    }

    public void Block(Guid blocker, Guid blocked) => _blocks.Add((blocker, blocked));

    public Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken)
    {
        if (viewerId == targetId)
        {
            return Task.FromResult(Relationship.Self);
        }

        if (_blocks.Contains((viewerId, targetId)) || _blocks.Contains((targetId, viewerId)))
        {
            return Task.FromResult(Relationship.Blocked);
        }

        if (!_follows.Contains((viewerId, targetId)))
        {
            return Task.FromResult(Relationship.None);
        }

        return Task.FromResult(_follows.Contains((targetId, viewerId)) ? Relationship.Friend : Relationship.Follower);
    }

    public Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> hidden = _blocks
            .Where(block => block.Item1 == userId || block.Item2 == userId)
            .Select(block => block.Item1 == userId ? block.Item2 : block.Item1)
            .Distinct()
            .ToList();
        return Task.FromResult(hidden);
    }

    public Task<IReadOnlyCollection<Guid>> GetFollowingIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> following = _follows.Where(follow => follow.Follower == userId).Select(follow => follow.Followee).ToList();
        return Task.FromResult(following);
    }
}

internal sealed class FakeIdentity : IIdentityApi
{
    public Dictionary<Guid, UserSummary> Users { get; } = [];

    public void Add(Guid id, string username, bool isPrivate = false) => Users[id] = new UserSummary(id, username, username, null, null, isPrivate);

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<IReadOnlyDictionary<Guid, UserSummary>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, UserSummary> found = userIds.Distinct().Where(Users.ContainsKey).ToDictionary(id => id, id => Users[id]);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyDictionary<string, UserSummary>> GetPublicUsersByUsernameAsync(IReadOnlyCollection<string> usernames, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, UserSummary> found = Users.Values.Where(user => usernames.Contains(user.Username)).ToDictionary(user => user.Username, StringComparer.Ordinal);
        return Task.FromResult(found);
    }

    public Task<LiveSharingSettings?> GetLiveSharingAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Users.TryGetValue(userId, out var user) ? new LiveSharingSettings(null, user.IsPrivate) : null);
}
