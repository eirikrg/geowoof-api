using System.Net;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Social.Infrastructure.Persistence;
using GeoWoof.Modules.Walks.Domain;
using GeoWoof.Modules.Walks.Domain.Routes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Social;

/// <summary>The contract of phases 4 and 5 end to end: HTTP, handlers, PostgreSQL, the cache in Redis and the Outbox.</summary>
public sealed class SocialEndpointsTests(TestInfrastructure infrastructure)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> GetAsync(TestUser user, string path) =>
        user.Client.GetAsync(new Uri(path, UriKind.Relative), Token);

    private static Task<HttpResponseMessage> PostAsync(TestUser user, string path) =>
        user.Client.PostAsync(new Uri(path, UriKind.Relative), null, Token);

    private static Task<HttpResponseMessage> DeleteAsync(TestUser user, string path) =>
        user.Client.DeleteAsync(new Uri(path, UriKind.Relative), Token);

    private static async Task<JsonElement> FollowAsync(TestUser follower, TestUser followee)
    {
        using var response = await follower.Client.PutJsonAsync($"/api/v1/users/{followee.Id}/follow", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.JsonAsync();
    }

    /// <summary>Whoever follows needs a verified email.</summary>
    private static async Task BefriendAsync(ApiFactory api, TestUser first, TestUser second)
    {
        await first.VerifyEmailAsync(api);
        await second.VerifyEmailAsync(api);
        await FollowAsync(first, second);
        await FollowAsync(second, first);
    }

    private static async Task BlockAsync(TestUser blocker, TestUser blocked)
    {
        using var response = await blocker.Client.PutJsonAsync($"/api/v1/blocks/{blocked.Id}", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task MakePrivateAsync(TestUser user, bool isPrivate = true)
    {
        using var response = await user.Client.PutJsonAsync("/api/v1/me/privacy", new { isPrivate });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("isPrivate").GetBoolean().ShouldBe(isPrivate);
    }

    private static async Task<Guid> CreatePetAsync(TestUser user, string name = "Luna", string? breed = null)
    {
        using var created = await user.Client.PostJsonAsync("/api/v1/pets", new { name, species = "Dog", sex = "Female", neutered = false, breed, microchip = "941000024567890" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.JsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>Goes north for about 2,2 km: enough to have a route outside the hidden zones.</summary>
    private static async Task<Guid> PutWalkAsync(TestUser user, Guid petId, string visibility, int minutesAfterStart = 0)
    {
        var positions = Enumerable.Range(0, 201).Select(i => new GeoPoint(40.4 + (i * 0.0001), -3.7)).ToList();
        var id = Guid.CreateVersion7();
        var startedAt = Start.AddMinutes(minutesAfterStart);
        using var created = await user.Client.PutJsonAsync($"/api/v1/walks/{id}", new
        {
            startedAt,
            endedAt = startedAt.AddMinutes(30),
            timeZone = "Europe/Madrid",
            petIds = new[] { petId },
            visibility,
            note = "Por el Retiro",
            photoAssetIds = Array.Empty<Guid>(),
            segments = new[]
            {
                new { polyline = Polyline.Encode(positions), timeOffsetsMs = Enumerable.Range(0, 201).Select(i => i * 8000L).ToArray() },
            },
            updatedAt = startedAt.AddMinutes(31),
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return id;
    }

    private static async Task<string> RelationshipAsync(TestUser viewer, TestUser user) =>
        (await viewer.Client.GetJsonAsync($"/api/v1/users/{user.Id}/relationship")).GetProperty("status").GetString()!;

    private static async Task<List<string>> SearchUsersAsync(TestUser viewer, string text)
    {
        var page = await viewer.Client.GetJsonAsync($"/api/v1/search/users?q={Uri.EscapeDataString(text)}");
        return page.GetProperty("items").EnumerateArray().Select(user => user.GetProperty("username").GetString()!).ToList();
    }

    private static async Task<List<string>> SearchPetsAsync(TestUser viewer, string text)
    {
        var page = await viewer.Client.GetJsonAsync($"/api/v1/search/pets?q={Uri.EscapeDataString(text)}");
        return page.GetProperty("items").EnumerateArray().Select(pet => pet.GetProperty("name").GetString()!).ToList();
    }

    private static async Task<List<string>> VisibilitiesAsync(TestUser viewer, TestUser owner)
    {
        var page = await viewer.Client.GetJsonAsync($"/api/v1/users/{owner.Id}/walks");
        return page.GetProperty("items").EnumerateArray().Select(walk => walk.GetProperty("visibility").GetString()!).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Two_people_who_follow_each_other_are_friends_until_one_of_them_stops()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ana.VerifyEmailAsync(api);
        await bea.VerifyEmailAsync(api);
        (await RelationshipAsync(ana, bea)).ShouldBe("None");

        var followed = await FollowAsync(ana, bea);

        followed.GetProperty("status").GetString().ShouldBe("Following");
        followed.GetProperty("followsYou").GetBoolean().ShouldBeFalse();
        var seenByBea = await bea.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/relationship");
        seenByBea.GetProperty("status").GetString().ShouldBe("None");
        seenByBea.GetProperty("followsYou").GetBoolean().ShouldBeTrue();
        var counts = await bea.Client.GetJsonAsync($"/api/v1/users/{bea.Id}/counts");
        counts.GetProperty("followers").GetInt32().ShouldBe(1);
        counts.GetProperty("following").GetInt32().ShouldBe(0);
        counts.GetProperty("friends").GetInt32().ShouldBe(0);

        var back = await FollowAsync(bea, ana);

        back.GetProperty("status").GetString().ShouldBe("Friends");
        (await RelationshipAsync(ana, bea)).ShouldBe("Friends");
        (await ana.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/counts")).GetProperty("friends").GetInt32().ShouldBe(1);
        var friends = await ana.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/friends");
        var friend = friends.GetProperty("items")[0];
        friend.GetProperty("user").GetProperty("id").GetGuid().ShouldBe(bea.Id);
        friend.GetProperty("user").TryGetProperty("email", out _).ShouldBeFalse();

        using var stopped = await DeleteAsync(bea, $"/api/v1/users/{ana.Id}/follow");

        stopped.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await stopped.JsonAsync()).GetProperty("status").GetString().ShouldBe("None");
        (await RelationshipAsync(ana, bea)).ShouldBe("Following");
        (await ana.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/friends")).GetProperty("items").GetArrayLength().ShouldBe(0);

        using var scope = api.Services.CreateScope();
        var outbox = await scope.ServiceProvider.GetRequiredService<SocialDbContext>().OutboxMessages
            .Select(message => message.Type)
            .ToListAsync(Token);
        outbox.ShouldBe(["social.user_followed.v1", "social.user_followed.v1", "social.friendship_started.v1", "social.friendship_removed.v1"], ignoreOrder: true);
    }

    [Fact]
    public async Task Following_needs_a_verified_email()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");

        using var response = await ana.Client.PutJsonAsync($"/api/v1/users/{bea.Id}/follow", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(response)).ShouldBe("auth.forbidden");
    }

    [Fact]
    public async Task Following_a_private_profile_needs_a_yes_and_it_can_be_rejected_or_cancelled()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var dani = await TestUser.RegisterAsync(api, "dani@example.com");
        await ana.VerifyEmailAsync(api);
        await MakePrivateAsync(dani);

        var asked = await FollowAsync(ana, dani);

        asked.GetProperty("status").GetString().ShouldBe("Requested");
        var requestId = asked.GetProperty("requestId").GetGuid();
        (await FollowAsync(ana, dani)).GetProperty("requestId").GetGuid().ShouldBe(requestId);
        var received = await dani.Client.GetJsonAsync("/api/v1/follow-requests/received");
        received.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(requestId);
        received.GetProperty("items")[0].GetProperty("direction").GetString().ShouldBe("Received");
        (await ana.Client.GetJsonAsync("/api/v1/follow-requests/sent")).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await dani.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/relationship")).GetProperty("incomingRequestId").GetGuid().ShouldBe(requestId);

        using var notYours = await PostAsync(ana, $"/api/v1/follow-requests/{requestId}/accept");
        notYours.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(notYours)).ShouldBe("social.request_not_found");

        using var rejected = await PostAsync(dani, $"/api/v1/follow-requests/{requestId}/reject");
        rejected.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var rejectedAgain = await PostAsync(dani, $"/api/v1/follow-requests/{requestId}/reject");
        rejectedAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RelationshipAsync(ana, dani)).ShouldBe("None");
        using var tooLate = await PostAsync(dani, $"/api/v1/follow-requests/{requestId}/accept");
        tooLate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(tooLate)).ShouldBe("social.request_not_pending");

        var second = (await FollowAsync(ana, dani)).GetProperty("requestId").GetGuid();
        using var cancelled = await DeleteAsync(ana, $"/api/v1/follow-requests/{second}");
        cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await dani.Client.GetJsonAsync("/api/v1/follow-requests/received")).GetProperty("items").GetArrayLength().ShouldBe(0);

        await FollowAsync(ana, dani);
        using var unfollowed = await DeleteAsync(ana, $"/api/v1/users/{dani.Id}/follow");
        (await unfollowed.JsonAsync()).GetProperty("status").GetString().ShouldBe("None");
    }

    [Fact]
    public async Task Accepting_lets_that_person_follow_you_and_see_what_is_for_everyone()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var dani = await TestUser.RegisterAsync(api, "dani@example.com");
        await ana.VerifyEmailAsync(api);
        await MakePrivateAsync(dani);
        var petId = await CreatePetAsync(dani);
        await PutWalkAsync(dani, petId, "Everyone");
        await PutWalkAsync(dani, petId, "Friends", minutesAfterStart: 60);
        var requestId = (await FollowAsync(ana, dani)).GetProperty("requestId").GetGuid();

        using var denied = await GetAsync(ana, $"/api/v1/users/{dani.Id}/walks");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var accepted = await PostAsync(dani, $"/api/v1/follow-requests/{requestId}/accept");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = await accepted.JsonAsync();
        answer.GetProperty("status").GetString().ShouldBe("None");
        answer.GetProperty("followsYou").GetBoolean().ShouldBeTrue();

        (await RelationshipAsync(ana, dani)).ShouldBe("Following");
        (await VisibilitiesAsync(ana, dani)).ShouldBe(["Everyone"]);

        await dani.VerifyEmailAsync(api);
        await FollowAsync(dani, ana);
        (await RelationshipAsync(ana, dani)).ShouldBe("Friends");
        (await VisibilitiesAsync(ana, dani)).ShouldBe(["Everyone", "Friends"]);
    }

    [Fact]
    public async Task A_profile_that_becomes_public_accepts_who_was_waiting()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var dani = await TestUser.RegisterAsync(api, "dani@example.com");
        await ana.VerifyEmailAsync(api);
        await MakePrivateAsync(dani);
        await FollowAsync(ana, dani);

        await MakePrivateAsync(dani, isPrivate: false);

        await Eventually.Until(
            () => RelationshipAsync(ana, dani),
            status => string.Equals(status, "Following", StringComparison.Ordinal),
            "the request is accepted");
        (await dani.Client.GetJsonAsync("/api/v1/follow-requests/received")).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_private_profile_only_shows_its_username_photo_bio_and_numbers_to_who_does_not_follow_it()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var stranger = await TestUser.RegisterAsync(api, "stranger@example.com");
        using var follower = await TestUser.RegisterAsync(api, "follower@example.com");
        using var dani = await TestUser.RegisterAsync(api, "dani@example.com", username: "dani", displayName: "Dani Prieto");
        await follower.VerifyEmailAsync(api);
        using var profile = await dani.Client.PutJsonAsync("/api/v1/me/profile", new { displayName = "Dani Prieto", bio = "Con Bimba", city = "Madrid", avatarAssetId = (Guid?)null });
        profile.StatusCode.ShouldBe(HttpStatusCode.OK);
        var petId = await CreatePetAsync(dani, "Bimba");
        var walkId = await PutWalkAsync(dani, petId, "Everyone");
        await FollowAsync(follower, dani);
        await MakePrivateAsync(dani);

        var restricted = await stranger.Client.GetJsonAsync($"/api/v1/users/{dani.Id}");

        restricted.GetProperty("isPrivate").GetBoolean().ShouldBeTrue();
        restricted.GetProperty("username").GetString().ShouldBe("dani");
        restricted.GetProperty("displayName").GetString().ShouldBe("dani");
        restricted.GetProperty("bio").GetString().ShouldBe("Con Bimba");
        restricted.GetProperty("city").ValueKind.ShouldBe(JsonValueKind.Null);
        (await stranger.Client.GetJsonAsync($"/api/v1/users/{dani.Id}/counts")).GetProperty("followers").GetInt32().ShouldBe(1);

        foreach (var path in new[] { "pets", "walks", "walk-stats" })
        {
            using var denied = await GetAsync(stranger, $"/api/v1/users/{dani.Id}/{path}");
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeAsync(denied)).ShouldBe("identity.profile_private");
        }

        using var pet = await GetAsync(stranger, $"/api/v1/pets/{petId}");
        pet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var walk = await GetAsync(stranger, $"/api/v1/walks/{walkId}");
        walk.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var full = await follower.Client.GetJsonAsync($"/api/v1/users/{dani.Id}");
        full.GetProperty("displayName").GetString().ShouldBe("Dani Prieto");
        full.GetProperty("city").GetString().ShouldBe("Madrid");
        (await follower.Client.GetJsonAsync($"/api/v1/users/{dani.Id}/pets")).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await follower.Client.GetJsonAsync($"/api/v1/users/{dani.Id}/walk-stats")).GetProperty("walkCount").GetInt32().ShouldBe(1);
        (await follower.Client.GetJsonAsync($"/api/v1/walks/{walkId}")).GetProperty("note").GetString().ShouldBe("Por el Retiro");
    }

    [Fact]
    public async Task The_search_only_finds_a_private_profile_by_its_username_unless_you_follow_it()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var stranger = await TestUser.RegisterAsync(api, "stranger@example.com", displayName: "Quien Busca");
        using var follower = await TestUser.RegisterAsync(api, "follower@example.com", displayName: "Quien Sigue");
        using var dani = await TestUser.RegisterAsync(api, "dani@example.com", username: "dani_p", displayName: "Rocío Prieto");
        await follower.VerifyEmailAsync(api);
        await CreatePetAsync(dani, "Bimba");
        await FollowAsync(follower, dani);
        await MakePrivateAsync(dani);

        (await SearchUsersAsync(stranger, "rocio")).ShouldBeEmpty();
        (await SearchUsersAsync(stranger, "dani_p")).ShouldBe(["dani_p"]);
        var found = (await stranger.Client.GetJsonAsync("/api/v1/search/users?q=dani_p")).GetProperty("items")[0];
        found.GetProperty("displayName").GetString().ShouldBe("dani_p");
        found.GetProperty("city").ValueKind.ShouldBe(JsonValueKind.Null);
        (await SearchPetsAsync(stranger, "Bimba")).ShouldBeEmpty();

        (await SearchUsersAsync(follower, "rocio")).ShouldBe(["dani_p"]);
        (await follower.Client.GetJsonAsync("/api/v1/search/users?q=rocio")).GetProperty("items")[0].GetProperty("displayName").GetString().ShouldBe("Rocío Prieto");
        (await SearchPetsAsync(follower, "Bimba")).ShouldBe(["Bimba"]);
    }

    [Theory]
    [InlineData("friends")]
    [InlineData("followers")]
    [InlineData("following")]
    public async Task Only_that_person_and_their_friends_can_see_their_lists(string list)
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var friend = await TestUser.RegisterAsync(api, "friend@example.com");
        using var follower = await TestUser.RegisterAsync(api, "follower@example.com");
        using var stranger = await TestUser.RegisterAsync(api, "stranger@example.com");
        await BefriendAsync(api, ana, friend);
        await follower.VerifyEmailAsync(api);
        await FollowAsync(follower, ana);

        (await ana.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/{list}")).GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);
        (await friend.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/{list}")).GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);

        foreach (var viewer in new[] { follower, stranger })
        {
            using var denied = await GetAsync(viewer, $"/api/v1/users/{ana.Id}/{list}");
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeAsync(denied)).ShouldBe("social.lists_private");
        }
    }

    [Theory]
    [InlineData("friends")]
    [InlineData("followers")]
    [InlineData("following")]
    public async Task A_list_of_people_goes_through_its_pages_without_repeating_anyone(string list)
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        await ana.VerifyEmailAsync(api);
        var people = new List<TestUser>();
        for (var i = 0; i < 3; i++)
        {
            var person = await TestUser.RegisterAsync(api, $"p{i}@example.com", username: $"person_{i}");
            people.Add(person);
            await person.VerifyEmailAsync(api);
            await FollowAsync(ana, person);
            await FollowAsync(person, ana);
        }

        var seen = new List<Guid>();
        var path = $"/api/v1/users/{ana.Id}/{list}?limit=2";
        var first = await ana.Client.GetJsonAsync(path);
        seen.AddRange(first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("user").GetProperty("id").GetGuid()));
        var second = await ana.Client.GetJsonAsync($"{path}&cursor={first.GetProperty("nextCursor").GetString()}");
        seen.AddRange(second.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("user").GetProperty("id").GetGuid()));

        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        seen.ShouldBe(people.AsEnumerable().Reverse().Select(person => person.Id));
        foreach (var person in people)
        {
            person.Dispose();
        }
    }

    [Fact]
    public async Task Someone_can_be_removed_from_your_followers()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await BefriendAsync(api, ana, bea);

        using var removed = await DeleteAsync(ana, $"/api/v1/users/{bea.Id}/follower");

        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = await removed.JsonAsync();
        answer.GetProperty("status").GetString().ShouldBe("Following");
        answer.GetProperty("followsYou").GetBoolean().ShouldBeFalse();
        (await RelationshipAsync(bea, ana)).ShouldBe("None");
    }

    [Fact]
    public async Task Concurrent_follows_between_two_people_leave_a_friendship()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await ana.VerifyEmailAsync(api);
        await bea.VerifyEmailAsync(api);

        var responses = await Task.WhenAll(
            ana.Client.PutJsonAsync($"/api/v1/users/{bea.Id}/follow", new { }),
            bea.Client.PutJsonAsync($"/api/v1/users/{ana.Id}/follow", new { }));

        responses.ShouldAllBe(response => response.IsSuccessStatusCode);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        (await RelationshipAsync(ana, bea)).ShouldBe("Friends");
        (await ana.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/friends")).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Nobody_can_follow_block_or_report_themselves()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        await ana.VerifyEmailAsync(api);

        using var follow = await ana.Client.PutJsonAsync($"/api/v1/users/{ana.Id}/follow", new { });
        using var block = await ana.Client.PutJsonAsync($"/api/v1/blocks/{ana.Id}", new { });
        using var report = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = ana.Id, reason = "Spam" });

        foreach (var response in new[] { follow, block, report })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CodeAsync(response)).ShouldBe("social.cannot_target_self");
        }

        (await RelationshipAsync(ana, ana)).ShouldBe("Self");
    }

    [Fact]
    public async Task Each_viewer_sees_the_walks_that_the_owner_allowed()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var friend = await TestUser.RegisterAsync(api, "friend@example.com");
        using var follower = await TestUser.RegisterAsync(api, "follower@example.com");
        using var stranger = await TestUser.RegisterAsync(api, "stranger@example.com");
        await BefriendAsync(api, ana, friend);
        await follower.VerifyEmailAsync(api);
        await FollowAsync(follower, ana);
        var petId = await CreatePetAsync(ana);
        var everyone = await PutWalkAsync(ana, petId, "Everyone");
        var friends = await PutWalkAsync(ana, petId, "Friends", minutesAfterStart: 60);
        var onlyMe = await PutWalkAsync(ana, petId, "OnlyMe", minutesAfterStart: 120);

        (await VisibilitiesAsync(ana, ana)).ShouldBe(["Everyone", "Friends", "OnlyMe"]);
        (await VisibilitiesAsync(friend, ana)).ShouldBe(["Everyone", "Friends"]);
        (await VisibilitiesAsync(follower, ana)).ShouldBe(["Everyone"]);
        (await VisibilitiesAsync(stranger, ana)).ShouldBe(["Everyone"]);
        (await friend.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/walk-stats")).GetProperty("walkCount").GetInt32().ShouldBe(2);
        (await stranger.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/walk-stats")).GetProperty("walkCount").GetInt32().ShouldBe(1);

        foreach (var (viewer, walkId, visible) in new[]
                 {
                     (ana, everyone, true), (ana, friends, true), (ana, onlyMe, true),
                     (friend, everyone, true), (friend, friends, true), (friend, onlyMe, false),
                     (follower, everyone, true), (follower, friends, false), (follower, onlyMe, false),
                     (stranger, everyone, true), (stranger, friends, false), (stranger, onlyMe, false),
                 })
        {
            using var response = await GetAsync(viewer, $"/api/v1/walks/{walkId}");
            response.StatusCode.ShouldBe(visible ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            if (!visible)
            {
                (await CodeAsync(response)).ShouldBe("walks.not_found");
            }
        }
    }

    [Fact]
    public async Task Only_the_owner_gets_the_points_near_the_start_and_the_end_of_a_route()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        var walkId = await PutWalkAsync(ana, await CreatePetAsync(ana), "Everyone");

        var whole = await ana.Client.GetJsonAsync($"/api/v1/walks/{walkId}");
        var cut = await bea.Client.GetJsonAsync($"/api/v1/walks/{walkId}");
        var cutList = (await bea.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/walks")).GetProperty("items")[0];

        whole.GetProperty("segments")[0].GetProperty("timeOffsetsMs").GetArrayLength().ShouldBe(201);
        cut.GetProperty("distanceM").GetInt32().ShouldBe(whole.GetProperty("distanceM").GetInt32());
        cut.GetProperty("note").GetString().ShouldBe("Por el Retiro");

        var start = new GeoPoint(40.4, -3.7);
        var end = new GeoPoint(40.42, -3.7);
        var visibleRoutes = new[]
        {
            cut.GetProperty("segments")[0].GetProperty("polyline").GetString()!,
            cut.GetProperty("previewPolylines")[0].GetString()!,
            cutList.GetProperty("previewPolylines")[0].GetString()!,
        };
        foreach (var route in visibleRoutes)
        {
            Polyline.TryDecode(route, out var points).ShouldBeTrue();
            points.ShouldNotBeEmpty();
            points.ShouldAllBe(point => GeoMath.DistanceM(point, start) >= RoutePrivacy.HiddenZoneM && GeoMath.DistanceM(point, end) >= RoutePrivacy.HiddenZoneM);
        }

        cut.GetProperty("segments")[0].GetProperty("timeOffsetsMs")[0].GetInt64().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_block_in_either_direction_makes_each_person_disappear_for_the_other(bool ownerBlocks)
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var owner = await TestUser.RegisterAsync(api, "owner@example.com", username: "owner_name");
        using var viewer = await TestUser.RegisterAsync(api, "viewer@example.com", username: "viewer_name");
        await BefriendAsync(api, viewer, owner);
        var petId = await CreatePetAsync(owner, "Canela");
        var walkId = await PutWalkAsync(owner, petId, "Everyone");
        (await SearchUsersAsync(viewer, "owner_name")).ShouldBe(["owner_name"]);
        (await SearchPetsAsync(viewer, "Canela")).ShouldBe(["Canela"]);

        await BlockAsync(ownerBlocks ? owner : viewer, ownerBlocks ? viewer : owner);

        foreach (var (path, code) in new[]
                 {
                     ($"/api/v1/users/{owner.Id}", "identity.user_not_found"),
                     ($"/api/v1/users/{owner.Id}/counts", "identity.user_not_found"),
                     ($"/api/v1/users/{owner.Id}/friends", "identity.user_not_found"),
                     ($"/api/v1/users/{owner.Id}/followers", "identity.user_not_found"),
                     ($"/api/v1/users/{owner.Id}/following", "identity.user_not_found"),
                     ($"/api/v1/users/{owner.Id}/pets", "identity.user_not_found"),
                     ($"/api/v1/users/{owner.Id}/walks", "identity.user_not_found"),
                     ($"/api/v1/users/{owner.Id}/walk-stats", "identity.user_not_found"),
                     ($"/api/v1/pets/{petId}", "pets.not_found"),
                     ($"/api/v1/walks/{walkId}", "walks.not_found"),
                 })
        {
            using var response = await GetAsync(viewer, path);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            (await CodeAsync(response)).ShouldBe(code);
        }

        (await SearchUsersAsync(viewer, "owner_name")).ShouldBeEmpty();
        (await SearchPetsAsync(viewer, "Canela")).ShouldBeEmpty();
        (await SearchUsersAsync(owner, "viewer_name")).ShouldBeEmpty();
        (await viewer.Client.GetJsonAsync($"/api/v1/users/{viewer.Id}/friends")).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await owner.Client.GetJsonAsync($"/api/v1/users/{owner.Id}/friends")).GetProperty("items").GetArrayLength().ShouldBe(0);

        var blocker = ownerBlocks ? owner : viewer;
        var blocked = ownerBlocks ? viewer : owner;
        (await RelationshipAsync(blocker, blocked)).ShouldBe("Blocked");
        using var hidden = await GetAsync(blocked, $"/api/v1/users/{blocker.Id}/relationship");
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(hidden)).ShouldBe("identity.user_not_found");
    }

    [Fact]
    public async Task A_blocked_person_cannot_follow_and_cannot_tell_why()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await bea.VerifyEmailAsync(api);
        await BlockAsync(ana, bea);

        using var toBlocker = await bea.Client.PutJsonAsync($"/api/v1/users/{ana.Id}/follow", new { });
        using var toNobody = await bea.Client.PutJsonAsync($"/api/v1/users/{Guid.CreateVersion7()}/follow", new { });

        toBlocker.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        toNobody.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(toBlocker)).ShouldBe("identity.user_not_found");
        (await CodeAsync(toNobody)).ShouldBe("identity.user_not_found");
    }

    [Fact]
    public async Task Blocking_removes_the_follows_and_the_requests_and_unblocking_does_not_bring_them_back()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        using var carlos = await TestUser.RegisterAsync(api, "carlos@example.com");
        await BefriendAsync(api, ana, bea);
        await carlos.VerifyEmailAsync(api);
        await MakePrivateAsync(ana);
        (await FollowAsync(carlos, ana)).GetProperty("status").GetString().ShouldBe("Requested");

        await BlockAsync(ana, bea);
        await BlockAsync(ana, bea);
        await BlockAsync(ana, carlos);

        (await ana.Client.GetJsonAsync("/api/v1/follow-requests/received")).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await carlos.Client.GetJsonAsync("/api/v1/follow-requests/sent")).GetProperty("items").GetArrayLength().ShouldBe(0);
        var blocks = await ana.Client.GetJsonAsync("/api/v1/blocks");
        blocks.GetProperty("items").EnumerateArray().Select(block => block.GetProperty("user").GetProperty("id").GetGuid()).ShouldBe([carlos.Id, bea.Id]);

        using var unblocked = await DeleteAsync(ana, $"/api/v1/blocks/{bea.Id}");
        unblocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var unblockedAgain = await DeleteAsync(ana, $"/api/v1/blocks/{bea.Id}");
        unblockedAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await RelationshipAsync(ana, bea)).ShouldBe("None");
        (await RelationshipAsync(bea, ana)).ShouldBe("None");
        (await ana.Client.GetJsonAsync($"/api/v1/users/{bea.Id}")).GetProperty("username").GetString().ShouldBe("bea");

        using var scope = api.Services.CreateScope();
        var outbox = await scope.ServiceProvider.GetRequiredService<SocialDbContext>().OutboxMessages.Select(message => message.Type).ToListAsync(Token);
        outbox.Count(type => string.Equals(type, "social.user_blocked.v1", StringComparison.Ordinal)).ShouldBe(2);
        outbox.Count(type => string.Equals(type, "social.friendship_removed.v1", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task People_are_found_without_caring_about_accents_or_case()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var viewer = await TestUser.RegisterAsync(api, "viewer@example.com", username: "viewer", displayName: "Quien Busca");
        using var jose = await TestUser.RegisterAsync(api, "jose@example.com", username: "pepe_paseos", displayName: "José Ángel");
        using var mariana = await TestUser.RegisterAsync(api, "mariana@example.com", username: "mari", displayName: "Mariana");
        using var pending = await TestUser.RegisterAsync(api, "pending@example.com", onboard: false, displayName: "José Pendiente");

        (await SearchUsersAsync(viewer, "jose")).ShouldBe(["pepe_paseos"]);
        (await SearchUsersAsync(viewer, "JOSÉ ÁNGEL")).ShouldBe(["pepe_paseos"]);
        (await SearchUsersAsync(viewer, "angel")).ShouldBe(["pepe_paseos"]);
        (await SearchUsersAsync(viewer, "pepe_paseos")).ShouldBe(["pepe_paseos"]);
        (await SearchUsersAsync(viewer, "arian")).ShouldBe(["mari"]);
        (await SearchUsersAsync(viewer, "viewer")).ShouldBeEmpty();
        (await SearchUsersAsync(viewer, "%%")).ShouldBeEmpty();
        (await SearchUsersAsync(viewer, "__")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Pets_are_found_by_name_and_breed_with_their_owner()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await CreatePetAsync(ana, "Ñora", breed: "Galgo español");
        await CreatePetAsync(ana, "Luna");
        await CreatePetAsync(bea, "Luna de Bea");

        (await SearchPetsAsync(bea, "nora")).ShouldBe(["Ñora"]);
        (await SearchPetsAsync(bea, "galgo espanol")).ShouldBe(["Ñora"]);
        (await SearchPetsAsync(bea, "luan")).ShouldBe(["Luna"]);

        var found = (await bea.Client.GetJsonAsync("/api/v1/search/pets?q=luna")).GetProperty("items")[0];
        found.GetProperty("owner").GetProperty("username").GetString().ShouldBe("ana");
        found.TryGetProperty("microchip", out _).ShouldBeFalse();
        found.GetProperty("owner").TryGetProperty("email", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_search_goes_through_its_pages_without_repeating_anyone()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var viewer = await TestUser.RegisterAsync(api, "viewer@example.com", username: "viewer", displayName: "Quien Busca");
        var people = new List<TestUser>();
        for (var i = 0; i < 5; i++)
        {
            people.Add(await TestUser.RegisterAsync(api, $"p{i}@example.com", username: $"pablo_{i}", displayName: "Pablo"));
        }

        var seen = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var path = "/api/v1/search/users?q=pablo&limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}");
            var result = await viewer.Client.GetJsonAsync(path);
            seen.AddRange(result.GetProperty("items").EnumerateArray().Select(user => user.GetProperty("id").GetGuid()));
            if (result.GetProperty("nextCursor").ValueKind == JsonValueKind.Null)
            {
                break;
            }

            cursor = result.GetProperty("nextCursor").GetString();
        }

        seen.ShouldBe(people.Select(person => person.Id), ignoreOrder: true);
        foreach (var person in people)
        {
            person.Dispose();
        }
    }

    [Theory]
    [InlineData("/api/v1/search/users?q=a")]
    [InlineData("/api/v1/search/users")]
    [InlineData("/api/v1/search/pets?q=%20a%20")]
    [InlineData("/api/v1/search/pets")]
    public async Task A_search_needs_at_least_two_characters(string path)
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");

        using var response = await GetAsync(ana, path);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.JsonAsync();
        problem.GetProperty("code").GetString().ShouldBe("validation.failed");
        problem.GetProperty("errors").TryGetProperty("q", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/v1/search/users?q=ana&cursor=!!")]
    [InlineData("/api/v1/search/pets?q=ana&cursor=!!")]
    [InlineData("/api/v1/follow-requests/received?cursor=!!")]
    [InlineData("/api/v1/blocks?cursor=!!")]
    public async Task A_bad_cursor_is_rejected(string path)
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");

        using var response = await GetAsync(ana, path);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(response)).ShouldBe("request.invalid");
    }

    [Fact]
    public async Task A_bad_cursor_in_your_own_list_is_rejected()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");

        using var response = await GetAsync(ana, $"/api/v1/users/{ana.Id}/followers?cursor=!!");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(response)).ShouldBe("request.invalid");
    }

    [Fact]
    public async Task The_pets_of_another_person_do_not_include_the_microchip_or_exist_for_unknown_people()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        using var onboarding = await TestUser.RegisterAsync(api, "pending@example.com", onboard: false);
        await CreatePetAsync(ana);

        (await bea.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/pets")).GetProperty("items")[0].GetProperty("microchip").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ana.Client.GetJsonAsync($"/api/v1/users/{ana.Id}/pets")).GetProperty("items")[0].GetProperty("microchip").GetString().ShouldBe("941000024567890");

        foreach (var missing in new[] { Guid.CreateVersion7(), onboarding.Id })
        {
            using var pets = await GetAsync(bea, $"/api/v1/users/{missing}/pets");
            using var walks = await GetAsync(bea, $"/api/v1/users/{missing}/walks");
            pets.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            walks.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeAsync(pets)).ShouldBe("identity.user_not_found");
            (await CodeAsync(walks)).ShouldBe("identity.user_not_found");
        }
    }

    [Fact]
    public async Task The_profile_can_be_chosen_private_when_creating_the_account()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var dani = await TestUser.RegisterAsync(api, "dani@example.com", onboard: false);

        (await dani.Client.GetJsonAsync("/api/v1/me")).GetProperty("isPrivate").GetBoolean().ShouldBeFalse();

        using var chosen = await dani.Client.PutJsonAsync("/api/v1/me/username", new { username = "dani", isPrivate = true });

        chosen.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await chosen.JsonAsync()).GetProperty("isPrivate").GetBoolean().ShouldBeTrue();
        (await dani.Client.GetJsonAsync("/api/v1/me/profile")).GetProperty("isPrivate").GetBoolean().ShouldBeTrue();

        using var again = await dani.Client.PutJsonAsync("/api/v1/me/username", new { username = "dani" });
        (await again.JsonAsync()).GetProperty("isPrivate").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_report_is_kept_once_and_validated()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");

        using var first = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = bea.Id, reason = "Harassment", details = "Insiste" });
        using var second = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = bea.Id, reason = "Spam" });
        using var nobody = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = Guid.CreateVersion7(), reason = "Spam" });
        using var badReason = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = bea.Id, reason = "Rude" });
        using var badType = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "Walk", targetId = bea.Id, reason = "Spam" });

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.JsonAsync()).GetProperty("id").GetGuid().ShouldBe((await first.JsonAsync()).GetProperty("id").GetGuid());
        nobody.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        badReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badType.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var scope = api.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<SocialDbContext>().Reports.SingleAsync(Token);
        stored.Details.ShouldBe("Insiste");
        stored.ReporterId.ShouldBe(ana.Id);
    }

    [Fact]
    public async Task Follows_and_reports_have_a_limit_per_person()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Api:SocialWritesPermitsPerMinute"] = "2",
        });
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");

        using var first = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = bea.Id, reason = "Spam" });
        using var second = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = bea.Id, reason = "Spam" });
        using var third = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = bea.Id, reason = "Spam" });
        using var other = await bea.Client.PostJsonAsync("/api/v1/reports", new { targetType = "User", targetId = ana.Id, reason = "Spam" });

        first.IsSuccessStatusCode.ShouldBeTrue();
        second.IsSuccessStatusCode.ShouldBeTrue();
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeAsync(third)).ShouldBe("rate_limit.exceeded");
        other.IsSuccessStatusCode.ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_an_account_removes_it_from_the_lists_of_other_people()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        using var carlos = await TestUser.RegisterAsync(api, "carlos@example.com");
        await BefriendAsync(api, ana, bea);
        await BlockAsync(carlos, ana);

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/me", UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { password = TestUser.Password }),
        };
        using var deleted = await ana.Client.SendAsync(request, Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await bea.Client.GetJsonAsync($"/api/v1/users/{bea.Id}/friends")).GetProperty("items").GetArrayLength().ShouldBe(0);
        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        await Eventually.Until(
            () => social.Follows.CountAsync(Token),
            count => count == 0,
            "the follows of the deleted account are erased");
        await Eventually.Until(
            () => social.UserBlocks.CountAsync(Token),
            count => count == 0,
            "the blocks of the deleted account are erased");
    }
}
