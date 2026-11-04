using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GeoWoof.BuildingBlocks.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Infrastructure;

/// <summary>A signed-in user of the API under test.</summary>
internal sealed record TestUser(HttpClient Client, Guid Id, string Email) : IDisposable
{
    public const string Password = "Password1";

    /// <summary>Registers an account and, unless told otherwise, finishes its onboarding.</summary>
    public static async Task<TestUser> RegisterAsync(ApiFactory api, string email, string? username = null, bool onboard = true)
    {
        using var anonymous = api.CreateClient();
        using var registered = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password = Password, displayName = "Ana" },
            TestContext.Current.CancellationToken);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var session = await registered.JsonAsync();

        var client = api.CreateAuthenticatedClient(session.GetProperty("accessToken").GetString()!);
        var user = new TestUser(client, session.GetProperty("user").GetProperty("id").GetGuid(), email);
        if (onboard)
        {
            using var chosen = await client.PutAsJsonAsync(
                "/api/v1/me/username",
                new { username = username ?? email.Split('@')[0] },
                TestContext.Current.CancellationToken);
            chosen.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return user;
    }

    public void Dispose() => Client.Dispose();
}

internal static class HttpTestExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.JsonAsync();
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string path, object body) =>
        client.PutAsJsonAsync(path, body, TestContext.Current.CancellationToken);
}

/// <summary>The client side of the image upload (docs/api.md, phase 2), with real signed links.</summary>
internal static class MediaTestClient
{
    /// <summary>Asks for a link and uploads the bytes to storage, without confirming.</summary>
    public static async Task<Guid> UploadAsync(ApiFactory api, HttpClient client, byte[] content, string purpose, string contentType = "image/jpeg", long? declaredSize = null)
    {
        await api.Services.GetRequiredService<StorageBucket>().EnsureReadyAsync(TestContext.Current.CancellationToken);

        using var requested = await client.PostJsonAsync("/api/v1/media/uploads", new { contentType, sizeBytes = declaredSize ?? content.Length, purpose });
        requested.StatusCode.ShouldBe(HttpStatusCode.Created);
        var upload = await requested.JsonAsync();

        // A client of its own: the signed link must not carry the API's Authorization header.
        using var storage = new HttpClient();
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = MediaTypeHeaderValue.Parse(upload.GetProperty("uploadHeaders").GetProperty("Content-Type").GetString()!);
        using var uploaded = await storage.PutAsync(new Uri(upload.GetProperty("uploadUrl").GetString()!), body, TestContext.Current.CancellationToken);
        uploaded.IsSuccessStatusCode.ShouldBeTrue($"Upload failed: {uploaded.StatusCode}");
        return upload.GetProperty("assetId").GetGuid();
    }

    public static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid assetId) =>
        client.PostAsync(new Uri($"/api/v1/media/uploads/{assetId}/complete", UriKind.Relative), null, TestContext.Current.CancellationToken);

    /// <summary>Uploads, confirms and waits for the background processor.</summary>
    public static async Task<JsonElement> UploadReadyAsync(ApiFactory api, HttpClient client, byte[] content, string purpose)
    {
        var assetId = await UploadAsync(api, client, content, purpose);
        using var completed = await CompleteAsync(client, assetId);
        completed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return await WaitForStatusAsync(client, assetId, "Ready");
    }

    public static Task<JsonElement> WaitForStatusAsync(HttpClient client, Guid assetId, string status) =>
        Eventually.Until(
            () => client.GetJsonAsync($"/api/v1/media/uploads/{assetId}"),
            asset => string.Equals(asset.GetProperty("status").GetString(), status, StringComparison.Ordinal),
            $"image {assetId} is {status}");

    public static async Task<byte[]> DownloadAsync(string url)
    {
        using var storage = new HttpClient();
        return await storage.GetByteArrayAsync(new Uri(url), TestContext.Current.CancellationToken);
    }
}
