using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Identity;

/// <summary>The phase 1 contract end to end: HTTP, handlers, PostgreSQL and the Outbox.</summary>
public sealed class IdentityEndpointsTests(TestInfrastructure infrastructure)
{
    private const string Password = "Password1";

    [Fact]
    public async Task Register_verify_and_onboard()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var anonymous = api.CreateClient();

        using var registered = await PostAsync(anonymous, "/api/v1/auth/register", new { email = "ana@example.com", password = Password, displayName = "Ana", language = "es" });
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var session = await JsonAsync(registered);
        session.GetProperty("isNewUser").GetBoolean().ShouldBeTrue();
        session.GetProperty("user").GetProperty("emailVerified").GetBoolean().ShouldBeFalse();
        session.GetProperty("user").GetProperty("username").ValueKind.ShouldBe(JsonValueKind.Null);

        using var client = api.CreateAuthenticatedClient(session.GetProperty("accessToken").GetString()!);
        using var verified = await PostAsync(client, "/api/v1/auth/email/verify", new { code = api.Emails.LastCodeFor("ana@example.com") });
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        var verifiedBody = await JsonAsync(verified);
        verifiedBody.GetProperty("user").GetProperty("emailVerified").GetBoolean().ShouldBeTrue();

        using var onboardedClient = api.CreateAuthenticatedClient(verifiedBody.GetProperty("accessToken").GetString()!);
        using var availability = await onboardedClient.GetAsync(new Uri("/api/v1/usernames/availability?username=Ana.Paws", UriKind.Relative), TestContext.Current.CancellationToken);
        (await JsonAsync(availability)).GetProperty("available").GetBoolean().ShouldBeTrue();

