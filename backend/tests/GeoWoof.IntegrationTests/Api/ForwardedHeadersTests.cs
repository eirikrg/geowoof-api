using System.Net;
using System.Net.Http.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Api;

public sealed class ForwardedHeadersTests(TestInfrastructure infrastructure)
{
    private const int PermitsPerMinute = 2;

    [Fact]
    public async Task The_proxy_on_this_machine_says_who_the_client_is()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, ThrottledLogin);
        await using var behindProxy = api.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new ConnectionFrom(IPAddress.Loopback))));
        using var client = behindProxy.CreateClient();

        var first = await LoginAttemptsAsync(client, "203.0.113.1", PermitsPerMinute + 1);
        var second = await LoginAttemptsAsync(client, "203.0.113.2", 1);

        first[^1].ShouldBe(HttpStatusCode.TooManyRequests);
        second[^1].ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Anybody_else_cannot_choose_their_own_address()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure, ThrottledLogin);
        await using var direct = api.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new ConnectionFrom(IPAddress.Parse("198.51.100.7")))));
        using var client = direct.CreateClient();

        await LoginAttemptsAsync(client, "203.0.113.1", PermitsPerMinute);
        var withAnotherAddress = await LoginAttemptsAsync(client, "203.0.113.2", 1);

        withAnotherAddress[^1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private static Dictionary<string, string?> ThrottledLogin => new(StringComparer.Ordinal)
    {
        ["Api:AuthPermitsPerMinute"] = PermitsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async Task<List<HttpStatusCode>> LoginAttemptsAsync(HttpClient client, string forwardedFor, int attempts)
    {
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/auth/login", UriKind.Relative))
            {
                Content = JsonContent.Create(new { email = "nobody@example.com", password = TestUser.Password }),
            };
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    /// <summary>Makes every request of a test server come from an address, as a real connection would.</summary>
    private sealed class ConnectionFrom(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
