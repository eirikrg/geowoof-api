using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Social.Infrastructure.Persistence;
using GeoWoof.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Social;

/// <summary>The contract of phase 6 (docs/api.md) end to end: HTTP, handlers, PostgreSQL, the cache in Redis and the Outbox.</summary>
public sealed class PostEndpointsTests(TestInfrastructure infrastructure)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> GetAsync(TestUser user, string path) =>
        user.Client.GetAsync(new Uri(path, UriKind.Relative), Token);

    private static Task<HttpResponseMessage> DeleteAsync(TestUser user, string path) =>
        user.Client.DeleteAsync(new Uri(path, UriKind.Relative), Token);

    private static async Task<Guid> ImageAsync(ApiFactory api, TestUser user, string purpose = "PostImage")
    {
        var photo = await MediaTestClient.UploadReadyAsync(api, user.Client, TestImages.Jpeg(400, 300), purpose);
        return photo.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PostAsync(ApiFactory api, TestUser author, string visibility = "Everyone", string? text = "Un paseo", Guid[]? petIds = null)
    {
        var id = Guid.CreateVersion7();
        using var created = await author.Client.PutJsonAsync($"/api/v1/posts/{id}", new { text, visibility, mediaAssetIds = new[] { await ImageAsync(api, author) }, petIds });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return id;
    }

    private static async Task FollowAsync(TestUser follower, TestUser followee)
    {
        using var response = await follower.Client.PutJsonAsync($"/api/v1/users/{followee.Id}/follow", new { });
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

    private static async Task MakePrivateAsync(TestUser user, bool isPrivate = true)
    {
        using var response = await user.Client.PutJsonAsync("/api/v1/me/privacy", new { isPrivate });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<TestUser> VerifiedAsync(ApiFactory api, string username)
    {
        var user = await TestUser.RegisterAsync(api, $"{username}@example.com", username: username);
        await user.VerifyEmailAsync(api);
        return user;
    }

    private static async Task<List<Guid>> PostIdsAsync(TestUser viewer, string path) =>
        (await viewer.Client.GetJsonAsync(path)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

    private static async Task<List<Guid>> TimelinePostIdsAsync(TestUser viewer, string path) =>
        (await viewer.Client.GetJsonAsync(path)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("post").GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task A_post_keeps_what_was_sent_and_repeating_the_request_does_not_create_another()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var created = await ana.Client.PostJsonAsync("/api/v1/pets", new { name = "Luna", species = "Dog", sex = "Female", neutered = false });
        var petId = (await created.JsonAsync()).GetProperty("id").GetGuid();
        var first = await ImageAsync(api, ana);
        var second = await ImageAsync(api, ana);
        var id = Guid.CreateVersion7();
        var body = new { text = "  Dos fotos  ", visibility = "Friends", mediaAssetIds = new[] { second, first }, petIds = new[] { petId } };

        using var response = await ana.Client.PutJsonAsync($"/api/v1/posts/{id}", body);
        using var repeated = await ana.Client.PutJsonAsync($"/api/v1/posts/{id}", body with { text = "Otro texto" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await repeated.JsonAsync()).GetProperty("text").GetString().ShouldBe("Dos fotos");

        var post = await response.JsonAsync();
        post.GetProperty("author").GetProperty("username").GetString().ShouldBe("ana");
        post.GetProperty("visibility").GetString().ShouldBe("Friends");
        post.GetProperty("media").EnumerateArray().Select(item => item.GetProperty("assetId").GetGuid()).ShouldBe([second, first]);
        post.GetProperty("media")[0].GetProperty("type").GetString().ShouldBe("Image");
        post.GetProperty("media")[0].GetProperty("thumbUrl").GetString().ShouldNotBeNullOrEmpty();
        post.GetProperty("pets")[0].GetProperty("name").GetString().ShouldBe("Luna");
        post.GetProperty("canRepost").GetBoolean().ShouldBeFalse();
        post.GetProperty("createdAt").GetString().ShouldEndWith("Z");

        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        (await social.Posts.CountAsync(Token)).ShouldBe(1);
        (await social.OutboxMessages.CountAsync(message => message.Type == "social.post_created.v1", Token)).ShouldBe(1);
    }

    [Fact]
    public async Task A_post_needs_a_verified_email()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");

        using var response = await ana.Client.PutJsonAsync($"/api/v1/posts/{Guid.CreateVersion7()}", new { visibility = "Everyone", mediaAssetIds = new[] { Guid.CreateVersion7() } });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(response)).ShouldBe("auth.forbidden");
    }

    [Fact]
    public async Task What_a_post_cannot_be_is_rejected()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var image = await ImageAsync(api, ana);
        using var created = await bea.Client.PostJsonAsync("/api/v1/pets", new { name = "Mishi", species = "Cat", sex = "Female", neutered = false });
        var petOfBea = (await created.JsonAsync()).GetProperty("id").GetGuid();

        async Task<HttpResponseMessage> PutAsync(TestUser user, object body) =>
            await user.Client.PutJsonAsync($"/api/v1/posts/{Guid.CreateVersion7()}", body);

        using var noImages = await PutAsync(ana, new { visibility = "Everyone", mediaAssetIds = Array.Empty<Guid>() });
        using var tooMany = await PutAsync(ana, new { visibility = "Everyone", mediaAssetIds = Enumerable.Range(0, 11).Select(_ => Guid.CreateVersion7()).ToArray() });
        using var repeated = await PutAsync(ana, new { visibility = "Everyone", mediaAssetIds = new[] { image, image } });
        using var onlyMe = await PutAsync(ana, new { visibility = "OnlyMe", mediaAssetIds = new[] { image } });
        using var longText = await PutAsync(ana, new { text = new string('x', 2001), visibility = "Everyone", mediaAssetIds = new[] { image } });
        foreach (var response in new[] { noImages, tooMany, repeated, onlyMe, longText })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await CodeAsync(response)).ShouldBe("validation.failed");
        }

        using var notMyPet = await PutAsync(ana, new { visibility = "Everyone", mediaAssetIds = new[] { image }, petIds = new[] { petOfBea } });
        notMyPet.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(notMyPet)).ShouldBe("social.pet_not_owned");

        using var notMyImage = await PutAsync(bea, new { visibility = "Everyone", mediaAssetIds = new[] { image } });
        notMyImage.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var avatar = await ImageAsync(api, ana, "UserAvatar");
        using var wrongPurpose = await PutAsync(ana, new { visibility = "Everyone", mediaAssetIds = new[] { avatar } });
        wrongPurpose.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(wrongPurpose)).ShouldBe("media.wrong_purpose");

        var taken = await PostAsync(api, ana);
        using var someoneElses = await bea.Client.PutJsonAsync($"/api/v1/posts/{taken}", new { visibility = "Everyone", mediaAssetIds = new[] { await ImageAsync(api, bea) } });
        someoneElses.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(someoneElses)).ShouldBe("social.post_id_taken");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_viewer_sees_the_posts_that_the_author_allowed(bool isPrivateProfile)
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var friend = await VerifiedAsync(api, "friend");
        using var follower = await VerifiedAsync(api, "follower");
        using var stranger = await VerifiedAsync(api, "stranger");
        using var blocked = await VerifiedAsync(api, "blocked");
        await BefriendAsync(ana, friend);
        await FollowAsync(follower, ana);
        await BlockAsync(ana, blocked);
        var everyone = await PostAsync(api, ana, "Everyone");
        var friends = await PostAsync(api, ana, "Friends");
        if (isPrivateProfile)
        {
            await MakePrivateAsync(ana);
        }

        foreach (var (viewer, postId, visible) in new[]
                 {
                     (ana, everyone, true), (ana, friends, true),
                     (friend, everyone, true), (friend, friends, true),
                     (follower, everyone, true), (follower, friends, false),
                     (stranger, everyone, !isPrivateProfile), (stranger, friends, false),
                     (blocked, everyone, false), (blocked, friends, false),
                 })
        {
            using var response = await GetAsync(viewer, $"/api/v1/posts/{postId}");
            response.StatusCode.ShouldBe(visible ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            if (!visible)
            {
                (await CodeAsync(response)).ShouldBe("social.post_not_found");
            }
        }
    }

    [Fact]
    public async Task The_feed_has_what_you_and_those_you_follow_publish_and_repost_if_you_can_see_it()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        using var dani = await VerifiedAsync(api, "dani");
        await FollowAsync(ana, bea);
        await BefriendAsync(bea, carlos);

        var mine = await PostAsync(api, ana);
        var ofBea = await PostAsync(api, bea);
        var friendsOfBea = await PostAsync(api, bea, "Friends");
        var ofDani = await PostAsync(api, dani);
        var ofCarlos = await PostAsync(api, carlos);
        var friendsOfCarlos = await PostAsync(api, carlos, "Friends");
        using var public1 = await bea.Client.PutJsonAsync($"/api/v1/posts/{ofCarlos}/repost", new { });
        using var friendsOnly = await bea.Client.PutJsonAsync($"/api/v1/posts/{friendsOfCarlos}/repost", new { });
        public1.StatusCode.ShouldBe(HttpStatusCode.OK);
        friendsOnly.StatusCode.ShouldBe(HttpStatusCode.OK);

        var feed = await ana.Client.GetJsonAsync("/api/v1/feed");
        var items = feed.GetProperty("items").EnumerateArray().ToList();

        // Most recent first. What Bea reposted from Carlos only for friends is not for Ana, who is not his friend.
        items.Select(item => item.GetProperty("post").GetProperty("id").GetGuid()).ShouldBe([ofCarlos, ofBea, mine]);
        var repost = items[0];
        repost.GetProperty("repostedBy").GetProperty("username").GetString().ShouldBe("bea");
        repost.GetProperty("post").GetProperty("author").GetProperty("username").GetString().ShouldBe("carlos");
        repost.GetProperty("id").GetGuid().ShouldNotBe(ofCarlos);
        repost.GetProperty("repostedAt").GetString().ShouldEndWith("Z");
        items[1].GetProperty("repostedBy").ValueKind.ShouldBe(JsonValueKind.Null);
        items[1].GetProperty("id").GetGuid().ShouldBe(ofBea);
        (await TimelinePostIdsAsync(bea, "/api/v1/feed")).ShouldContain(friendsOfBea);
        (await TimelinePostIdsAsync(bea, "/api/v1/feed")).ShouldNotContain(ofDani);
    }

    [Fact]
    public async Task Explore_only_has_what_anyone_can_see_of_others_and_goes_away_with_the_privacy_at_once()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        using var dani = await VerifiedAsync(api, "dani");
        using var blocked = await VerifiedAsync(api, "blocked");
        await BlockAsync(ana, blocked);
        await MakePrivateAsync(dani);

        await PostAsync(api, ana);
        var ofBea = await PostAsync(api, bea);
        await PostAsync(api, bea, "Friends");
        var ofCarlos = await PostAsync(api, carlos);
        await PostAsync(api, dani);
        await PostAsync(api, blocked);
        using var reposted = await carlos.Client.PutJsonAsync($"/api/v1/posts/{ofBea}/repost", new { });

        (await PostIdsAsync(ana, "/api/v1/explore")).ShouldBe([ofCarlos, ofBea]);

        await MakePrivateAsync(carlos);

        // Without waiting for the event: the rules are applied again when each post is shown.
        (await PostIdsAsync(ana, "/api/v1/explore")).ShouldBe([ofBea]);
        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        await Eventually.Until(
            () => social.Posts.AsNoTracking().CountAsync(post => post.Id == ofCarlos && post.AuthorIsPrivate, Token),
            count => count == 1,
            "the copy of the privacy is updated");

        await MakePrivateAsync(carlos, isPrivate: false);
        await Eventually.Until(() => PostIdsAsync(ana, "/api/v1/explore"), ids => ids.Contains(ofCarlos), "the post is back");
    }

    [Fact]
    public async Task A_profile_shows_its_posts_and_reposts_with_the_rules_of_each_author()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var dani = await VerifiedAsync(api, "dani");
        using var stranger = await VerifiedAsync(api, "stranger");
        await BefriendAsync(ana, bea);
        var own = await PostAsync(api, ana);
        var friendsOfBea = await PostAsync(api, bea, "Friends");
        using var reposted = await ana.Client.PutJsonAsync($"/api/v1/posts/{friendsOfBea}/repost", new { });
        reposted.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await TimelinePostIdsAsync(bea, $"/api/v1/users/{ana.Id}/posts")).ShouldBe([friendsOfBea, own]);
        (await TimelinePostIdsAsync(stranger, $"/api/v1/users/{ana.Id}/posts")).ShouldBe([own]);

        await MakePrivateAsync(dani);
        using var denied = await GetAsync(stranger, $"/api/v1/users/{dani.Id}/posts");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(denied)).ShouldBe("identity.profile_private");

        await BlockAsync(ana, stranger);
        using var hidden = await GetAsync(stranger, $"/api/v1/users/{ana.Id}/posts");
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(hidden)).ShouldBe("identity.user_not_found");
        using var nobody = await GetAsync(stranger, $"/api/v1/users/{Guid.CreateVersion7()}/posts");
        nobody.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_like_counts_once_however_many_times_it_is_given()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com", username: "bea");
        var postId = await PostAsync(api, ana);

        var responses = await Task.WhenAll(
            bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/likes", new { }),
            bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/likes", new { }),
            bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/likes", new { }));
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Dispose();
        }

        using var again = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/likes", new { });
        var liked = await again.JsonAsync();
        liked.GetProperty("likeCount").GetInt32().ShouldBe(1);
        liked.GetProperty("likedByMe").GetBoolean().ShouldBeTrue();
        (await bea.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("likedByMe").GetBoolean().ShouldBeTrue();
        (await ana.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("likedByMe").GetBoolean().ShouldBeFalse();

        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        (await social.OutboxMessages.CountAsync(message => message.Type == "social.post_liked.v1", Token)).ShouldBe(1);

        using var removed = await DeleteAsync(bea, $"/api/v1/posts/{postId}/likes");
        using var removedAgain = await DeleteAsync(bea, $"/api/v1/posts/{postId}/likes");
        var unliked = await removedAgain.JsonAsync();
        unliked.GetProperty("likeCount").GetInt32().ShouldBe(0);
        unliked.GetProperty("likedByMe").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task What_cannot_be_seen_cannot_be_used()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var postId = await PostAsync(api, ana, "Friends");

        using var like = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/likes", new { });
        using var unlike = await DeleteAsync(bea, $"/api/v1/posts/{postId}/likes");
        using var comment = await bea.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Hola" });
        using var comments = await GetAsync(bea, $"/api/v1/posts/{postId}/comments");
        using var repost = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/repost", new { });
        using var save = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/save", new { });
        using var report = await bea.Client.PostJsonAsync("/api/v1/reports", new { targetType = "Post", targetId = postId, reason = "Spam" });

        foreach (var response in new[] { like, unlike, comment, comments, repost, save, report })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeAsync(response)).ShouldBe("social.post_not_found");
        }

        // It does not even say that it is not theirs.
        using var deleted = await DeleteAsync(bea, $"/api/v1/posts/{postId}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync(ana, $"/api/v1/posts/{postId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Comments_go_from_the_oldest_to_the_newest_and_their_authors_and_the_author_of_the_post_can_delete_them()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        var postId = await PostAsync(api, ana);

        async Task<Guid> CommentAsync(TestUser user, string text)
        {
            using var response = await user.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text });
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            return (await response.JsonAsync()).GetProperty("id").GetGuid();
        }

        var first = await CommentAsync(bea, "  Primero  ");
        var second = await CommentAsync(carlos, "Segundo");
        var third = await CommentAsync(bea, "Tercero");

        var page = await bea.Client.GetJsonAsync($"/api/v1/posts/{postId}/comments?limit=2");
        var items = page.GetProperty("items");
        items[0].GetProperty("text").GetString().ShouldBe("Primero");
        items[0].GetProperty("canDelete").GetBoolean().ShouldBeTrue();
        items[1].GetProperty("canDelete").GetBoolean().ShouldBeFalse();
        var rest = await bea.Client.GetJsonAsync($"/api/v1/posts/{postId}/comments?limit=2&cursor={page.GetProperty("nextCursor").GetString()}");
        rest.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(third);
        rest.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ana.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("commentCount").GetInt32().ShouldBe(3);

        using var notAllowed = await DeleteAsync(carlos, $"/api/v1/posts/{postId}/comments/{first}");
        notAllowed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(notAllowed)).ShouldBe("social.cannot_delete_comment");
        using var byAuthor = await DeleteAsync(carlos, $"/api/v1/posts/{postId}/comments/{second}");
        using var byPostAuthor = await DeleteAsync(ana, $"/api/v1/posts/{postId}/comments/{first}");
        using var twice = await DeleteAsync(ana, $"/api/v1/posts/{postId}/comments/{first}");
        byAuthor.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        byPostAuthor.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        twice.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ana.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("commentCount").GetInt32().ShouldBe(1);

        using var empty = await bea.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "   " });
        using var tooLong = await bea.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = new string('x', 1001) });
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var badCursor = await GetAsync(bea, $"/api/v1/posts/{postId}/comments?cursor=!!");
        badCursor.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(badCursor)).ShouldBe("request.invalid");
    }

    [Fact]
    public async Task Commenting_needs_a_verified_email()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com", username: "bea");
        var postId = await PostAsync(api, ana);

        using var comment = await bea.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Hola" });
        using var repost = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/repost", new { });

        comment.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        repost.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(comment)).ShouldBe("auth.forbidden");
    }

    [Fact]
    public async Task A_post_of_a_friend_or_a_public_profile_can_be_reposted_once_and_undone()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var postId = await PostAsync(api, ana);
        (await bea.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("canRepost").GetBoolean().ShouldBeTrue();

        using var reposted = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/repost", new { });
        using var repeated = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/repost", new { });
        var answer = await repeated.JsonAsync();
        answer.GetProperty("repostCount").GetInt32().ShouldBe(1);
        answer.GetProperty("repostedByMe").GetBoolean().ShouldBeTrue();

        var seen = await bea.Client.GetJsonAsync($"/api/v1/posts/{postId}");
        seen.GetProperty("repostedByMe").GetBoolean().ShouldBeTrue();
        seen.GetProperty("repostCount").GetInt32().ShouldBe(1);

        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        (await social.OutboxMessages.CountAsync(message => message.Type == "social.post_reposted.v1", Token)).ShouldBe(1);

        using var undone = await DeleteAsync(bea, $"/api/v1/posts/{postId}/repost");
        using var undoneAgain = await DeleteAsync(bea, $"/api/v1/posts/{postId}/repost");
        (await undoneAgain.JsonAsync()).GetProperty("repostedByMe").GetBoolean().ShouldBeFalse();
        (await TimelinePostIdsAsync(bea, $"/api/v1/users/{bea.Id}/posts")).ShouldBeEmpty();

        using var own = await ana.Client.PutJsonAsync($"/api/v1/posts/{postId}/repost", new { });
        own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(own)).ShouldBe("social.cannot_repost");
    }

    [Fact]
    public async Task A_follower_of_a_private_profile_can_see_a_post_but_only_a_friend_can_repost_it()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var friend = await VerifiedAsync(api, "friend");
        using var follower = await VerifiedAsync(api, "follower");
        using var otherFriend = await VerifiedAsync(api, "other");
        await BefriendAsync(ana, friend);
        await BefriendAsync(friend, otherFriend);
        await FollowAsync(follower, ana);
        var postId = await PostAsync(api, ana);
        await MakePrivateAsync(ana);

        (await follower.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("canRepost").GetBoolean().ShouldBeFalse();
        using var denied = await follower.Client.PutJsonAsync($"/api/v1/posts/{postId}/repost", new { });
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(denied)).ShouldBe("social.cannot_repost");

        (await friend.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("canRepost").GetBoolean().ShouldBeTrue();
        using var allowed = await friend.Client.PutJsonAsync($"/api/v1/posts/{postId}/repost", new { });
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The repost does not show it to more people: a friend of the friend does not follow Ana.
        (await TimelinePostIdsAsync(otherFriend, "/api/v1/feed")).ShouldNotContain(postId);
        (await TimelinePostIdsAsync(otherFriend, $"/api/v1/users/{friend.Id}/posts")).ShouldNotContain(postId);
        (await TimelinePostIdsAsync(follower, $"/api/v1/users/{friend.Id}/posts")).ShouldContain(postId);
    }

    [Fact]
    public async Task Without_the_original_there_is_no_repost_and_a_block_takes_it_away()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        var deleted = await PostAsync(api, ana);
        var blocked = await PostAsync(api, ana);
        using var first = await bea.Client.PutJsonAsync($"/api/v1/posts/{deleted}/repost", new { });
        using var second = await bea.Client.PutJsonAsync($"/api/v1/posts/{blocked}/repost", new { });

        using var removed = await DeleteAsync(ana, $"/api/v1/posts/{deleted}");
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await TimelinePostIdsAsync(carlos, $"/api/v1/users/{bea.Id}/posts")).ShouldBe([blocked]);

        await BlockAsync(ana, bea);
        (await TimelinePostIdsAsync(carlos, $"/api/v1/users/{bea.Id}/posts")).ShouldBeEmpty();

        using var unblocked = await DeleteAsync(ana, $"/api/v1/blocks/{bea.Id}");
        (await TimelinePostIdsAsync(carlos, $"/api/v1/users/{bea.Id}/posts")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_block_hides_comments_likes_and_mentions_also_in_the_numbers()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        var postId = await PostAsync(api, ana, text: "Con @bea y @carlos");
        using var like = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/likes", new { });
        using var comment = await bea.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Soy Bea" });
        using var other = await carlos.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Soy Carlos" });

        (await carlos.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("mentions").GetArrayLength().ShouldBe(2);

        await BlockAsync(carlos, bea);

        var seen = await carlos.Client.GetJsonAsync($"/api/v1/posts/{postId}");
        seen.GetProperty("likeCount").GetInt32().ShouldBe(0);
        seen.GetProperty("commentCount").GetInt32().ShouldBe(1);
        seen.GetProperty("mentions").EnumerateArray().Select(mention => mention.GetProperty("username").GetString()).ShouldBe(["carlos"]);
        var comments = await carlos.Client.GetJsonAsync($"/api/v1/posts/{postId}/comments");
        comments.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("text").GetString()).ShouldBe(["Soy Carlos"]);

        var forAna = await ana.Client.GetJsonAsync($"/api/v1/posts/{postId}");
        forAna.GetProperty("likeCount").GetInt32().ShouldBe(1);
        forAna.GetProperty("commentCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_mention_only_counts_if_that_person_can_see_it_and_there_is_no_block()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var friend = await VerifiedAsync(api, "friend");
        using var stranger = await VerifiedAsync(api, "stranger");
        using var blocked = await VerifiedAsync(api, "blocked");
        await BefriendAsync(ana, friend);
        await BlockAsync(ana, blocked);

        var forAll = await PostAsync(api, ana, text: "Con @Friend, @stranger, @blocked, @nobody_here y mail ana@friend.es.");
        var post = await ana.Client.GetJsonAsync($"/api/v1/posts/{forAll}");
        post.GetProperty("mentions").EnumerateArray().Select(mention => mention.GetProperty("username").GetString()).ShouldBe(["friend", "stranger"], ignoreOrder: true);
        post.GetProperty("mentions").EnumerateArray().Single(mention => mention.GetProperty("username").GetString() == "friend").GetProperty("userId").GetGuid().ShouldBe(friend.Id);

        var forFriends = await PostAsync(api, ana, "Friends", "Con @friend y @stranger");
        var limited = await friend.Client.GetJsonAsync($"/api/v1/posts/{forFriends}");
        limited.GetProperty("mentions").EnumerateArray().Select(mention => mention.GetProperty("username").GetString()).ShouldBe(["friend"]);

        using var comment = await friend.Client.PostJsonAsync($"/api/v1/posts/{forFriends}/comments", new { text = "Mira @ana y @stranger" });
        (await comment.JsonAsync()).GetProperty("mentions").EnumerateArray().Select(mention => mention.GetProperty("username").GetString()).ShouldBe(["ana"]);

        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        (await social.OutboxMessages.CountAsync(message => message.Type == "social.user_mentioned.v1", Token)).ShouldBe(4);
    }

    [Fact]
    public async Task A_saved_post_is_only_in_the_list_of_who_saved_it_while_they_can_see_it()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await BefriendAsync(ana, bea);
        var older = await PostAsync(api, ana);
        var forFriends = await PostAsync(api, ana, "Friends");

        foreach (var postId in new[] { forFriends, older, older })
        {
            using var saved = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/save", new { });
            saved.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await PostIdsAsync(bea, "/api/v1/me/saved-posts")).ShouldBe([older, forFriends]);
        (await PostIdsAsync(ana, "/api/v1/me/saved-posts")).ShouldBeEmpty();
        (await bea.Client.GetJsonAsync($"/api/v1/posts/{older}")).GetProperty("savedByMe").GetBoolean().ShouldBeTrue();

        using var unfollowed = await DeleteAsync(ana, $"/api/v1/users/{bea.Id}/follow");
        (await PostIdsAsync(bea, "/api/v1/me/saved-posts")).ShouldBe([older]);
        await FollowAsync(ana, bea);
        (await PostIdsAsync(bea, "/api/v1/me/saved-posts")).ShouldBe([older, forFriends]);

        using var removed = await DeleteAsync(bea, $"/api/v1/posts/{older}/save");
        using var removedAgain = await DeleteAsync(bea, $"/api/v1/posts/{older}/save");
        removedAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PostIdsAsync(bea, "/api/v1/me/saved-posts")).ShouldBe([forFriends]);
    }

    [Fact]
    public async Task New_posts_do_not_repeat_or_skip_anything_in_the_next_pages()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        await FollowAsync(ana, bea);
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add(await PostAsync(api, bea));
        }

        foreach (var (path, read) in new (string, Func<JsonElement, Guid>)[]
                 {
                     ("/api/v1/feed", item => item.GetProperty("post").GetProperty("id").GetGuid()),
                     ("/api/v1/explore", item => item.GetProperty("id").GetGuid()),
                     ($"/api/v1/users/{bea.Id}/posts", item => item.GetProperty("post").GetProperty("id").GetGuid()),
                 })
        {
            var first = await ana.Client.GetJsonAsync($"{path}?limit=2");
            var seen = first.GetProperty("items").EnumerateArray().Select(read).ToList();
            seen.ShouldBe([created[4], created[3]]);

            // Published between one page and the next.
            var newer = await PostAsync(api, bea);

            var second = await ana.Client.GetJsonAsync($"{path}?limit=2&cursor={first.GetProperty("nextCursor").GetString()}");
            seen.AddRange(second.GetProperty("items").EnumerateArray().Select(read));
            var third = await ana.Client.GetJsonAsync($"{path}?limit=2&cursor={second.GetProperty("nextCursor").GetString()}");
            seen.AddRange(third.GetProperty("items").EnumerateArray().Select(read));

            seen.ShouldBe(Enumerable.Reverse(created).Take(5));
            third.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
            using var removed = await DeleteAsync(bea, $"/api/v1/posts/{newer}");
        }
    }

    [Theory]
    [InlineData("/api/v1/feed?cursor=!!")]
    [InlineData("/api/v1/explore?cursor=!!")]
    [InlineData("/api/v1/me/saved-posts?cursor=!!")]
    public async Task A_bad_cursor_is_rejected(string path)
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");

        using var response = await GetAsync(ana, path);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(response)).ShouldBe("request.invalid");
    }

    [Fact]
    public async Task Deleting_a_post_takes_everything_with_it_and_only_its_author_can_do_it()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var postId = await PostAsync(api, ana, text: "Con @bea");
        using var like = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/likes", new { });
        using var comment = await bea.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Hola @ana" });
        using var repost = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/repost", new { });
        using var save = await bea.Client.PutJsonAsync($"/api/v1/posts/{postId}/save", new { });

        using var notAuthor = await DeleteAsync(bea, $"/api/v1/posts/{postId}");
        notAuthor.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(notAuthor)).ShouldBe("social.not_post_author");

        using var deleted = await DeleteAsync(ana, $"/api/v1/posts/{postId}");
        using var deletedAgain = await DeleteAsync(ana, $"/api/v1/posts/{postId}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deletedAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        (await social.Posts.CountAsync(Token)).ShouldBe(0);
        (await social.PostLikes.CountAsync(Token)).ShouldBe(0);
        (await social.Comments.CountAsync(Token)).ShouldBe(0);
        (await social.CommentMentions.CountAsync(Token)).ShouldBe(0);
        (await social.Reposts.CountAsync(Token)).ShouldBe(0);
        (await social.SavedPosts.CountAsync(Token)).ShouldBe(0);
        (await PostIdsAsync(bea, "/api/v1/me/saved-posts")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Posts_and_comments_can_be_reported_if_they_can_be_seen_and_are_not_yours()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var postId = await PostAsync(api, ana);
        using var added = await ana.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Hola" });
        var commentId = (await added.JsonAsync()).GetProperty("id").GetGuid();

        using var post = await bea.Client.PostJsonAsync("/api/v1/reports", new { targetType = "Post", targetId = postId, reason = "Spam" });
        using var comment = await bea.Client.PostJsonAsync("/api/v1/reports", new { targetType = "Comment", targetId = commentId, reason = "Harassment" });
        using var ownPost = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "Post", targetId = postId, reason = "Spam" });
        using var ownComment = await ana.Client.PostJsonAsync("/api/v1/reports", new { targetType = "Comment", targetId = commentId, reason = "Spam" });
        using var noComment = await bea.Client.PostJsonAsync("/api/v1/reports", new { targetType = "Comment", targetId = Guid.CreateVersion7(), reason = "Spam" });

        post.StatusCode.ShouldBe(HttpStatusCode.Created);
        comment.StatusCode.ShouldBe(HttpStatusCode.Created);
        ownPost.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        ownComment.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noComment.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(noComment)).ShouldBe("social.comment_not_found");
    }

    [Fact]
    public async Task Posts_and_comments_have_a_limit_per_person()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Api:SocialWritesPermitsPerMinute"] = "2",
        });
        using var ana = await VerifiedAsync(api, "ana");

        using var first = await ana.Client.PutJsonAsync($"/api/v1/posts/{Guid.CreateVersion7()}", new { visibility = "OnlyMe" });
        using var second = await ana.Client.PostJsonAsync($"/api/v1/posts/{Guid.CreateVersion7()}/comments", new { text = "Hola" });
        using var third = await ana.Client.PutJsonAsync($"/api/v1/posts/{Guid.CreateVersion7()}/repost", new { });

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeAsync(third)).ShouldBe("rate_limit.exceeded");
    }

    [Fact]
    public async Task A_deleted_pet_stops_appearing_and_the_post_stays()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var created = await ana.Client.PostJsonAsync("/api/v1/pets", new { name = "Luna", species = "Dog", sex = "Female", neutered = false });
        var petId = (await created.JsonAsync()).GetProperty("id").GetGuid();
        var postId = await PostAsync(api, ana, petIds: [petId]);

        using var deleted = await DeleteAsync(ana, $"/api/v1/pets/{petId}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // At once, even before the event arrives: a pet that does not exist has nothing to show.
        (await ana.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("pets").GetArrayLength().ShouldBe(0);
        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        await Eventually.Until(() => social.PostPets.CountAsync(Token), count => count == 0, "the tag is removed");
    }

    [Fact]
    public async Task Deleting_an_account_removes_what_it_published_and_did()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var ofAna = await PostAsync(api, ana);
        var ofBea = await PostAsync(api, bea, text: "Con @ana");
        using var like = await ana.Client.PutJsonAsync($"/api/v1/posts/{ofBea}/likes", new { });
        using var comment = await ana.Client.PostJsonAsync($"/api/v1/posts/{ofBea}/comments", new { text = "Hola" });
        using var repost = await ana.Client.PutJsonAsync($"/api/v1/posts/{ofBea}/repost", new { });
        using var save = await ana.Client.PutJsonAsync($"/api/v1/posts/{ofBea}/save", new { });
        using var other = await bea.Client.PutJsonAsync($"/api/v1/posts/{ofAna}/likes", new { });

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/me", UriKind.Relative))
        {
            Content = JsonContent.Create(new { password = TestUser.Password }),
        };
        using var deleted = await ana.Client.SendAsync(request, Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        await Eventually.Until(() => social.Posts.CountAsync(post => post.AuthorId == ana.Id, Token), count => count == 0, "the posts are erased");
        (await social.PostLikes.CountAsync(Token)).ShouldBe(0);
        (await social.Comments.CountAsync(Token)).ShouldBe(0);
        (await social.Reposts.CountAsync(Token)).ShouldBe(0);
        (await social.SavedPosts.CountAsync(Token)).ShouldBe(0);
        (await social.PostMentions.CountAsync(Token)).ShouldBe(0);
        (await PostIdsAsync(bea, "/api/v1/explore")).ShouldBeEmpty();
    }
}