        using var username = await onboardedClient.PutAsJsonAsync("/api/v1/me/username", new { username = "Ana.Paws" }, TestContext.Current.CancellationToken);
        username.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var me = await onboardedClient.GetAsync(new Uri("/api/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);
        var meBody = await JsonAsync(me);
        meBody.GetProperty("username").GetString().ShouldBe("ana.paws");
        meBody.GetProperty("signInMethods")[0].GetString().ShouldBe("Password");
    }

    [Fact]
    public async Task Protected_endpoints_require_a_token()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var anonymous = api.CreateClient();

        using var response = await anonymous.GetAsync(new Uri("/api/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe("auth.unauthorized");
    }

    [Fact]
    public async Task Duplicate_emails_and_wrong_passwords_are_reported_with_stable_codes()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var anonymous = api.CreateClient();
        await RegisterAsync(anonymous, "ana@example.com");

        using var duplicate = await PostAsync(anonymous, "/api/v1/auth/register", new { email = "ANA@example.com", password = Password, displayName = "Ana" });
        using var wrongPassword = await PostAsync(anonymous, "/api/v1/auth/login", new { email = "ana@example.com", password = "Wrong1234" });
        using var invalid = await PostAsync(anonymous, "/api/v1/auth/register", new { email = "not-an-email", password = "x", displayName = "" });

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(duplicate)).GetProperty("code").GetString().ShouldBe("auth.email_taken");
        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonAsync(wrongPassword)).GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await JsonAsync(invalid)).GetProperty("errors");
        errors.TryGetProperty("email", out _).ShouldBeTrue();
        errors.TryGetProperty("password", out _).ShouldBeTrue();
        errors.TryGetProperty("displayName", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_a_replayed_token_revokes_the_session()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>
        {
            ["Auth:RefreshTokenReuseGracePeriod"] = "00:00:00",
        });
        using var anonymous = api.CreateClient();
        var session = await RegisterAsync(anonymous, "ana@example.com");
        var original = session.GetProperty("refreshToken").GetString();

        using var rotated = await PostAsync(anonymous, "/api/v1/auth/refresh", new { refreshToken = original });
        var newToken = (await JsonAsync(rotated)).GetProperty("refreshToken").GetString();
        using var replayed = await PostAsync(anonymous, "/api/v1/auth/refresh", new { refreshToken = original });
        using var afterReplay = await PostAsync(anonymous, "/api/v1/auth/refresh", new { refreshToken = newToken });

        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        newToken.ShouldNotBe(original);
        (await JsonAsync(replayed)).GetProperty("code").GetString().ShouldBe("auth.refresh_token_reused");
        (await JsonAsync(afterReplay)).GetProperty("code").GetString().ShouldBe("auth.refresh_token_invalid");
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var anonymous = api.CreateClient();
        var session = await RegisterAsync(anonymous, "ana@example.com");
        using var client = api.CreateAuthenticatedClient(session.GetProperty("accessToken").GetString()!);

        using var logout = await PostAsync(client, "/api/v1/auth/logout", new { refreshToken = session.GetProperty("refreshToken").GetString() });
        using var refresh = await PostAsync(anonymous, "/api/v1/auth/refresh", new { refreshToken = session.GetProperty("refreshToken").GetString() });

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Lockout_and_rate_limit_responses_tell_the_client_when_to_retry()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>
        {
            ["Auth:MaxFailedSignInAttempts"] = "2",
        });
        using var anonymous = api.CreateClient();
        await RegisterAsync(anonymous, "ana@example.com");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var _ = await PostAsync(anonymous, "/api/v1/auth/login", new { email = "ana@example.com", password = "Wrong1234" });
        }

        using var locked = await PostAsync(anonymous, "/api/v1/auth/login", new { email = "ana@example.com", password = Password });

        locked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await JsonAsync(locked)).GetProperty("code").GetString().ShouldBe("auth.locked_out");
        locked.Headers.RetryAfter.ShouldNotBeNull();

        await using var throttled = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>
        {
            ["Api:AuthPermitsPerMinute"] = "2",
        });
        using var throttledClient = throttled.CreateClient();
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last?.Dispose();
            last = await PostAsync(throttledClient, "/api/v1/auth/login", new { email = "nobody@example.com", password = Password });
        }

        last!.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await JsonAsync(last)).GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
        last.Dispose();
    }

    [Fact]
    public async Task Google_sign_in_is_off_until_configured_and_then_creates_the_account()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var anonymous = api.CreateClient();

        using var providersOff = await anonymous.GetAsync(new Uri("/api/v1/auth/providers", UriKind.Relative), TestContext.Current.CancellationToken);
        using var disabled = await PostAsync(anonymous, "/api/v1/auth/google", new { idToken = "token" });
        api.Google.Accept("token", new GoogleIdentity("subject-1", "bea@example.com", EmailVerified: true, "Bea"));
        using var signedIn = await PostAsync(anonymous, "/api/v1/auth/google", new { idToken = "token" });

        (await JsonAsync(providersOff)).GetProperty("google").GetBoolean().ShouldBeFalse();
        disabled.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await JsonAsync(disabled)).GetProperty("code").GetString().ShouldBe("auth.google_disabled");
        var session = await JsonAsync(signedIn);
        session.GetProperty("isNewUser").GetBoolean().ShouldBeTrue();
        session.GetProperty("user").GetProperty("signInMethods")[0].GetString().ShouldBe("Google");
    }

    [Fact]
    public async Task Password_reset_with_the_emailed_code()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var anonymous = api.CreateClient();
        await RegisterAsync(anonymous, "ana@example.com");

        using var forgot = await PostAsync(anonymous, "/api/v1/auth/password/forgot", new { email = "ana@example.com" });
        using var forgotUnknown = await PostAsync(anonymous, "/api/v1/auth/password/forgot", new { email = "nobody@example.com" });
        using var reset = await PostAsync(anonymous, "/api/v1/auth/password/reset", new
        {
            email = "ana@example.com",
            code = api.Emails.LastCodeFor("ana@example.com"),
            newPassword = "NewPassword9",
        });
        using var login = await PostAsync(anonymous, "/api/v1/auth/login", new { email = "ana@example.com", password = "NewPassword9" });

        forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        forgotUnknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deleting_the_account_publishes_user_deleted_through_the_outbox()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var anonymous = api.CreateClient();
        var session = await RegisterAsync(anonymous, "ana@example.com");
        using var client = api.CreateAuthenticatedClient(session.GetProperty("accessToken").GetString()!);

        using var wrong = await SendAsync(client, HttpMethod.Delete, "/api/v1/me", new { password = "Wrong1234" });
        using var deleted = await SendAsync(client, HttpMethod.Delete, "/api/v1/me", new { password = Password });
        using var me = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        (await JsonAsync(wrong)).GetProperty("code").GetString().ShouldBe("auth.password_incorrect");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var scope = api.Services.CreateAsyncScope();
        var outbox = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().OutboxMessages
            .AsNoTracking()
            .Select(message => message.Type)
            .ToListAsync(TestContext.Current.CancellationToken);
        outbox.ShouldContain("identity.user_deleted.v1");
    }

    private static async Task<JsonElement> RegisterAsync(HttpClient client, string email)
    {
        using var response = await PostAsync(client, "/api/v1/auth/register", new { email, password = Password, displayName = "Ana" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await JsonAsync(response);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) =>
        SendAsync(client, HttpMethod.Post, path, body);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body) =>
        client.SendAsync(
            new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) },
            TestContext.Current.CancellationToken);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
