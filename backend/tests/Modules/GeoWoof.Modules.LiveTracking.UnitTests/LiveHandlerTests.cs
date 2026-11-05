using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.LiveTracking.Application;
using GeoWoof.Modules.LiveTracking.Domain;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.LiveTracking.UnitTests;

public sealed class LiveHandlerTests
{
    private const double Latitude = 40.4153;
    private const double Longitude = -3.6844;

    /// <summary>In another cell: about 20 km to the north.</summary>
    private const double FarLatitude = 40.6;

    private static readonly string Cell = GeoHash.Encode(Latitude, Longitude);
    private static readonly string FarCell = GeoHash.Encode(FarLatitude, Longitude);

    private readonly Guid _ana = Guid.CreateVersion7();
    private readonly Guid _viewer = Guid.CreateVersion7();
    private readonly InMemoryLiveStore _store = new();
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly FakeRelationships _relationships = new();
    private readonly FakeIdentity _identity = new();
    private readonly FakePets _pets = new();
    private readonly FakeMedia _media = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
    private readonly LiveTrackingOptions _options = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private LiveAudience Audience => new(_store, _broadcaster, _relationships);

    private IOptions<LiveTrackingOptions> Options => Microsoft.Extensions.Options.Options.Create(_options);

    private StartSharingCommandHandler Start => new(_store, _identity, _pets, _media, _time);

    private UpdatePositionCommandHandler Update => new(_store, Audience, _identity, Options, _time);

    private SubscribeViewportCommandHandler Subscribe => new(_store, _broadcaster, Audience, Options, _time);

    private UpdatePositionCommand Position(double latitude = Latitude) => new(_ana, latitude, Longitude, 8, _time.GetUtcNow());

