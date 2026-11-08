using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Notifications.Application;
using GeoWoof.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Notifications;

/// <summary>The notifications of phase 8 end to end: the events of Social and Chat, the Outbox and the Inbox.</summary>
public sealed class NotificationsEndpointsTests(TestInfrastructure infrastructure)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    private static async Task<TestUser> VerifiedAsync(ApiFactory api, string username)
    {
        var user = await TestUser.RegisterAsync(api, $"{username}@example.com", username, displayName: char.ToUpperInvariant(username[0]) + username[1..]);
        await user.VerifyEmailAsync(api);
        return user;
    }

    private static async Task FollowAsync(TestUser follower, TestUser followee)
    {
        using var response = await follower.Client.PutJsonAsync($"/api/v1/users/{followee.Id}/follow", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task BlockAsync(TestUser blocker, TestUser blocked)
    {
        using var response = await blocker.Client.PutJsonAsync($"/api/v1/blocks/{blocked.Id}", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<Guid> PostAsync(ApiFactory api, TestUser author, string? text = "Un paseo")
    {
        var photo = await MediaTestClient.UploadReadyAsync(api, author.Client, TestImages.Jpeg(400, 300), "PostImage");
        var id = Guid.CreateVersion7();
        using var created = await author.Client.PutJsonAsync(
            $"/api/v1/posts/{id}",
            new { text, visibility = "Everyone", mediaAssetIds = new[] { photo.GetProperty("id").GetGuid() } });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return id;
    }

    private static async Task<JsonElement> ItemsAsync(TestUser user) =>
        (await user.Client.GetJsonAsync("/api/v1/notifications")).GetProperty("items");

    private static async Task<int> UnreadAsync(TestUser user) =>
        (await user.Client.GetJsonAsync("/api/v1/notifications/unread-count")).GetProperty("count").GetInt32();

    private static string[] TypesOf(JsonElement items) =>
        items.EnumerateArray().Select(item => item.GetProperty("type").GetString()!).ToArray();

    /// <summary>The events take a moment to arrive.</summary>
    private static Task<JsonElement> UntilAsync(TestUser user, int count) =>
        Eventually.Until(() => ItemsAsync(user), items => items.GetArrayLength() >= count, $"{user.Email} has {count} notifications");

    private static async Task RegisterDeviceAsync(TestUser user, string token)
    {
        using var response = await user.Client.PutJsonAsync("/api/v1/devices", new { token, platform = "Android" });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>Waits for everything in the Outbox to have been dealt with, by making something later arrive.</summary>
    private static async Task WaitForEventsAsync(ApiFactory api, TestUser witness)
    {
        var marker = await VerifiedAsync(api, $"marker{Guid.NewGuid():N}"[..12]);
        await FollowAsync(marker, witness);
        await Eventually.Until(
            () => ItemsAsync(witness),
            items => items.EnumerateArray().Any(item => item.GetProperty("actor").GetProperty("id").GetGuid() == marker.Id),
            "the events have been processed");
        marker.Dispose();
    }

    [Fact]
    public async Task What_someone_does_with_your_posts_reaches_you()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var post = await PostAsync(api, ana);

        using var like = await bea.Client.PutJsonAsync($"/api/v1/posts/{post}/likes", new { });
        like.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var comment = await bea.Client.PostJsonAsync($"/api/v1/posts/{post}/comments", new { text = "Qué bonito" });
        comment.StatusCode.ShouldBe(HttpStatusCode.Created);
        var commentId = (await comment.JsonAsync()).GetProperty("id").GetGuid();
        using var repost = await bea.Client.PutJsonAsync($"/api/v1/posts/{post}/repost", new { });
        repost.StatusCode.ShouldBe(HttpStatusCode.OK);

        var items = await UntilAsync(ana, 3);

        TypesOf(items).ShouldBe(["PostLiked", "CommentAdded", "PostReposted"], ignoreOrder: true);
        var byType = items.EnumerateArray().ToDictionary(item => item.GetProperty("type").GetString()!, StringComparer.Ordinal);
        byType["PostLiked"].GetProperty("postId").GetGuid().ShouldBe(post);
        byType["PostLiked"].GetProperty("actor").GetProperty("username").GetString().ShouldBe("bea");
        byType["PostLiked"].GetProperty("isRead").GetBoolean().ShouldBeFalse();
        byType["PostLiked"].GetProperty("createdAt").GetString()!.ShouldEndWith("Z");
        byType["CommentAdded"].GetProperty("commentId").GetGuid().ShouldBe(commentId);
        (await UnreadAsync(ana)).ShouldBe(3);
    }

    [Fact]
    public async Task Nobody_hears_about_what_they_do_themselves_and_nothing_twice()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var post = await PostAsync(api, ana);

        using var own = await ana.Client.PutJsonAsync($"/api/v1/posts/{post}/likes", new { });
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        for (var i = 0; i < 2; i++)
        {
            using var like = await bea.Client.PutJsonAsync($"/api/v1/posts/{post}/likes", new { });
            like.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var unlike = await bea.Client.DeleteAsync(new Uri($"/api/v1/posts/{post}/likes", UriKind.Relative), Token);
            unlike.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await WaitForEventsAsync(api, ana);

        var likes = (await ItemsAsync(ana)).EnumerateArray().Where(item => item.GetProperty("type").GetString() == "PostLiked").ToList();
        likes.Count.ShouldBe(1);
        likes[0].GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(bea.Id);
    }

    [Fact]
    public async Task A_mention_reaches_who_is_mentioned()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");

        var post = await PostAsync(api, ana, "Paseando con @bea");

        var item = (await UntilAsync(bea, 1))[0];
        item.GetProperty("type").GetString().ShouldBe("UserMentioned");
        item.GetProperty("postId").GetGuid().ShouldBe(post);
        item.GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(ana.Id);
    }

    [Fact]
    public async Task Following_and_following_back_say_each_thing_once()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");

        await FollowAsync(bea, ana);
        TypesOf(await UntilAsync(ana, 1)).ShouldBe(["UserFollowed"]);

        await FollowAsync(ana, bea);
        var atBea = await UntilAsync(bea, 1);
        TypesOf(atBea).ShouldBe(["FriendshipStarted"]);
        atBea[0].GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(ana.Id);

        await WaitForEventsAsync(api, ana);
        (await ItemsAsync(ana)).EnumerateArray().Count(item => item.GetProperty("actor").GetProperty("id").GetGuid() == bea.Id).ShouldBe(1);
    }

    [Fact]
    public async Task A_request_says_who_asks_and_the_answer_reaches_them()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var privacy = await ana.Client.PutJsonAsync("/api/v1/me/privacy", new { isPrivate = true });
        privacy.StatusCode.ShouldBe(HttpStatusCode.OK);

        await FollowAsync(bea, ana);

        var request = (await UntilAsync(ana, 1))[0];
        request.GetProperty("type").GetString().ShouldBe("FollowRequested");
        var requestId = request.GetProperty("requestId").GetGuid();

        using var accepted = await ana.Client.PostAsync(new Uri($"/api/v1/follow-requests/{requestId}/accept", UriKind.Relative), null, Token);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);

        var answer = (await UntilAsync(bea, 1))[0];
        answer.GetProperty("type").GetString().ShouldBe("FollowRequestAccepted");
        answer.GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(ana.Id);

        // Whoever accepted already knows that they follow them now.
        await WaitForEventsAsync(api, ana);
        TypesOf(await ItemsAsync(ana)).ShouldNotContain("UserFollowed", customMessage: null);
    }

    [Fact]
    public async Task A_block_hides_what_was_there_and_stops_what_comes()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var post = await PostAsync(api, ana);
        using var like = await bea.Client.PutJsonAsync($"/api/v1/posts/{post}/likes", new { });
        like.StatusCode.ShouldBe(HttpStatusCode.OK);
        await UntilAsync(ana, 1);

        await BlockAsync(ana, bea);

        (await ItemsAsync(ana)).GetArrayLength().ShouldBe(0);
        (await UnreadAsync(ana)).ShouldBe(0);
    }

    [Fact]
    public async Task They_can_be_read_one_by_one_or_all_together_and_only_your_own()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await FollowAsync(bea, ana);
        await FollowAsync(carlos, ana);
        var items = await UntilAsync(ana, 2);
        var first = items[0].GetProperty("id").GetGuid();

        using var someoneElses = await bea.Client.PostAsync(new Uri($"/api/v1/notifications/{first}/read", UriKind.Relative), null, Token);
        someoneElses.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(someoneElses)).ShouldBe("notifications.not_found");

        using var one = await ana.Client.PostAsync(new Uri($"/api/v1/notifications/{first}/read", UriKind.Relative), null, Token);
        one.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await UnreadAsync(ana)).ShouldBe(1);
        (await ItemsAsync(ana))[0].GetProperty("isRead").GetBoolean().ShouldBeTrue();

        using var all = await ana.Client.PostAsync(new Uri("/api/v1/notifications/read-all", UriKind.Relative), null, Token);
        all.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await UnreadAsync(ana)).ShouldBe(0);
    }

    [Fact]
    public async Task The_list_can_be_paged()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await FollowAsync(bea, ana);
        await UntilAsync(ana, 1);
        await FollowAsync(carlos, ana);
        await UntilAsync(ana, 2);

        var first = await ana.Client.GetJsonAsync("/api/v1/notifications?limit=1");
        first.GetProperty("items")[0].GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(carlos.Id);
        var second = await ana.Client.GetJsonAsync($"/api/v1/notifications?limit=1&cursor={first.GetProperty("nextCursor").GetString()}");
        second.GetProperty("items")[0].GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(bea.Id);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        using var bad = await ana.Client.GetAsync(new Uri("/api/v1/notifications?cursor=%25%25", UriKind.Relative), Token);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(bad)).ShouldBe("request.invalid_cursor");
    }

    [Fact]
    public async Task Deleting_an_account_removes_what_it_did_and_what_it_had()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await FollowAsync(bea, ana);
        await UntilAsync(ana, 1);

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/me", UriKind.Relative))
        {
            Content = JsonContent.Create(new { password = TestUser.Password }),
        };
        using var deleted = await bea.Client.SendAsync(request, Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Eventually.Until(() => UnreadAsync(ana), count => count == 0, "what a deleted account did disappears");
        (await ItemsAsync(ana)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_notification_goes_to_the_devices_of_who_receives_it_and_a_message_with_its_text()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await RegisterDeviceAsync(ana, "ana-phone");
        await RegisterDeviceAsync(ana, "ana-tablet");
        await RegisterDeviceAsync(bea, "bea-phone");

        await FollowAsync(bea, ana);
        await Eventually.Until(() => Task.FromResult(api.Pushes.Sent.Count), count => count >= 1, "the push of the notification is sent");
        var push = api.Pushes.Sent[0];
        push.Tokens.ShouldBe(["ana-phone", "ana-tablet"], ignoreOrder: true);
        push.Message.IsHighPriority.ShouldBeFalse();
        push.Message.Data["type"].ShouldBe("UserFollowed");
        push.Message.Data["actorId"].ShouldBe(bea.Id.ToString());
        push.Message.Data["actorName"].ShouldBe("Bea");
        push.Message.Data["notificationId"].ShouldBe((await UntilAsync(ana, 1))[0].GetProperty("id").GetGuid().ToString());

        await FollowAsync(ana, bea);
        using var open = await ana.Client.PutAsync(new Uri($"/api/v1/conversations/direct/{bea.Id}", UriKind.Relative), null, Token);
        var conversation = (await open.JsonAsync()).GetProperty("id").GetGuid();
        var longText = new string('a', 500);
        using var sent = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.CreateVersion7(), body = longText });
        sent.StatusCode.ShouldBe(HttpStatusCode.Created);

        await Eventually.Until(
            () => Task.FromResult(api.Pushes.Sent.Any(item => item.Message.Data["type"] == "Message")),
            found => found,
            "the push of the message is sent");
        var messagePush = api.Pushes.Sent.First(item => item.Message.Data["type"] == "Message");
        messagePush.Tokens.ShouldBe(["bea-phone"]);
        messagePush.Message.IsHighPriority.ShouldBeTrue();
        messagePush.Message.Data["conversationId"].ShouldBe(conversation.ToString());
        messagePush.Message.Data["senderName"].ShouldBe("Ana");
        messagePush.Message.Data["body"].Length.ShouldBe(200);
        messagePush.Message.Data["hasImage"].ShouldBe("false");
    }

    [Fact]
    public async Task A_device_belongs_to_the_last_account_and_can_leave()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await RegisterDeviceAsync(ana, "shared-phone");
        await RegisterDeviceAsync(ana, "shared-phone");
        await RegisterDeviceAsync(bea, "shared-phone");

        await FollowAsync(carlos, ana);
        await UntilAsync(ana, 1);
        await FollowAsync(carlos, bea);
        await UntilAsync(bea, 1);
        api.Pushes.Sent.Select(push => push.Message.Data["actorId"]).ShouldBe([carlos.Id.ToString()]);

        // Somebody else cannot take it away.
        using var notYours = await DeleteDeviceAsync(ana, "shared-phone");
        notYours.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var leaving = await DeleteDeviceAsync(bea, "shared-phone");
        leaving.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        api.Pushes.Clear();
        await FollowAsync(ana, bea);
        await WaitForEventsAsync(api, bea);
        api.Pushes.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_device_needs_a_token_and_a_known_platform()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");

        using var noToken = await ana.Client.PutJsonAsync("/api/v1/devices", new { token = "", platform = "Android" });
        using var wrongPlatform = await ana.Client.PutJsonAsync("/api/v1/devices", new { token = "abc", platform = "Toaster" });

        noToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        wrongPlatform.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(noToken)).ShouldBe("validation.failed");
    }

    [Fact]
    public async Task A_token_that_is_no_longer_valid_is_forgotten()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await RegisterDeviceAsync(ana, "good-phone");
        await RegisterDeviceAsync(ana, "old-phone");
        api.Pushes.InvalidTokens.Add("old-phone");

        await FollowAsync(bea, ana);
        await UntilAsync(ana, 1);
        await Eventually.Until(() => Task.FromResult(api.Pushes.Sent.Count), count => count == 1, "the first push is sent");

        await FollowAsync(carlos, ana);
        await UntilAsync(ana, 2);
        var second = await Eventually.Until(() => Task.FromResult(api.Pushes.Sent.Count), count => count == 2, "the second push is sent");

        second.ShouldBe(2);
        api.Pushes.Sent[1].Tokens.ShouldBe(["good-phone"]);
    }

    [Fact]
    public async Task A_push_that_fails_does_not_lose_the_notification()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await RegisterDeviceAsync(ana, "phone");
        api.Pushes.Fails = true;

        await FollowAsync(bea, ana);

        TypesOf(await UntilAsync(ana, 1)).ShouldBe(["UserFollowed"]);
    }

    [Fact]
    public async Task Without_firebase_the_push_only_goes_to_the_log()
    {
        using var api = ApiFactory.WithRealPushSender(infrastructure);

        var sender = api.Services.GetRequiredService<IPushSender>();

        sender.GetType().Name.ShouldBe("LoggingPushSender");
        (await sender.SendAsync(["token"], new PushMessage(new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "Message" }, IsHighPriority: true), Token)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("does-not-exist.json")]
    [InlineData("not-a-key.json")]
    public async Task A_firebase_key_that_cannot_be_read_stops_the_push_and_nothing_else(string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{fileName}");
        if (fileName == "not-a-key.json")
        {
            await File.WriteAllTextAsync(path, "{ this is not a key", Token);
        }

        try
        {
            using var api = ApiFactory.WithRealPushSender(infrastructure, path);

            var sender = api.Services.GetRequiredService<IPushSender>();

            sender.GetType().Name.ShouldBe("FirebasePushSender");
            (await sender.SendAsync(["token"], new PushMessage(new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "Message" }, IsHighPriority: true), Token)).ShouldBeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<HttpResponseMessage> DeleteDeviceAsync(TestUser user, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/devices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { token }),
        };
        return await user.Client.SendAsync(request, Token);
    }
}
