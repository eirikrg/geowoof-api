using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Social.Infrastructure.Persistence;
using GeoWoof.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Social;

/// <summary>Replies to comments, in threads of one level: HTTP, handlers and PostgreSQL.</summary>
public sealed class CommentRepliesTests(TestInfrastructure infrastructure)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> DeleteAsync(TestUser user, string path) =>
        user.Client.DeleteAsync(new Uri(path, UriKind.Relative), Token);

    private static async Task<TestUser> VerifiedAsync(ApiFactory api, string username)
    {
        var user = await TestUser.RegisterAsync(api, $"{username}@example.com", username: username);
        await user.VerifyEmailAsync(api);
        return user;
    }

    private static async Task<Guid> PostAsync(ApiFactory api, TestUser author)
    {
        var photo = await MediaTestClient.UploadReadyAsync(api, author.Client, TestImages.Jpeg(400, 300), "PostImage");
        var id = Guid.CreateVersion7();
        using var created = await author.Client.PutJsonAsync(
            $"/api/v1/posts/{id}",
            new { text = "Un paseo", visibility = "Everyone", mediaAssetIds = new[] { photo.GetProperty("id").GetGuid() } });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return id;
    }

    private static async Task<JsonElement> CommentAsync(TestUser user, Guid postId, string text, Guid? parentId = null)
    {
        using var response = await user.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text, parentId });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.JsonAsync();
    }

    private static async Task<Guid> IdOfCommentAsync(TestUser user, Guid postId, string text, Guid? parentId = null) =>
        (await CommentAsync(user, postId, text, parentId)).GetProperty("id").GetGuid();

    private static Task<JsonElement> CommentsAsync(TestUser viewer, Guid postId, string query = "") =>
        viewer.Client.GetJsonAsync($"/api/v1/posts/{postId}/comments{query}");

    private static async Task<int> CommentCountAsync(TestUser viewer, Guid postId) =>
        (await viewer.Client.GetJsonAsync($"/api/v1/posts/{postId}")).GetProperty("commentCount").GetInt32();

    private static IEnumerable<string?> Texts(JsonElement list) =>
        list.EnumerateArray().Select(item => item.GetProperty("text").GetString());

    [Fact]
    public async Task A_reply_goes_to_the_thread_of_its_comment_also_when_it_answers_a_reply()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        var postId = await PostAsync(api, ana);
        var first = await IdOfCommentAsync(bea, postId, "Qué bonito");

        var reply = await CommentAsync(carlos, postId, "@bea sí", first);
        var answer = await CommentAsync(bea, postId, "@carlos gracias", reply.GetProperty("id").GetGuid());

        reply.GetProperty("parentId").GetGuid().ShouldBe(first);
        reply.GetProperty("mentions")[0].GetProperty("username").GetString().ShouldBe("bea");
        answer.GetProperty("parentId").GetGuid().ShouldBe(first);
        answer.GetProperty("replyCount").GetInt32().ShouldBe(0);
        answer.GetProperty("replies").GetArrayLength().ShouldBe(0);

        var comments = (await CommentsAsync(ana, postId)).GetProperty("items");
        Texts(comments).ShouldBe(["Qué bonito"]);
        comments[0].GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
        comments[0].GetProperty("replyCount").GetInt32().ShouldBe(2);
        comments[0].GetProperty("isDeleted").GetBoolean().ShouldBeFalse();
        Texts(comments[0].GetProperty("replies")).ShouldBe(["@bea sí", "@carlos gracias"]);
        comments[0].GetProperty("replies")[0].GetProperty("canDelete").GetBoolean().ShouldBeTrue();
        (await CommentCountAsync(ana, postId)).ShouldBe(3);
    }

    [Fact]
    public async Task A_comment_brings_its_first_two_replies_and_the_rest_come_page_by_page()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var postId = await PostAsync(api, ana);
        var first = await IdOfCommentAsync(bea, postId, "Primero");
        var other = await IdOfCommentAsync(bea, postId, "Otro");
        foreach (var text in new[] { "Uno", "Dos", "Tres" })
        {
            await IdOfCommentAsync(ana, postId, text, first);
        }

        var comments = (await CommentsAsync(bea, postId)).GetProperty("items");
        comments[0].GetProperty("replyCount").GetInt32().ShouldBe(3);
        Texts(comments[0].GetProperty("replies")).ShouldBe(["Uno", "Dos"]);
        comments[1].GetProperty("id").GetGuid().ShouldBe(other);
        comments[1].GetProperty("replyCount").GetInt32().ShouldBe(0);

        var page = await bea.Client.GetJsonAsync($"/api/v1/posts/{postId}/comments/{first}/replies?limit=2");
        Texts(page.GetProperty("items")).ShouldBe(["Uno", "Dos"]);
        var rest = await bea.Client.GetJsonAsync($"/api/v1/posts/{postId}/comments/{first}/replies?limit=2&cursor={page.GetProperty("nextCursor").GetString()}");
        Texts(rest.GetProperty("items")).ShouldBe(["Tres"]);
        rest.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var reply = comments[0].GetProperty("replies")[0].GetProperty("id").GetGuid();
        using var ofAReply = await bea.Client.GetAsync(new Uri($"/api/v1/posts/{postId}/comments/{reply}/replies", UriKind.Relative), Token);
        ofAReply.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(ofAReply)).ShouldBe("social.comment_not_found");
    }

    [Fact]
    public async Task A_reply_needs_a_comment_of_the_same_post()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        var postId = await PostAsync(api, ana);
        var otherPost = await PostAsync(api, ana);
        var elsewhere = await IdOfCommentAsync(bea, otherPost, "En otra");

        using var missing = await bea.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Hola", parentId = Guid.CreateVersion7() });
        using var ofAnotherPost = await bea.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Hola", parentId = elsewhere });

        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(missing)).ShouldBe("social.comment_not_found");
        ofAnotherPost.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(ofAnotherPost)).ShouldBe("social.comment_not_found");
        (await CommentCountAsync(ana, postId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_comment_with_replies_stays_as_deleted_until_its_last_reply_goes()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        var postId = await PostAsync(api, ana);
        var first = await IdOfCommentAsync(bea, postId, "Hola @ana");
        var reply = await IdOfCommentAsync(carlos, postId, "@bea hola", first);
        var lonely = await IdOfCommentAsync(bea, postId, "Sin respuestas");

        using var deleted = await DeleteAsync(bea, $"/api/v1/posts/{postId}/comments/{first}");
        using var alone = await DeleteAsync(ana, $"/api/v1/posts/{postId}/comments/{lonely}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        alone.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var comments = (await CommentsAsync(carlos, postId)).GetProperty("items");
        comments.GetArrayLength().ShouldBe(1);
        comments[0].GetProperty("id").GetGuid().ShouldBe(first);
        comments[0].GetProperty("isDeleted").GetBoolean().ShouldBeTrue();
        comments[0].GetProperty("text").GetString().ShouldBeEmpty();
        comments[0].GetProperty("mentions").GetArrayLength().ShouldBe(0);
        comments[0].GetProperty("canDelete").GetBoolean().ShouldBeFalse();
        Texts(comments[0].GetProperty("replies")).ShouldBe(["@bea hola"]);
        (await CommentCountAsync(ana, postId)).ShouldBe(1);

        using var again = await DeleteAsync(ana, $"/api/v1/posts/{postId}/comments/{first}");
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var report = await carlos.Client.PostJsonAsync("/api/v1/reports", new { targetType = "Comment", targetId = first, reason = "Spam" });
        report.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(report)).ShouldBe("social.comment_not_found");

        using var lastReply = await DeleteAsync(carlos, $"/api/v1/posts/{postId}/comments/{reply}");
        lastReply.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await CommentsAsync(carlos, postId)).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await CommentCountAsync(ana, postId)).ShouldBe(0);
        using var scope = api.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<SocialDbContext>().Comments.CountAsync(Token)).ShouldBe(0);
    }

    [Fact]
    public async Task A_block_hides_the_threads_of_that_person_also_in_the_numbers()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        using var dani = await VerifiedAsync(api, "dani");
        var postId = await PostAsync(api, ana);
        var ofBea = await IdOfCommentAsync(bea, postId, "Soy Bea");
        await IdOfCommentAsync(dani, postId, "Respondo a Bea", ofBea);
        var ofDani = await IdOfCommentAsync(dani, postId, "Soy Dani");
        await IdOfCommentAsync(bea, postId, "Respondo a Dani", ofDani);

        using var blocked = await carlos.Client.PutJsonAsync($"/api/v1/blocks/{bea.Id}", new { });
        blocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var comments = (await CommentsAsync(carlos, postId)).GetProperty("items");
        Texts(comments).ShouldBe(["Soy Dani"]);
        comments[0].GetProperty("replyCount").GetInt32().ShouldBe(0);
        comments[0].GetProperty("replies").GetArrayLength().ShouldBe(0);
        (await CommentCountAsync(carlos, postId)).ShouldBe(1);
        (await CommentCountAsync(ana, postId)).ShouldBe(4);

        using var replies = await carlos.Client.GetAsync(new Uri($"/api/v1/posts/{postId}/comments/{ofBea}/replies", UriKind.Relative), Token);
        using var reply = await carlos.Client.PostJsonAsync($"/api/v1/posts/{postId}/comments", new { text = "Hola", parentId = ofBea });
        replies.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        reply.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(reply)).ShouldBe("social.comment_not_found");
    }

    [Fact]
    public async Task Deleting_an_account_takes_its_threads_and_the_deleted_comments_that_only_its_replies_kept()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await VerifiedAsync(api, "ana");
        using var bea = await VerifiedAsync(api, "bea");
        using var carlos = await VerifiedAsync(api, "carlos");
        var postId = await PostAsync(api, ana);
        var ofBea = await IdOfCommentAsync(bea, postId, "Soy Bea");
        await IdOfCommentAsync(carlos, postId, "Respondo a Bea", ofBea);
        var ofCarlos = await IdOfCommentAsync(carlos, postId, "Soy Carlos");
        await IdOfCommentAsync(bea, postId, "Respondo a Carlos", ofCarlos);
        using var deletedByCarlos = await DeleteAsync(carlos, $"/api/v1/posts/{postId}/comments/{ofCarlos}");
        deletedByCarlos.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/me", UriKind.Relative))
        {
            Content = JsonContent.Create(new { password = TestUser.Password }),
        };
        using var deleted = await bea.Client.SendAsync(request, Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = api.Services.CreateScope();
        var social = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
        await Eventually.Until(() => social.Comments.CountAsync(Token), count => count == 0, "the threads of Bea and what only she kept are gone");
        (await CommentsAsync(ana, postId)).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await CommentCountAsync(ana, postId)).ShouldBe(0);
    }
}