    private async Task SharingAsync(LiveVisibility? visibility = LiveVisibility.Everyone, bool isPrivate = false)
    {
        _identity.Add(_ana, visibility, isPrivate);
        (await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), [_pets.Add(_ana)]), Token)).IsSuccess.ShouldBeTrue();
    }

    private async Task ListeningAsync(Guid viewer, string connectionId, double latitude = Latitude)
    {
        var result = await Subscribe.Handle(new SubscribeViewportCommand(viewer, connectionId, latitude + 0.001, latitude - 0.001, Longitude + 0.001, Longitude - 0.001), Token);
        result.IsSuccess.ShouldBeTrue();
    }

    private async Task SendPositionAsync(double latitude = Latitude)
    {
        (await Update.Handle(Position(latitude), Token)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Starting_builds_the_card_without_a_position()
    {
        var avatar = Guid.CreateVersion7();
        var petAvatar = Guid.CreateVersion7();
        _identity.Add(_ana, LiveVisibility.Everyone, avatarAssetId: avatar);
        var pet = _pets.Add(_ana, "Luna", petAvatar);

        var result = await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), [pet]), Token);

        result.Value.Visibility.ShouldBe("Everyone");
        var walker = _store.Walker(_ana)!;
        walker.Username.ShouldBe("ana");
        walker.Avatar!.AssetId.ShouldBe(avatar);
        walker.Pets.Single().Name.ShouldBe("Luna");
        walker.Pets.Single().Avatar!.AssetId.ShouldBe(petAvatar);
        walker.Latitude.ShouldBeNull();
        _broadcaster.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_choice_it_shares_with_friends_only()
    {
        _identity.Add(_ana, visibility: null);

        var result = await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), [_pets.Add(_ana)]), Token);

        result.Value.Visibility.ShouldBe("FriendsOnly");
    }

    [Fact]
    public async Task Starting_with_nobody_or_without_an_account_is_refused()
    {
        _identity.Add(_ana, LiveVisibility.Nobody);
        var pet = _pets.Add(_ana);

        (await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), [pet]), Token)).Error.ShouldBe(LiveErrors.SharingDisabled);
        (await Start.Handle(new StartSharingCommand(_viewer, Guid.CreateVersion7(), [pet]), Token)).Error.ShouldBe(LiveErrors.SharingDisabled);
        _store.Walker(_ana).ShouldBeNull();
    }

    [Fact]
    public async Task Starting_with_a_pet_of_someone_else_is_refused()
    {
        _identity.Add(_ana, LiveVisibility.Everyone);
        var pet = _pets.Add(_viewer);

        var result = await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), [pet]), Token);

        result.Error.ShouldBe(LiveErrors.PetNotOwned);
    }

    [Fact]
    public async Task Starting_needs_a_walk_and_between_one_and_five_different_pets()
    {
        _identity.Add(_ana, LiveVisibility.Everyone);
        var pet = _pets.Add(_ana);
        var six = Enumerable.Range(0, 6).Select(_ => _pets.Add(_ana)).ToList();

        (await Start.Handle(new StartSharingCommand(_ana, Guid.Empty, [pet]), Token)).Error.ShouldBe(LiveErrors.InvalidRequest);
        (await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), []), Token)).Error.ShouldBe(LiveErrors.InvalidRequest);
        (await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), null), Token)).Error.ShouldBe(LiveErrors.InvalidRequest);
        (await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), [pet, pet]), Token)).Error.ShouldBe(LiveErrors.InvalidRequest);
        (await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), six), Token)).Error.ShouldBe(LiveErrors.InvalidRequest);
    }

    [Fact]
    public async Task Starting_again_keeps_the_position_it_had()
    {
        await SharingAsync();
        await SendPositionAsync();

        await Start.Handle(new StartSharingCommand(_ana, Guid.CreateVersion7(), [_pets.Add(_ana)]), Token);

        _store.Walker(_ana)!.Latitude.ShouldBe(Latitude);
        _store.Walker(_ana)!.Cell.ShouldBe(Cell);
    }

    [Fact]
    public async Task A_position_of_someone_public_goes_to_the_cell_except_the_blocks_and_themselves()
    {
        const string AnaConnection = "ana-1";
        const string BlockedConnection = "blocked-1";
        var blocked = Guid.CreateVersion7();
        _relationships.Set(blocked, _ana, Relationship.Blocked);
        await _store.AddConnectionAsync(_ana, AnaConnection, Token);
        await _store.AddConnectionAsync(blocked, BlockedConnection, Token);
        await SharingAsync();

        await SendPositionAsync();

        _broadcaster.Sent.ShouldBe([$"updated cell {Cell} except [{AnaConnection},{BlockedConnection}]"]);
    }

    [Theory]
    [InlineData(LiveVisibility.Everyone, true, Relationship.None, false)]
    [InlineData(LiveVisibility.Everyone, true, Relationship.Follower, true)]
    [InlineData(LiveVisibility.Everyone, true, Relationship.Friend, true)]
    [InlineData(LiveVisibility.Everyone, true, Relationship.Blocked, false)]
    [InlineData(LiveVisibility.FriendsOnly, false, Relationship.None, false)]
    [InlineData(LiveVisibility.FriendsOnly, false, Relationship.Follower, false)]
    [InlineData(LiveVisibility.FriendsOnly, false, Relationship.Friend, true)]
    [InlineData(LiveVisibility.FriendsOnly, false, Relationship.Blocked, false)]
    [InlineData(LiveVisibility.FriendsOnly, true, Relationship.Follower, false)]
    [InlineData(LiveVisibility.FriendsOnly, true, Relationship.Friend, true)]
    public async Task A_limited_position_only_goes_to_who_the_rules_allow(LiveVisibility visibility, bool isPrivate, Relationship relationship, bool arrives)
    {
        const string Connection = "viewer-1";
        _relationships.Set(_viewer, _ana, relationship);
        await ListeningAsync(_viewer, Connection);
        await SharingAsync(visibility, isPrivate);

        await SendPositionAsync();

        _broadcaster.Sent.ShouldBe([arrives ? $"updated to [{Connection}]" : "updated to []"]);
    }

    [Fact]
    public async Task A_position_never_goes_back_to_who_sends_it()
    {
        const string Connection = "ana-1";
        await ListeningAsync(_ana, Connection);
        await SharingAsync(LiveVisibility.FriendsOnly);

        await SendPositionAsync();

        _broadcaster.Sent.ShouldBe(["updated to []"]);
    }

    [Fact]
    public async Task A_position_without_sharing_says_so()
    {
        var result = await Update.Handle(Position(), Token);

        result.Error.ShouldBe(LiveErrors.NotSharing);
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public async Task A_position_outside_the_world_is_refused(double latitude, double longitude)
    {
        await SharingAsync();

        var result = await Update.Handle(new UpdatePositionCommand(_ana, latitude, longitude, 8, _time.GetUtcNow()), Token);

        result.Error.ShouldBe(LiveErrors.InvalidPosition);
    }

    [Fact]
    public async Task What_arrives_too_soon_is_ignored_without_an_error()
    {
        await SharingAsync();
        await SendPositionAsync();
        _broadcaster.Sent.Clear();

        _time.Advance(TimeSpan.FromSeconds(2));
        await SendPositionAsync(Latitude + 0.0001);
        _broadcaster.Sent.ShouldBeEmpty();
        _store.Walker(_ana)!.Latitude.ShouldBe(Latitude);

        _time.Advance(TimeSpan.FromSeconds(1));
        await SendPositionAsync(Latitude + 0.0001);
        _broadcaster.Sent.Count.ShouldBe(1);
        _store.Walker(_ana)!.Latitude.ShouldBe(Latitude + 0.0001);
    }

    [Fact]
    public async Task A_position_from_too_long_ago_is_ignored_without_an_error()
    {
        await SharingAsync();

        var old = _time.GetUtcNow() - TimeSpan.FromSeconds(31);
        var result = await Update.Handle(new UpdatePositionCommand(_ana, Latitude, Longitude, 8, old), Token);

        result.IsSuccess.ShouldBeTrue();
        _broadcaster.Sent.ShouldBeEmpty();
        _store.Walker(_ana)!.Latitude.ShouldBeNull();
    }

    [Fact]
    public async Task Changing_cell_says_goodbye_to_the_old_one_first()
    {
        await SharingAsync();
        await SendPositionAsync();
        _broadcaster.Sent.Clear();
        _time.Advance(TimeSpan.FromSeconds(5));

        await SendPositionAsync(FarLatitude);

        _broadcaster.Sent.ShouldBe([$"left cell {Cell} except []", $"updated cell {FarCell} except []"]);
    }

    [Fact]
    public async Task Moving_inside_a_cell_says_no_goodbye()
    {
        await SharingAsync();
        await SendPositionAsync();
        _broadcaster.Sent.Clear();
        _time.Advance(TimeSpan.FromSeconds(5));

        await SendPositionAsync(Latitude + 0.0001);

        _broadcaster.Sent.ShouldBe([$"updated cell {Cell} except []"]);
    }

    [Fact]
    public async Task Limiting_the_visibility_tells_those_who_stop_seeing_them_at_the_next_position()
    {
        const string Connection = "stranger-1";
        await ListeningAsync(_viewer, Connection);
        await SharingAsync();
        await SendPositionAsync();
        _broadcaster.Sent.Clear();
        _time.Advance(TimeSpan.FromSeconds(5));

        _identity.Add(_ana, LiveVisibility.FriendsOnly);
        await SendPositionAsync();

        _broadcaster.Sent.ShouldBe([$"left cell {Cell} except []", "updated to []"]);
    }

    [Fact]
    public async Task Making_the_profile_private_tells_the_strangers_at_the_next_position()
    {
        const string Connection = "stranger-1";
        await ListeningAsync(_viewer, Connection);
        await SharingAsync();
        await SendPositionAsync();
        _broadcaster.Sent.Clear();
        _time.Advance(TimeSpan.FromSeconds(5));

        _identity.Add(_ana, LiveVisibility.Everyone, isPrivate: true);
        await SendPositionAsync();

        _broadcaster.Sent.ShouldBe([$"left cell {Cell} except []", "updated to []"]);
    }

    [Fact]
    public async Task Choosing_nobody_stops_sharing_at_the_next_position()
    {
        await SharingAsync();
        await SendPositionAsync();
        _broadcaster.Sent.Clear();
        _time.Advance(TimeSpan.FromSeconds(5));

        _identity.Add(_ana, LiveVisibility.Nobody);
        var result = await Update.Handle(Position(), Token);

        result.Error.ShouldBe(LiveErrors.SharingDisabled);
        _broadcaster.Sent.ShouldBe([$"left cell {Cell} except []"]);
        _store.Walker(_ana).ShouldBeNull();
    }

    [Fact]
    public async Task A_deleted_account_stops_sharing_at_the_next_position()
    {
        await SharingAsync();
        _identity.Settings.Remove(_ana);

        var result = await Update.Handle(Position(), Token);

        result.Error.ShouldBe(LiveErrors.SharingDisabled);
        _store.Walker(_ana).ShouldBeNull();
    }

    [Fact]
    public async Task Stopping_says_goodbye_and_can_be_repeated()
    {
        var stop = new StopSharingCommandHandler(_store, Audience);
        await SharingAsync();
        await SendPositionAsync();
        _broadcaster.Sent.Clear();

        (await stop.Handle(new StopSharingCommand(_ana), Token)).IsSuccess.ShouldBeTrue();
        _broadcaster.Sent.ShouldBe([$"left cell {Cell} except []"]);
        _store.Walker(_ana).ShouldBeNull();

        (await stop.Handle(new StopSharingCommand(_ana), Token)).IsSuccess.ShouldBeTrue();
        _broadcaster.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Stopping_before_the_first_position_tells_nobody()
    {
        await SharingAsync();

        await new StopSharingCommandHandler(_store, Audience).Handle(new StopSharingCommand(_ana), Token);

        _broadcaster.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Looking_at_an_area_returns_who_the_rules_allow_and_listens_to_its_cell()
    {
        const string Connection = "viewer-1";
        await SharingAsync();
        await SendPositionAsync();

        var result = await Subscribe.Handle(new SubscribeViewportCommand(_viewer, Connection, Latitude + 0.001, Latitude - 0.001, Longitude + 0.001, Longitude - 0.001), Token);

        result.Value.TooWide.ShouldBeFalse();
        result.Value.Walkers.Single().UserId.ShouldBe(_ana);
        _broadcaster.Groups[Cell].ShouldContain(Connection);
    }

    [Fact]
    public async Task Looking_does_not_return_who_it_cannot_see_or_oneself()
    {
        await SharingAsync(LiveVisibility.FriendsOnly);
        await SendPositionAsync();

        var stranger = await Subscribe.Handle(new SubscribeViewportCommand(_viewer, "viewer-1", Latitude + 0.001, Latitude - 0.001, Longitude + 0.001, Longitude - 0.001), Token);
        var self = await Subscribe.Handle(new SubscribeViewportCommand(_ana, "ana-1", Latitude + 0.001, Latitude - 0.001, Longitude + 0.001, Longitude - 0.001), Token);

        stranger.Value.Walkers.ShouldBeEmpty();
        self.Value.Walkers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Looking_does_not_return_a_position_that_has_expired()
    {
        await SharingAsync();
        await SendPositionAsync();
        _time.Advance(TimeSpan.FromSeconds(31));

        var result = await Subscribe.Handle(new SubscribeViewportCommand(_viewer, "viewer-1", Latitude + 0.001, Latitude - 0.001, Longitude + 0.001, Longitude - 0.001), Token);

        result.Value.Walkers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Looking_elsewhere_stops_listening_to_the_previous_cell()
    {
        const string Connection = "viewer-1";
        await ListeningAsync(_viewer, Connection);

        await ListeningAsync(_viewer, Connection, FarLatitude);

        _broadcaster.Groups[Cell].ShouldNotContain(Connection);
        _broadcaster.Groups[FarCell].ShouldContain(Connection);
    }

    [Fact]
    public async Task An_area_too_wide_returns_nobody_and_stops_listening()
    {
        const string Connection = "viewer-1";
        await SharingAsync();
        await SendPositionAsync();
        await ListeningAsync(_viewer, Connection);

        var result = await Subscribe.Handle(new SubscribeViewportCommand(_viewer, Connection, 41, 40, -3, -4), Token);

        result.Value.TooWide.ShouldBeTrue();
        result.Value.Walkers.ShouldBeEmpty();
        _broadcaster.Groups[Cell].ShouldNotContain(Connection);
    }

    [Theory]
    [InlineData(91, 40, -3, -4)]
    [InlineData(41, -91, -3, -4)]
    [InlineData(41, 40, 181, -4)]
    [InlineData(41, 40, -3, -181)]
    [InlineData(40, 41, -3, -4)]
    [InlineData(double.NaN, 40, -3, -4)]
    public async Task An_area_that_makes_no_sense_is_refused(double north, double south, double east, double west)
    {
        var result = await Subscribe.Handle(new SubscribeViewportCommand(_viewer, "viewer-1", north, south, east, west), Token);

        result.Error.ShouldBe(LiveErrors.InvalidViewport);
    }

    [Fact]
    public async Task Unsubscribing_stops_listening()
    {
        const string Connection = "viewer-1";
        await ListeningAsync(_viewer, Connection);

        await new UnsubscribeViewportCommandHandler(_store, _broadcaster).Handle(new UnsubscribeViewportCommand(_viewer, Connection), Token);

        _broadcaster.Groups[Cell].ShouldNotContain(Connection);
        (await _store.GetCellListenersAsync(Cell, Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_sweep_says_goodbye_to_who_stopped_sending_and_nobody_else()
    {
        var sweep = new LiveSweep(_store, _broadcaster, _relationships, Options, _time);
        var other = Guid.CreateVersion7();
        await SharingAsync();
        await SendPositionAsync();
        _time.Advance(TimeSpan.FromSeconds(31));
        _identity.Add(other, LiveVisibility.Everyone);
        await Start.Handle(new StartSharingCommand(other, Guid.CreateVersion7(), [_pets.Add(other)]), Token);
        _broadcaster.Sent.Clear();

        await sweep.RunAsync(Token);

        _broadcaster.Sent.ShouldBe([$"left cell {Cell} except []"]);
        _store.Walker(_ana).ShouldBeNull();
        _store.Walker(other).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_sweep_of_a_walker_who_never_sent_a_position_tells_nobody()
    {
        var sweep = new LiveSweep(_store, _broadcaster, _relationships, Options, _time);
        await SharingAsync();
        _time.Advance(TimeSpan.FromSeconds(31));

        await sweep.RunAsync(Token);

        _broadcaster.Sent.ShouldBeEmpty();
        _store.Walker(_ana).ShouldBeNull();
    }

    [Fact]
    public async Task Only_one_instance_sweeps_at_a_time()
    {
        var sweep = new LiveSweep(_store, _broadcaster, _relationships, Options, _time);
        await SharingAsync();
        _time.Advance(TimeSpan.FromSeconds(31));
        _store.SweepLockIsTaken = true;

        await sweep.RunAsync(Token);

        _store.Walker(_ana).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_person_can_only_have_so_many_connections()
    {
        var connections = new LiveConnections(_store, Options);
        _options.MaxConnectionsPerUser = 2;

        (await connections.OpenAsync(_ana, "1", Token)).ShouldBeTrue();
        (await connections.OpenAsync(_ana, "2", Token)).ShouldBeTrue();
        (await connections.OpenAsync(_ana, "3", Token)).ShouldBeFalse();

        await connections.CloseAsync(_ana, "3", Token);
        await connections.CloseAsync(_ana, "2", Token);
        (await connections.OpenAsync(_ana, "4", Token)).ShouldBeTrue();
    }

    [Fact]
    public async Task Closing_a_connection_stops_it_listening()
    {
        const string Connection = "viewer-1";
        var connections = new LiveConnections(_store, Options);
        await connections.OpenAsync(_viewer, Connection, Token);
        await ListeningAsync(_viewer, Connection);

        await connections.CloseAsync(_viewer, Connection, Token);

        (await _store.GetCellListenersAsync(Cell, Token)).ShouldBeEmpty();
        (await _store.GetConnectionsAsync([_viewer], Token)).ShouldBeEmpty();
    }
}
