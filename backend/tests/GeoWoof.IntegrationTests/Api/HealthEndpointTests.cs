using System.Net;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;

namespace GeoWoof.IntegrationTests.Api;

public sealed class HealthEndpointTests(TestInfrastructure infrastructure)
{
    [Fact]
    public async Task Live_endpoint_reports_the_process_as_healthy()
    {
        await using var factory = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task Ready_endpoint_reports_every_dependency_once_they_are_up()
    {
        await using var factory = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var client = factory.CreateClient();

        // The storage bucket is created in the background right after startup.
        var statusCode = await Eventually.Until(
            async () =>
            {
                using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
                return response.StatusCode;
            },
            status => status == HttpStatusCode.OK,
            "the API is ready");

        statusCode.ShouldBe(HttpStatusCode.OK);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var checks = (await ReadJsonAsync(ready)).GetProperty("checks");
        foreach (var dependency in new[] { "postgres", "redis", "storage" })
        {
            checks.GetProperty(dependency).GetProperty("status").GetString().ShouldBe("Healthy");
        }
    }

    [Fact]
    public async Task Ready_endpoint_returns_503_when_a_dependency_is_unreachable()
    {
        await using var factory = await ApiFactory.WithFreshDatabaseAsync(infrastructure, new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,connectTimeout=500,asyncTimeout=500",
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var checks = (await ReadJsonAsync(response)).GetProperty("checks");
        checks.GetProperty("redis").GetProperty("status").GetString().ShouldBe("Unhealthy");
        checks.GetProperty("postgres").GetProperty("status").GetString().ShouldBe("Healthy");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("127.0.0.1");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        return document.RootElement.Clone();
    }
}
