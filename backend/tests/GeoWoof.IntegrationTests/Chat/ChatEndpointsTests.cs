using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.TestSupport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GeoWoof.IntegrationTests.Chat;

/// <summary>The chat of phase 8 (docs/api.md and docs/realtime.md) end to end: HTTP, PostgreSQL, the hub and the Outbox.</summary>
public sealed class ChatEndpointsTests(TestInfrastructure infrastructure)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> GetAsync(TestUser user, string path) =>
        user.Client.GetAsync(new Uri(path, UriKind.Relative), Token);

    private static Task<HttpResponseMessage> PutAsync(TestUser user, string path) =>
        user.Client.PutAsync(new Uri(path, UriKind.Relative), null, Token);

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    private static async Task<TestUser> VerifiedAsync(ApiFactory api, string username)
    {
        var user = await TestUser.RegisterAsync(api, $"{username}@example.com", username);
        await user.VerifyEmailAsync(api);
        return user;
    }

    private static async Task FollowAsync(TestUser follower, TestUser followee)
    {
        using var response = await follower.Client.PutJsonAsync($"/api/v1/users/{followee.Id}/follow", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task UnfollowAsync(TestUser follower, TestUser followee)
    {
        using var response = await follower.Client.DeleteAsync(new Uri($"/api/v1/users/{followee.Id}/follow", UriKind.Relative), Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task BefriendAsync(TestUser first, TestUser second)
    {
        await FollowAsync(first, second);
        await FollowAsync(second, first);
    }

    private static async Task BlockAsync(TestUser blocker, TestUser blocked)
    {
        using var response = await blocker.Client.PutJsonAsync($"/api/v1/blocks/{blocked.Id}", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task UnblockAsync(TestUser blocker, TestUser blocked)
    {
        using var response = await blocker.Client.DeleteAsync(new Uri($"/api/v1/blocks/{blocked.Id}", UriKind.Relative), Token);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<Guid> OpenAsync(TestUser user, TestUser other)
    {
        using var response = await PutAsync(user, $"/api/v1/conversations/direct/{other.Id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> SendAsync(TestUser user, Guid conversationId, string? body = "Hola", Guid? imageAssetId = null, Guid? clientMessageId = null)
    {
        using var response = await user.Client.PostJsonAsync(
            $"/api/v1/conversations/{conversationId}/messages",
            new { clientMessageId = clientMessageId ?? Guid.CreateVersion7(), body, imageAssetId });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.JsonAsync();
    }

    private static async Task ReadAsync(TestUser user, Guid conversationId, Guid messageId)
    {
        using var response = await user.Client.PostJsonAsync($"/api/v1/conversations/{conversationId}/read", new { lastReadMessageId = messageId });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<int> UnreadAsync(TestUser user) =>
        (await user.Client.GetJsonAsync("/api/v1/conversations/unread-count")).GetProperty("count").GetInt32();

    private static async Task<JsonElement> ListAsync(TestUser user) =>
        (await user.Client.GetJsonAsync("/api/v1/conversations")).GetProperty("items");

    [Fact]
    public async Task Two_friends_share_one_conversation_and_talk()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);

        var conversation = await OpenAsync(ana, bea);
        (await OpenAsync(bea, ana)).ShouldBe(conversation);
        (await ListAsync(bea)).GetArrayLength().ShouldBe(0);

        var sent = await SendAsync(ana, conversation, "¿Salimos a las 7?");
        sent.GetProperty("senderId").GetGuid().ShouldBe(ana.Id);
        sent.GetProperty("body").GetString().ShouldBe("¿Salimos a las 7?");
        sent.GetProperty("image").ValueKind.ShouldBe(JsonValueKind.Null);
        sent.GetProperty("sentAt").GetString()!.ShouldEndWith("Z");

        var listed = (await ListAsync(bea))[0];
        listed.GetProperty("id").GetGuid().ShouldBe(conversation);
        listed.GetProperty("otherUser").GetProperty("username").GetString().ShouldBe("ana");
        listed.GetProperty("canSend").GetBoolean().ShouldBeTrue();
        listed.GetProperty("unreadCount").GetInt32().ShouldBe(1);
        listed.GetProperty("lastMessage").GetProperty("id").GetGuid().ShouldBe(sent.GetProperty("id").GetGuid());
        (await UnreadAsync(bea)).ShouldBe(1);
        (await UnreadAsync(ana)).ShouldBe(0);

        await ReadAsync(bea, conversation, sent.GetProperty("id").GetGuid());

        (await UnreadAsync(bea)).ShouldBe(0);
        var seenByAna = await ana.Client.GetJsonAsync($"/api/v1/conversations/{conversation}");
        seenByAna.GetProperty("otherLastReadMessageId").GetGuid().ShouldBe(sent.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Only_friends_can_start_talking_and_a_block_looks_like_nobody()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await FollowAsync(ana, bea);
        await BlockAsync(carlos, ana);

        using var notMutual = await PutAsync(ana, $"/api/v1/conversations/direct/{bea.Id}");
        notMutual.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(notMutual)).ShouldBe("chat.not_friends");

        using var blocked = await PutAsync(ana, $"/api/v1/conversations/direct/{carlos.Id}");
        using var nobody = await PutAsync(ana, $"/api/v1/conversations/direct/{Guid.CreateVersion7()}");
        blocked.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(blocked)).ShouldBe("chat.user_not_found");
        (await CodeAsync(nobody)).ShouldBe("chat.user_not_found");

        using var yourself = await PutAsync(ana, $"/api/v1/conversations/direct/{ana.Id}");
        yourself.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(yourself)).ShouldBe("chat.with_yourself");
    }

    [Fact]
    public async Task Whoever_is_not_in_a_conversation_cannot_tell_it_exists()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        var message = (await SendAsync(ana, conversation)).GetProperty("id").GetGuid();

        using var get = await GetAsync(carlos, $"/api/v1/conversations/{conversation}");
        using var messages = await GetAsync(carlos, $"/api/v1/conversations/{conversation}/messages");
        using var send = await carlos.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.CreateVersion7(), body = "Hola" });
        using var read = await carlos.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/read", new { lastReadMessageId = message });

        foreach (var response in new[] { get, messages, send, read })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeAsync(response)).ShouldBe("chat.conversation_not_found");
        }
    }

    [Fact]
    public async Task Sending_the_same_message_twice_keeps_one()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        var clientMessageId = Guid.CreateVersion7();

        var first = await SendAsync(ana, conversation, "Hola", clientMessageId: clientMessageId);
        using var retry = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId, body = "Hola" });

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.JsonAsync()).GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
        (await bea.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages")).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await UnreadAsync(bea)).ShouldBe(1);
    }

    [Fact]
    public async Task Sending_at_the_same_moment_also_keeps_one()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        var clientMessageId = Guid.CreateVersion7();

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId, body = "Hola" })));

        answers.ShouldAllBe(answer => answer.IsSuccessStatusCode);
        answers.Count(answer => answer.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        (await bea.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages")).GetProperty("items").GetArrayLength().ShouldBe(1);
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task Ending_a_friendship_keeps_the_conversation_readable_but_not_writable()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        var clientMessageId = Guid.CreateVersion7();
        await SendAsync(ana, conversation, clientMessageId: clientMessageId);

        await UnfollowAsync(bea, ana);

        (await ListAsync(ana))[0].GetProperty("canSend").GetBoolean().ShouldBeFalse();
        (await ListAsync(bea))[0].GetProperty("canSend").GetBoolean().ShouldBeFalse();
        (await ana.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages")).GetProperty("items").GetArrayLength().ShouldBe(1);

        using var send = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.CreateVersion7(), body = "¿Hola?" });
        send.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(send)).ShouldBe("chat.not_friends");

        // The answer that got lost before the friendship ended still arrives.
        using var retry = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId, body = "Hola" });
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);

        // And opening it again finds it, without being friends.
        (await OpenAsync(ana, bea)).ShouldBe(conversation);
    }

    [Fact]
    public async Task Whoever_blocks_stops_seeing_the_conversation_and_the_other_only_sees_they_cannot_write()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        await SendAsync(bea, conversation);
        var fromAna = (await SendAsync(ana, conversation)).GetProperty("id").GetGuid();
        (await UnreadAsync(ana)).ShouldBe(1);

        await BlockAsync(ana, bea);

        (await ListAsync(ana)).GetArrayLength().ShouldBe(0);
        (await UnreadAsync(ana)).ShouldBe(0);
        using var get = await GetAsync(ana, $"/api/v1/conversations/{conversation}");
        using var messages = await GetAsync(ana, $"/api/v1/conversations/{conversation}/messages");
        using var read = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/read", new { lastReadMessageId = fromAna });
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        messages.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var seenByBea = await ListAsync(bea);
        seenByBea[0].GetProperty("canSend").GetBoolean().ShouldBeFalse();
        using var send = await bea.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.CreateVersion7(), body = "¿Hola?" });
        (await CodeAsync(send)).ShouldBe("chat.not_friends");

        await UnblockAsync(ana, bea);

        (await ListAsync(ana)).GetArrayLength().ShouldBe(1);
        (await UnreadAsync(ana)).ShouldBe(1);
    }

    [Fact]
    public async Task A_private_profile_you_no_longer_follow_says_little()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com", "ana_user", displayName: "Ana García");
        await ana.VerifyEmailAsync(api);
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(bea, ana);
        await SendAsync(ana, conversation);
        using var privacy = await ana.Client.PutJsonAsync("/api/v1/me/privacy", new { isPrivate = true });
        privacy.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ListAsync(bea))[0].GetProperty("otherUser").GetProperty("displayName").GetString().ShouldBe("Ana García");

        await UnfollowAsync(bea, ana);

        (await ListAsync(bea))[0].GetProperty("otherUser").GetProperty("displayName").GetString().ShouldBe("ana_user");
    }

    [Fact]
    public async Task A_message_can_be_a_photo_and_it_needs_something()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        var photo = (await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(400, 300), "ChatImage")).GetProperty("id").GetGuid();

        var sent = await SendAsync(ana, conversation, body: null, imageAssetId: photo);

        sent.GetProperty("body").ValueKind.ShouldBe(JsonValueKind.Null);
        sent.GetProperty("image").GetProperty("assetId").GetGuid().ShouldBe(photo);
        sent.GetProperty("image").GetProperty("thumbUrl").GetString().ShouldNotBeNull();
        var seen = (await bea.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages")).GetProperty("items")[0];
        seen.GetProperty("image").GetProperty("mediumUrl").GetString().ShouldNotBeNull();

        using var nothing = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.CreateVersion7(), body = "   " });
        using var tooLong = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.CreateVersion7(), body = new string('a', 2001) });
        using var noId = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.Empty, body = "Hola" });
        using var someoneElses = await bea.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.CreateVersion7(), imageAssetId = photo });
        foreach (var response in new[] { nothing, tooLong, noId })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await CodeAsync(response)).ShouldBe("validation.failed");
        }

        someoneElses.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Messages_can_be_paged_backwards_and_caught_up_forwards()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await SendAsync(ana, conversation, $"Mensaje {i}")).GetProperty("id").GetGuid());
        }

        var newest = await bea.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages?limit=2");
        newest.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([ids[4], ids[3]]);
        var older = await bea.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages?limit=2&cursor={newest.GetProperty("nextCursor").GetString()}");
        older.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([ids[2], ids[1]]);
        var oldest = await bea.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages?limit=2&cursor={older.GetProperty("nextCursor").GetString()}");
        oldest.GetProperty("items").GetArrayLength().ShouldBe(1);
        oldest.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var caughtUp = await bea.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages?after={ids[1]}&limit=2");
        caughtUp.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([ids[2], ids[3]]);
        caughtUp.GetProperty("nextCursor").ValueKind.ShouldNotBe(JsonValueKind.Null);
        var rest = await bea.Client.GetJsonAsync($"/api/v1/conversations/{conversation}/messages?after={ids[3]}&limit=2");
        rest.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([ids[4]]);
        rest.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        using var badCursor = await GetAsync(bea, $"/api/v1/conversations/{conversation}/messages?cursor=%25%25");
        badCursor.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(badCursor)).ShouldBe("request.invalid_cursor");
    }

    [Fact]
    public async Task Conversations_are_listed_by_their_last_message_and_can_be_paged()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await BefriendAsync(ana, bea);
        await BefriendAsync(ana, carlos);
        var withBea = await OpenAsync(ana, bea);
        var withCarlos = await OpenAsync(ana, carlos);
        await SendAsync(ana, withBea);
        await SendAsync(ana, withCarlos);
        await SendAsync(bea, withBea);

        var first = await ana.Client.GetJsonAsync("/api/v1/conversations?limit=1");
        first.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(withBea);
        var second = await ana.Client.GetJsonAsync($"/api/v1/conversations?limit=1&cursor={first.GetProperty("nextCursor").GetString()}");
        second.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(withCarlos);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Reading_only_moves_forward_and_only_with_messages_of_that_conversation()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await BefriendAsync(ana, bea);
        await BefriendAsync(ana, carlos);
        var conversation = await OpenAsync(ana, bea);
        var other = await OpenAsync(ana, carlos);
        var first = (await SendAsync(ana, conversation)).GetProperty("id").GetGuid();
        var second = (await SendAsync(ana, conversation)).GetProperty("id").GetGuid();
        var elsewhere = (await SendAsync(ana, other)).GetProperty("id").GetGuid();

        await ReadAsync(bea, conversation, second);
        await ReadAsync(bea, conversation, first);
        (await UnreadAsync(bea)).ShouldBe(0);

        using var wrong = await bea.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/read", new { lastReadMessageId = elsewhere });
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(wrong)).ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Deleting_an_account_removes_its_conversations_for_both()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        var photo = (await MediaTestClient.UploadReadyAsync(api, bea.Client, TestImages.Jpeg(400, 300), "ChatImage")).GetProperty("id").GetGuid();
        await SendAsync(bea, conversation, body: null, imageAssetId: photo);

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/me", UriKind.Relative))
        {
            Content = JsonContent.Create(new { password = TestUser.Password }),
        };
        using var deleted = await ana.Client.SendAsync(request, Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Eventually.Until(
            async () =>
            {
                using var response = await GetAsync(bea, $"/api/v1/conversations/{conversation}");
                return response.StatusCode;
            },
            status => status == HttpStatusCode.NotFound,
            "the conversation of a deleted account disappears");
        (await UnreadAsync(bea)).ShouldBe(0);
        (await ListAsync(bea)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Too_many_messages_in_a_minute_are_refused()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>(StringComparer.Ordinal) { ["Api:ChatMessagesPermitsPerMinute"] = "2" });
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId = Guid.CreateVersion7(), body = "Hola" });
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_hub_needs_a_token()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var anonymous = api.CreateClient();

        using var without = await anonymous.PostAsync(new Uri("/hubs/chat/negotiate?negotiateVersion=1", UriKind.Relative), null, Token);
        without.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var token = ana.Client.DefaultRequestHeaders.Authorization!.Parameter;
        using var with = await anonymous.PostAsync(new Uri($"/hubs/chat/negotiate?negotiateVersion=1&access_token={token}", UriKind.Relative), null, Token);
        with.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_message_reaches_both_people_and_nobody_else()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        await using var anaHub = await ChatClient.ConnectAsync(api, ana);
        await using var beaHub = await ChatClient.ConnectAsync(api, bea);
        await using var carlosHub = await ChatClient.ConnectAsync(api, carlos);

        var clientMessageId = Guid.CreateVersion7();
        var sent = await SendAsync(ana, conversation, "Hola", clientMessageId: clientMessageId);

        var atBea = await beaHub.NextAsync("MessageReceived");
        atBea.GetProperty("id").GetGuid().ShouldBe(sent.GetProperty("id").GetGuid());
        atBea.GetProperty("conversationId").GetGuid().ShouldBe(conversation);
        atBea.GetProperty("body").GetString().ShouldBe("Hola");
        atBea.GetProperty("sentAt").GetString()!.ShouldEndWith("Z");
        (await anaHub.NextAsync("MessageReceived")).GetProperty("clientMessageId").GetGuid().ShouldBe(clientMessageId);
        await carlosHub.ShouldReceiveNothingAsync();

        // A retry is not a new message.
        using var retry = await ana.Client.PostJsonAsync($"/api/v1/conversations/{conversation}/messages", new { clientMessageId, body = "Hola" });
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        await beaHub.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task Reading_tells_both_people_once()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        var message = (await SendAsync(ana, conversation)).GetProperty("id").GetGuid();
        await using var anaHub = await ChatClient.ConnectAsync(api, ana);
        await using var beaHub = await ChatClient.ConnectAsync(api, bea);

        await ReadAsync(bea, conversation, message);

        var atAna = await anaHub.NextAsync("ConversationRead");
        atAna.GetProperty("conversationId").GetGuid().ShouldBe(conversation);
        atAna.GetProperty("userId").GetGuid().ShouldBe(bea.Id);
        atAna.GetProperty("lastReadMessageId").GetGuid().ShouldBe(message);
        (await beaHub.NextAsync("ConversationRead")).GetProperty("userId").GetGuid().ShouldBe(bea.Id);

        await ReadAsync(bea, conversation, message);
        await anaHub.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task Typing_only_reaches_the_other_person_and_not_too_often()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        await using var anaHub = await ChatClient.ConnectAsync(api, ana);
        await using var beaHub = await ChatClient.ConnectAsync(api, bea);

        await anaHub.SetTypingAsync(conversation);

        var typing = await beaHub.NextAsync("TypingChanged");
        typing.GetProperty("conversationId").GetGuid().ShouldBe(conversation);
        typing.GetProperty("userId").GetGuid().ShouldBe(ana.Id);
        await anaHub.ShouldReceiveNothingAsync();

        await anaHub.SetTypingAsync(conversation);
        await beaHub.ShouldReceiveNothingAsync();
    }

    [Fact]
    public async Task Typing_says_its_error_when_it_is_not_allowed()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        await UnfollowAsync(bea, ana);
        await using var anaHub = await ChatClient.ConnectAsync(api, ana);
        await using var carlosHub = await ChatClient.ConnectAsync(api, carlos);

        var notFriends = await Should.ThrowAsync<HubException>(anaHub.SetTypingAsync(conversation));
        notFriends.Message.Split(": ")[^1].ShouldBe("chat.not_friends");

        var notThere = await Should.ThrowAsync<HubException>(carlosHub.SetTypingAsync(conversation));
        notThere.Message.Split(": ")[^1].ShouldBe("chat.conversation_not_found");
    }

    [Fact]
    public async Task A_person_with_too_many_connections_loses_the_last_one()
    {
        using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>(StringComparer.Ordinal) { ["Chat:MaxConnectionsPerUser"] = "1" });
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var conversation = await OpenAsync(ana, bea);
        await using var first = await ChatClient.ConnectAsync(api, ana);

        // A call is only answered after the connection has been counted.
        await first.SetTypingAsync(conversation);
        await using var second = ChatClient.Create(api, ana);
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
    }
}
