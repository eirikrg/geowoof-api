using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;

namespace GeoWoof.IntegrationTests.LiveTracking;

/// <summary>The contract of phase 7 (docs/realtime.md) end to end: real clients, the hub, Redis and the backplane.</summary>
public sealed class LiveHubTests(TestInfrastructure infrastructure)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The backplane is shared by every API of the test run, and its groups are named after the cell, so each test
    /// walks in a place of its own.
    /// </summary>
    private static (double Latitude, double Longitude) AnywhereElse() =>
        (-60 + (Random.Shared.NextDouble() * 120), -170 + (Random.Shared.NextDouble() * 340));

    private static Dictionary<string, string?> Settings(params (string Key, string Value)[] overrides) =>
        overrides
            .Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value))
            .Append(new KeyValuePair<string, string?>("LiveTracking:KeyPrefix", $"live-{Guid.NewGuid():N}:"))
            .Append(new KeyValuePair<string, string?>("LiveTracking:MinPositionInterval", "00:00:00"))
            .GroupBy(setting => setting.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

    private static Task<ApiFactory> ApiAsync(TestInfrastructure infrastructure, params (string Key, string Value)[] overrides) =>
        ApiFactory.WithFreshDatabaseAsync(infrastructure, Settings(overrides));

    private static async Task<Guid> CreatePetAsync(TestUser user)
    {
        using var created = await user.Client.PostJsonAsync("/api/v1/pets", new { name = "Luna", species = "Dog", sex = "Female", neutered = false });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task ChooseAsync(TestUser user, string visibility)
    {
        using var response = await user.Client.PatchAsJsonAsync("/api/v1/me/settings", new { liveVisibility = visibility }, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task FollowAsync(ApiFactory api, TestUser follower, TestUser followee)
    {
        await follower.VerifyEmailAsync(api);
        using var response = await follower.Client.PutJsonAsync($"/api/v1/users/{followee.Id}/follow", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task AcceptAllAsync(TestUser user)
    {
        var received = await user.Client.GetJsonAsync("/api/v1/follow-requests/received");
        foreach (var request in received.GetProperty("items").EnumerateArray())
        {
            using var accepted = await user.Client.PostAsync(new Uri($"/api/v1/follow-requests/{request.GetProperty("id").GetGuid()}/accept", UriKind.Relative), null, Token);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static async Task BefriendAsync(ApiFactory api, TestUser first, TestUser second)
    {
        await first.VerifyEmailAsync(api);
        await second.VerifyEmailAsync(api);
        foreach (var (follower, followee) in new[] { (first, second), (second, first) })
        {
            using var response = await follower.Client.PutJsonAsync($"/api/v1/users/{followee.Id}/follow", new { });
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static async Task BlockAsync(TestUser blocker, TestUser blocked)
    {
        using var response = await blocker.Client.PutJsonAsync($"/api/v1/blocks/{blocked.Id}", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task MakePrivateAsync(TestUser user)
    {
        using var response = await user.Client.PutJsonAsync("/api/v1/me/privacy", new { isPrivate = true });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_hub_needs_a_token_which_can_come_in_the_url()
    {
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var token = ana.Client.DefaultRequestHeaders.Authorization!.Parameter;
        using var anonymous = api.CreateClient();

        using var without = await anonymous.PostAsync(new Uri("/hubs/live/negotiate?negotiateVersion=1", UriKind.Relative), null, Token);
        without.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var with = await anonymous.PostAsync(new Uri($"/hubs/live/negotiate?negotiateVersion=1&access_token={token}", UriKind.Relative), null, Token);
        with.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_in_the_url_is_only_for_the_hubs()
    {
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var token = ana.Client.DefaultRequestHeaders.Authorization!.Parameter;
        using var anonymous = api.CreateClient();

        using var response = await anonymous.GetAsync(new Uri($"/api/v1/me?access_token={token}", UriKind.Relative), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Whoever_looks_sees_a_public_walker_and_what_travels_is_what_the_contract_says()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com", "ana_live", displayName: "Ana Live");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);

        (await viewer.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(0);
        (await walker.StartAsync(pet)).GetProperty("visibility").GetString().ShouldBe("Everyone");
        await walker.SendPositionAsync(latitude, longitude);

        var updated = await viewer.NextAsync("WalkerUpdated");
        updated.GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        updated.GetProperty("username").GetString().ShouldBe("ana_live");
        updated.GetProperty("displayName").GetString().ShouldBe("Ana Live");
        updated.GetProperty("avatar").ValueKind.ShouldBe(JsonValueKind.Null);
        updated.GetProperty("pets")[0].GetProperty("id").GetGuid().ShouldBe(pet);
        updated.GetProperty("pets")[0].GetProperty("name").GetString().ShouldBe("Luna");
        updated.GetProperty("latitude").GetDouble().ShouldBe(latitude);
        updated.GetProperty("longitude").GetDouble().ShouldBe(longitude);
        updated.GetProperty("updatedAt").GetString()!.ShouldEndWith("Z");

        // Whoever arrives later finds them too, and the walker does not receive their own position.
        var snapshot = await viewer.LookAtAsync(latitude, longitude);
        snapshot.GetProperty("tooWide").GetBoolean().ShouldBeFalse();
        snapshot.GetProperty("walkers")[0].GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        await walker.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task Whoever_listens_elsewhere_receives_nothing()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);

        await viewer.LookAtAsync(latitude + 1, longitude);
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);

        await viewer.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task Without_a_choice_only_friends_see_them()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        using var carlos = await TestUser.RegisterAsync(api, "carlos@example.com");
        using var dani = await TestUser.RegisterAsync(api, "dani@example.com");
        await BefriendAsync(api, ana, bea);
        await FollowAsync(api, carlos, ana);
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var friend = await LiveClient.ConnectAsync(api, bea);
        await using var follower = await LiveClient.ConnectAsync(api, carlos);
        await using var stranger = await LiveClient.ConnectAsync(api, dani);
        await friend.LookAtAsync(latitude, longitude);
        await follower.LookAtAsync(latitude, longitude);
        await stranger.LookAtAsync(latitude, longitude);

        (await walker.StartAsync(pet)).GetProperty("visibility").GetString().ShouldBe("FriendsOnly");
        await walker.SendPositionAsync(latitude, longitude);

        (await friend.NextAsync("WalkerUpdated")).GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        await follower.ShouldReceiveNothingAsync();
        await stranger.ShouldReceiveNothingAsync();
        (await follower.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(0);
        (await friend.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Everyone_on_a_private_profile_means_its_followers()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        using var carlos = await TestUser.RegisterAsync(api, "carlos@example.com");
        await ChooseAsync(ana, "Everyone");
        await MakePrivateAsync(ana);
        await FollowAsync(api, bea, ana);
        await AcceptAllAsync(ana);
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var follower = await LiveClient.ConnectAsync(api, bea);
        await using var stranger = await LiveClient.ConnectAsync(api, carlos);
        await follower.LookAtAsync(latitude, longitude);
        await stranger.LookAtAsync(latitude, longitude);

        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);

        (await follower.NextAsync("WalkerUpdated")).GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        await stranger.ShouldReceiveNothingAsync();
        (await stranger.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_block_hides_them_in_both_directions()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        using var carlos = await TestUser.RegisterAsync(api, "carlos@example.com");
        using var dani = await TestUser.RegisterAsync(api, "dani@example.com");
        await ChooseAsync(ana, "Everyone");
        await BlockAsync(ana, bea);
        await BlockAsync(carlos, ana);
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var blockedByHer = await LiveClient.ConnectAsync(api, bea);
        await using var whoBlockedHer = await LiveClient.ConnectAsync(api, carlos);
        await using var stranger = await LiveClient.ConnectAsync(api, dani);
        await blockedByHer.LookAtAsync(latitude, longitude);
        await whoBlockedHer.LookAtAsync(latitude, longitude);
        await stranger.LookAtAsync(latitude, longitude);

        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);

        (await stranger.NextAsync("WalkerUpdated")).GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        await blockedByHer.ShouldReceiveNothingAsync();
        await whoBlockedHer.ShouldReceiveNothingAsync();
        (await blockedByHer.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(0);
        (await whoBlockedHer.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_block_during_a_walk_counts_from_the_next_position()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        await viewer.LookAtAsync(latitude, longitude);
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);
        await viewer.NextAsync("WalkerUpdated");

        await BlockAsync(ana, bea);
        await walker.SendPositionAsync(latitude, longitude);

        await viewer.ShouldReceiveNothingAsync();
        (await viewer.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Choosing_nobody_stops_it_all()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        await viewer.LookAtAsync(latitude, longitude);
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);
        await viewer.NextAsync("WalkerUpdated");

        await ChooseAsync(ana, "Nobody");

        (await walker.SendPositionAsync(latitude, longitude).FailureCodeAsync()).ShouldBe("live.sharing_disabled");
        (await viewer.NextAsync("WalkerLeft")).GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        (await walker.StartAsync(pet).FailureCodeAsync()).ShouldBe("live.sharing_disabled");
        (await walker.SendPositionAsync(latitude, longitude).FailureCodeAsync()).ShouldBe("live.not_sharing");
    }

    [Fact]
    public async Task Limiting_the_visibility_tells_who_stops_seeing_them()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        await viewer.LookAtAsync(latitude, longitude);
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);
        await viewer.NextAsync("WalkerUpdated");

        await ChooseAsync(ana, "FriendsOnly");
        await walker.SendPositionAsync(latitude, longitude);

        (await viewer.NextAsync("WalkerLeft")).GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        await viewer.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task Stopping_tells_who_was_looking()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        await viewer.LookAtAsync(latitude, longitude);
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);
        await viewer.NextAsync("WalkerUpdated");

        await walker.StopAsync();

        (await viewer.NextAsync("WalkerLeft")).GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        (await viewer.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(0);
        await walker.StopAsync();
    }

    [Fact]
    public async Task Changing_cell_reaches_who_looks_at_both_in_order()
    {
        var (latitude, longitude) = AnywhereElse();
        const double Margin = 0.05;
        var nearbyLatitude = latitude + (Margin * 0.9);
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        (await viewer.LookAtAsync(latitude, longitude, Margin)).GetProperty("tooWide").GetBoolean().ShouldBeFalse();
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);
        await viewer.NextAsync("WalkerUpdated");

        await walker.SendPositionAsync(nearbyLatitude, longitude);

        await viewer.NextAsync("WalkerLeft");
        (await viewer.NextAsync("WalkerUpdated")).GetProperty("latitude").GetDouble().ShouldBe(nearbyLatitude);
    }

    [Fact]
    public async Task What_arrives_too_soon_does_not_reach_anyone()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure, ("LiveTracking:MinPositionInterval", "00:00:30"));
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        await viewer.LookAtAsync(latitude, longitude);
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);
        await viewer.NextAsync("WalkerUpdated");

        await walker.SendPositionAsync(latitude, longitude);

        await viewer.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task A_walker_who_stops_sending_disappears()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure, ("LiveTracking:PositionLifetime", "00:00:01"), ("LiveTracking:SweepInterval", "00:00:01"));
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        await viewer.LookAtAsync(latitude, longitude);
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);
        await viewer.NextAsync("WalkerUpdated");

        (await viewer.NextAsync("WalkerLeft")).GetProperty("userId").GetGuid().ShouldBe(ana.Id);

        (await viewer.LookAtAsync(latitude, longitude)).GetProperty("walkers").GetArrayLength().ShouldBe(0);
        (await walker.SendPositionAsync(latitude, longitude).FailureCodeAsync()).ShouldBe("live.not_sharing");
    }

    [Fact]
    public async Task An_area_too_wide_stops_the_events()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        await viewer.LookAtAsync(latitude, longitude);

        var wide = await viewer.LookAtAsync(latitude, longitude, margin: 2);
        wide.GetProperty("tooWide").GetBoolean().ShouldBeTrue();
        wide.GetProperty("walkers").GetArrayLength().ShouldBe(0);

        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);
        await viewer.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task Unsubscribing_stops_the_events()
    {
        var (latitude, longitude) = AnywhereElse();
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(ana);
        await using var walker = await LiveClient.ConnectAsync(api, ana);
        await using var viewer = await LiveClient.ConnectAsync(api, bea);
        await viewer.LookAtAsync(latitude, longitude);

        await viewer.Connection.InvokeAsync("UnsubscribeViewport", Token);
        await walker.StartAsync(pet);
        await walker.SendPositionAsync(latitude, longitude);

        await viewer.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task The_errors_say_their_code()
    {
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ChooseAsync(ana, "Everyone");
        var pet = await CreatePetAsync(bea);
        await using var client = await LiveClient.ConnectAsync(api, ana);

        (await client.SendPositionAsync(40, -3).FailureCodeAsync()).ShouldBe("live.not_sharing");
        (await client.StartAsync(pet).FailureCodeAsync()).ShouldBe("live.pet_not_owned");
        (await client.Connection.InvokeAsync("StartSharing", new { walkId = Guid.CreateVersion7(), petIds = Array.Empty<Guid>() }, Token).FailureCodeAsync())
            .ShouldBe("live.invalid_request");
        (await client.Connection.InvokeAsync("SubscribeViewport", new { north = 91, south = 40, east = -3, west = -4 }, Token).FailureCodeAsync())
            .ShouldBe("live.invalid_viewport");

        var own = await CreatePetAsync(ana);
        await client.StartAsync(own);
        (await client.SendPositionAsync(91, -3).FailureCodeAsync()).ShouldBe("live.invalid_position");
    }

    [Fact]
    public async Task Too_many_calls_in_a_minute_are_refused()
    {
        using var api = await ApiAsync(infrastructure, ("LiveTracking:SubscribePermitsPerMinute", "2"));
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        await using var client = await LiveClient.ConnectAsync(api, ana);
        var codes = new List<string>();

        // Five calls: even if the minute changes in the middle, one of the two windows goes over.
        for (var i = 0; i < 5; i++)
        {
            try
            {
                await client.LookAtAsync(10, 10);
            }
            catch (Microsoft.AspNetCore.SignalR.HubException failure)
            {
                codes.Add(failure.Message.Split(": ")[^1]);
            }
        }

        codes.ShouldNotBeEmpty();
        codes.ShouldAllBe(code => code == "rate_limit.exceeded");
    }

    [Fact]
    public async Task A_person_with_too_many_connections_loses_the_last_one()
    {
        using var api = await ApiAsync(infrastructure, ("LiveTracking:MaxConnectionsPerUser", "1"));
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        await using var first = await LiveClient.ConnectAsync(api, ana);

        // A call is only answered after the connection has been counted.
        await first.LookAtAsync(10, 10);
        await using var second = LiveClient.Create(api, ana);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            await second.Connection.StartAsync(Token);
        }
        catch (Exception failure) when (failure is IOException or OperationCanceledException or InvalidOperationException)
        {
            // It may be closed before the handshake ends: what matters is that it does not stay open.
            closed.TrySetResult();
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
        second.Connection.State.ShouldBe(HubConnectionState.Disconnected);
        first.Connection.State.ShouldBe(HubConnectionState.Connected);
        await first.LookAtAsync(10, 10);
    }

    [Fact]
    public async Task The_settings_start_empty_and_can_be_changed_one_at_a_time()
    {
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");

        var initial = await ana.Client.GetJsonAsync("/api/v1/me/settings");
        initial.GetProperty("liveVisibility").ValueKind.ShouldBe(JsonValueKind.Null);
        initial.GetProperty("hideStartDistanceM").GetInt32().ShouldBe(200);

        using var visibility = await ana.Client.PatchAsJsonAsync("/api/v1/me/settings", new { liveVisibility = "Everyone" }, Token);
        visibility.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var distance = await ana.Client.PatchAsJsonAsync("/api/v1/me/settings", new { hideStartDistanceM = 500 }, Token);
        var changed = await distance.JsonAsync();
        changed.GetProperty("liveVisibility").GetString().ShouldBe("Everyone");
        changed.GetProperty("hideStartDistanceM").GetInt32().ShouldBe(500);

        var read = await ana.Client.GetJsonAsync("/api/v1/me/settings");
        read.GetProperty("liveVisibility").GetString().ShouldBe("Everyone");
        read.GetProperty("hideStartDistanceM").GetInt32().ShouldBe(500);
    }

    [Theory]
    [InlineData("{\"liveVisibility\":\"Martians\"}")]
    [InlineData("{\"liveVisibility\":\"everyone\"}")]
    [InlineData("{\"hideStartDistanceM\":300}")]
    public async Task Settings_that_do_not_exist_are_refused(string body)
    {
        using var api = await ApiAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await ana.Client.PatchAsync(new Uri("/api/v1/me/settings", UriKind.Relative), content, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.JsonAsync()).GetProperty("code").GetString().ShouldBe("validation.failed");
    }
}
